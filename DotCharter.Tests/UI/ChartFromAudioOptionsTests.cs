using DotCharter.Analysis.AudioToChart;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Import.Audio;

namespace DotCharter.Tests.UI;

/// <summary>
///     Tools → Chart from audio stamps the ACTIVE arrangement's tuning, string
///     count and capo onto the draft it produces, so the pitch→fret map it
///     analyses with has to come from those same three. It used to come from the
///     arrangement TYPE alone — standard guitar or standard bass — and every way
///     that goes wrong is silent.
/// </summary>
public sealed class ChartFromAudioOptionsTests
{
    private static Arrangement Guitar(int[]? tuning = null, int capo = 0, int strings = 6,
        ArrangementType type = ArrangementType.Lead)
    {
        var arr = new Arrangement { ArrangementType = type, StringCount = strings, Capo = capo };
        foreach (var step in tuning ?? new int[strings]) arr.TuningPitches.Add(new TuningOffset(step));
        return arr;
    }

    /// <summary>The invariant in one line: the map's entry for a string must be
    /// the pitch that string's OPEN (un-capoed) fret really sounds, as the rest
    /// of the app derives it.</summary>
    private static void AssertAgreesWithTheSoundingDerivation(Arrangement arr)
    {
        var map = ChartFromAudioOptions.OpenStringMidiFor(arr);
        Assert.Equal(arr.StringCount, map.Count);
        for (var s = 0; s < map.Count; s++)
            Assert.Equal(LivePitchAcceptance.SoundingMidi(arr, s, 0)!.Value - arr.Capo, map[s]);
    }

    [Fact]
    public void StandardTuning_IsTheStandardMap_SoNothingMovesOnAnOrdinaryChart()
    {
        var map = ChartFromAudioOptions.OpenStringMidiFor(Guitar());
        Assert.Equal(AudioToChartOptions.StandardGuitarMidi, map);
        Assert.Equal(0, ChartFromAudioOptions.MinFretFor(Guitar()));
    }

    [Fact]
    public void DropC_FollowsTheTuning_InsteadOfSpellingEveryNoteAtItsStandardFret()
    {
        // Drop C: -4, -2, -2, -2, -2, -2 against E standard.
        var arr = Guitar([-4, -2, -2, -2, -2, -2]);

        var map = ChartFromAudioOptions.OpenStringMidiFor(arr);

        Assert.Equal([36, 43, 48, 53, 57, 62], map);
        AssertAgreesWithTheSoundingDerivation(arr);

        // C3 (48) is the OPEN third string here. Against the standard map it
        // would have been written as string 0 / fret 8 — which in this
        // arrangement sounds MIDI 44, four semitones flat.
        Assert.Equal(0, 48 - map[2]);
        Assert.Equal(8, 48 - AudioToChartOptions.StandardGuitarMidi[0]);
    }

    [Fact]
    public void ACapo_FloorsTheFretSearchSoNoSubCapoFretIsEverMinted()
    {
        var arr = Guitar(capo: 2);

        var map = ChartFromAudioOptions.OpenStringMidiFor(arr);
        var minFret = ChartFromAudioOptions.MinFretFor(arr);

        // The map is the UN-capoed neck (a fret is absolute); the floor is what
        // the capo contributes.
        Assert.Equal(AudioToChartOptions.StandardGuitarMidi, map);
        Assert.Equal(2, minFret);
        AssertAgreesWithTheSoundingDerivation(arr);

        // F#2 = 42 is what the capo'd low string sounds open. It resolves to
        // fret 2 — legal, sounds right (max(fret, capo)) — while 41 and 40 are
        // unreachable rather than being written as frets 1 and 0.
        Assert.Equal(2, 42 - map[0]);
        Assert.True(41 - map[0] < minFret);
    }

    [Theory]
    [InlineData(7, ArrangementType.Lead)]
    [InlineData(5, ArrangementType.Bass)]
    public void AnExtraString_GetsAnEntry_InsteadOfBeingUnreachable(int strings, ArrangementType type)
    {
        var arr = Guitar(new int[strings], strings: strings, type: type);

        var map = ChartFromAudioOptions.OpenStringMidiFor(arr);

        Assert.Equal(strings, map.Count);
        Assert.Equal(strings, map.Distinct().Count());
        AssertAgreesWithTheSoundingDerivation(arr);
    }

    /// <summary>
    ///     The end-to-end claim: run the analyzer with the derived options and
    ///     the note it writes must sound back as the pitch that was detected.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void EveryDetectedPitch_SoundsBackAsItselfOnADropTunedCapoedArrangement(int capo)
    {
        var arr = Guitar([-4, -2, -2, -2, -2, -2], capo: capo);
        var options = new AudioToChartOptions
        {
            MaxNotesPerOnset = 1,
            MinConfidence = 0f,
            OpenStringMidi = ChartFromAudioOptions.OpenStringMidiFor(arr),
            MinFret = ChartFromAudioOptions.MinFretFor(arr),
        };
        var beats = Enumerable.Range(0, 40)
            .Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })
            .ToList();

        // One onset per semitone across the arrangement's real range.
        var events = Enumerable.Range(0, 24)
            .Select(i => new GuitarAudioEvent(0.5 + i * 0.5, false, 1f,
                [new GuitarPitchCandidate(48 + i, 130f, 1f)]))
            .ToArray();
        var draft = new AudioToChartAnalyzer(rate => new ReplayDetector(rate, events))
            .Analyze(new float[16000 * 14], 16000, beats, options);

        Assert.Equal(events.Length, draft.Notes.Count);
        foreach (var note in draft.Notes)
        {
            Assert.True(note.Note.Fret == 0 || note.Note.Fret >= capo,
                $"fret {note.Note.Fret} is under the capo ({capo}) — inspector rule L01.");
            Assert.Equal(note.Midi, LivePitchAcceptance.SoundingMidi(arr, note.Note.StringIndex, note.Note.Fret));
        }
    }

    /// <summary>Replays a fixed event list once the pushed sample count reaches
    /// each one's timestamp — the analyzer's own internal detector seam.</summary>
    private sealed class ReplayDetector : IGuitarEventDetector
    {
        private readonly GuitarAudioEvent[] _events;
        private long _pushed;
        private int _next;

        public ReplayDetector(int sampleRate, GuitarAudioEvent[] events)
        {
            SampleRate = sampleRate;
            _events = events;
        }

        public int SampleRate { get; }

        public IReadOnlyList<GuitarAudioEvent> Push(ReadOnlySpan<float> samples)
        {
            _pushed += samples.Length;
            var emitted = new List<GuitarAudioEvent>();
            while (_next < _events.Length && _events[_next].TimeSeconds * SampleRate <= _pushed)
                emitted.Add(_events[_next++]);
            return emitted;
        }

        public void Reset()
        {
            _pushed = 0;
            _next = 0;
        }
    }

    /// <summary>
    ///     #391: the dialog drafts with the constant-Q pitch stage, and a bass onset
    ///     becomes at most TWO notes (it was three, for a part charted as single notes)
    ///     while a guitar onset keeps three.
    /// </summary>
    [Theory]
    [InlineData(ArrangementType.Bass, 2)]
    [InlineData(ArrangementType.Lead, 3)]
    [InlineData(ArrangementType.Rhythm, 3)]
    [InlineData(ArrangementType.Combo, 3)]
    public void TheDialogsOptions_UseTheCqtPitchStage_AndFewerNotesPerBassOnset(ArrangementType type, int perOnset)
    {
        var options = ChartFromAudioAnalyzer.OptionsFor(Guitar(type: type, strings: type == ArrangementType.Bass ? 4 : 6),
            ChartFromAudioAnalyzer.DefaultSensitivity);

        Assert.Equal(perOnset, options.MaxNotesPerOnset);
        Assert.Equal(AudioToChartPitchStage.ConstantQ, options.PitchStage);
        Assert.Equal(ChartFromAudioAnalyzer.GuitarMaxNotesPerOnset, ChartFromAudioAnalyzer.MaxNotesPerOnsetFor(null));
    }
}
