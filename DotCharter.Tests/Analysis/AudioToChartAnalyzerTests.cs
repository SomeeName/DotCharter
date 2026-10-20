using DotCharter.Analysis.AudioToChart;

namespace DotCharter.Tests.Analysis;

public sealed class AudioToChartAnalyzerTests
{
    private const int Rate = AnalysisTestSignals.SampleRate;

    private static readonly AudioToChartOptions Monophonic = new() { MaxNotesPerOnset = 1 };

    /// <summary>Three open-string plucks at 120 BPM: A2 on beat 1, D3 on
    /// beat 2, G3 on the off-beat 3½.</summary>
    private static float[] ThreePluckFixture()
    {
        var buffer = new float[(int)(3.0 * Rate)];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 110.00); // A2, midi 45
        AnalysisTestSignals.AddPluck(buffer, 1.00, 146.83); // D3, midi 50
        AnalysisTestSignals.AddPluck(buffer, 1.75, 196.00); // G3, midi 55
        return buffer;
    }

    [Fact]
    public void Analyze_Plucks_DetectsPitchesOnBeatGrid()
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var draft = new AudioToChartAnalyzer().Analyze(ThreePluckFixture(), Rate, beats, Monophonic);

        Assert.Equal(3, draft.Notes.Count);

        var expected = new[]
        {
            (Time: 0.50, Midi: 45, StringIndex: 1, Fret: 0, Beats: 1.0),
            (Time: 1.00, Midi: 50, StringIndex: 2, Fret: 0, Beats: 2.0),
            (Time: 1.75, Midi: 55, StringIndex: 3, Fret: 0, Beats: 3.5)
        };

        for (var i = 0; i < expected.Length; i++)
        {
            var note = draft.Notes[i];
            Assert.Equal(expected[i].Midi, note.Midi);
            Assert.Equal(expected[i].StringIndex, note.Note.StringIndex);
            Assert.Equal(expected[i].Fret, note.Note.Fret);
            Assert.InRange(note.TimeSeconds, expected[i].Time - 0.03, expected[i].Time + 0.03);
            Assert.InRange(note.Note.Position.DoubleValue, expected[i].Beats - 1.0 / 12,
                expected[i].Beats + 1.0 / 12);
            Assert.InRange(note.Confidence, 0.10f, 1.0f);
        }
    }

    [Fact]
    public void Analyze_FrettedNote_PrefersPositionNearHand()
    {
        var buffer = new float[(int)(2.0 * Rate)];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 220.0); // A3, midi 57
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);

        var draft = new AudioToChartAnalyzer().Analyze(buffer, Rate, beats, Monophonic);

        var note = Assert.Single(draft.Notes);
        Assert.Equal(57, note.Midi);
        // G string 2nd fret (cost 1 from neutral position 3) beats D string 7th.
        Assert.Equal(3, note.Note.StringIndex);
        Assert.Equal(2, note.Note.Fret);
    }

    [Fact]
    public void Analyze_IsDeterministic_AndChunkBoundaryIndependent()
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var analyzer = new AudioToChartAnalyzer();
        var fixture = ThreePluckFixture();

        var first = analyzer.Analyze(fixture, Rate, beats, Monophonic);
        var second = analyzer.Analyze(fixture, Rate, beats, Monophonic);
        var oddChunks = analyzer.Analyze(SliceOdd(fixture), Rate, beats, Monophonic);

        AssertSameDraft(first, second);
        AssertSameDraft(first, oddChunks);
    }

    [Fact]
    public void Analyze_Silence_ProducesNoNotes()
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var draft = new AudioToChartAnalyzer().Analyze(new float[Rate], Rate, beats);
        Assert.Empty(draft.Notes);
    }

    [Fact]
    public void BuildDraft_FakeDetector_MapsLegatoAndConfidence()
    {
        // A fake detector isolates the note-mapping stage from the DSP.
        var events = new[]
        {
            new GuitarAudioEvent(0.5, false, 0.8f,
                new[] { new GuitarPitchCandidate(45, 110f, 1f) }),
            new GuitarAudioEvent(1.0, true, 0.8f,
                new[] { new GuitarPitchCandidate(47, 123.47f, 1f) }), // legato up on same string
            new GuitarAudioEvent(1.5, false, 0.01f,
                new[] { new GuitarPitchCandidate(50, 146.83f, 1f) }) // too weak: dropped
        };
        var analyzer = new AudioToChartAnalyzer(rate => new FakeDetector(rate, events));
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);

        var draft = analyzer.Analyze(new float[Rate * 2], Rate, beats);

        Assert.Equal(2, draft.Notes.Count);

        var picked = draft.Notes[0];
        Assert.Equal(new FractionalPosition(1), picked.Note.Position);
        Assert.Equal(HopoType.None, picked.Note.Hopo);

        var legato = draft.Notes[1];
        Assert.Equal(new FractionalPosition(2), legato.Note.Position);
        Assert.Equal(picked.Note.StringIndex, legato.Note.StringIndex);
        Assert.Equal(HopoType.HammerOn, legato.Note.Hopo);
        Assert.True(legato.Confidence < picked.Confidence); // legato factor applied
    }

    /// <summary>
    ///     A beat map that stops before the audio does is ordinary — recomputing
    ///     beat TIMES for a new tempo never ADDS beats, so a grid generated at
    ///     100 BPM covers well under two thirds of the song once the charter sets
    ///     160. Every event past its end used to land on one and the same
    ///     position, because the hand-rolled snap clamped the beat fraction into
    ///     [0, 1].
    /// </summary>
    [Fact]
    public void AnalyzeMapsAnEventPastTheLastBeat_ToItsExtrapolatedPosition_NotOntoTheGridEnd()
    {
        // 8 beats at 120 BPM: the grid ends at beat 7 = 3,500 ms.
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var events = new[]
        {
            PastGridEvent(4.0), // beat 8
            PastGridEvent(5.0), // beat 10
            PastGridEvent(6.0), // beat 12
        };
        var analyzer = new AudioToChartAnalyzer(rate => new FakeDetector(rate, events));

        var draft = analyzer.Analyze(new float[Rate * 7], Rate, beats, Monophonic);

        Assert.Equal(3, draft.Notes.Count);
        Assert.Equal(new FractionalPosition(8), draft.Notes[0].Note.Position);
        Assert.Equal(new FractionalPosition(10), draft.Notes[1].Note.Position);
        Assert.Equal(new FractionalPosition(12), draft.Notes[2].Note.Position);
    }

    /// <summary>
    ///     The other half of the same defect, and the reason those notes SURVIVE:
    ///     a clamped fraction sits exactly on a grid slot, so the grid error came
    ///     out 0 and the events past the grid were scored at the highest possible
    ///     confidence — above every honestly-placed note inside it.
    /// </summary>
    [Fact]
    public void AnEventOffTheGridPastTheLastBeat_IsPenalisedExactlyLikeOneInsideIt()
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        // 12 slots per beat at 120 BPM ⇒ 41.67 ms each; +1/48 s is half a slot,
        // the worst snap there is.
        const double halfSlot = 1.0 / 48.0;
        var events = new[]
        {
            PastGridEvent(1.0 + halfSlot), // inside the grid
            PastGridEvent(4.0 + halfSlot), // past its end
        };
        var analyzer = new AudioToChartAnalyzer(rate => new FakeDetector(rate, events));

        var draft = analyzer.Analyze(new float[Rate * 6], Rate, beats, Monophonic);

        Assert.Equal(2, draft.Notes.Count);
        var inside = draft.Notes[0].Confidence;
        var past = draft.Notes[1].Confidence;
        Assert.Equal(inside, past, 5);
        Assert.True(past < 0.99f, $"a half-slot snap past the grid paid no penalty (confidence {past}).");
    }

    /// <summary>
    ///     A detector that hands back a non-finite number must not be able to mint
    ///     a note. NaN loses every comparison, so <c>confidence &lt; MinConfidence</c>
    ///     is FALSE for it and the candidate walks through the one gate meant to
    ///     drop it; an infinite Strength is worse, because the confidence clamp
    ///     turns it into 1.0 — the highest score in the draft. Both outrank every
    ///     honestly-scored note, which is the same shape as the clamped-grid
    ///     defect two tests up.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, 1f, 1f)]
    [InlineData(float.PositiveInfinity, 1f, 1f)]
    [InlineData(1f, float.NaN, 1f)]
    [InlineData(1f, float.PositiveInfinity, 1f)]
    [InlineData(1f, 1f, float.NaN)]
    public void ANonFiniteDetectorNumber_MintsNoNote(float strength, float salience, float frequencyHz)
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var events = new[]
        {
            new GuitarAudioEvent(1.0, false, strength,
                [new GuitarPitchCandidate(45, frequencyHz, salience)])
        };
        var analyzer = new AudioToChartAnalyzer(rate => new FakeDetector(rate, events));

        var draft = analyzer.Analyze(new float[Rate * 2], Rate, beats, Monophonic);

        Assert.Empty(draft.Notes);
    }

    /// <summary>The event's own timestamp is the other door: a non-finite time
    /// snaps to beat 0 with a grid error of 0 — i.e. no penalty at all — and
    /// parks a maximum-confidence note at the head of the chart.</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonFiniteEventTime_MintsNoNote(double timeSeconds)
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        // ImmediateDetector, not FakeDetector: the latter releases an event when
        // `TimeSeconds * SampleRate <= pushed`, which is FALSE for NaN and for
        // +inf — so it would never emit and the test would pass having asserted
        // nothing at all.
        var analyzer = new AudioToChartAnalyzer(
            rate => new ImmediateDetector(rate, [PastGridEvent(timeSeconds)]));

        var draft = analyzer.Analyze(new float[Rate * 2], Rate, beats, Monophonic);

        Assert.Empty(draft.Notes);
    }

    /// <summary>A grid denominator of 0 reached <c>Fraction</c>'s ctor as a
    /// <see cref="DivideByZeroException" /> thrown out of a primitive with no
    /// zero in it; a negative one is worse, because Fraction normalizes the sign
    /// and every note lands silently at its MIRROR position.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void ANonPositiveGridDenominator_IsRejectedAtTheEntryPoint(int denominator)
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var analyzer = new AudioToChartAnalyzer(rate => new FakeDetector(rate, [PastGridEvent(1.0)]));

        Assert.Throws<ArgumentException>(() => analyzer.Analyze(
            new float[Rate], Rate, beats, new AudioToChartOptions { GridDenominator = denominator }));
    }

    /// <summary>
    ///     An empty beat grid is rejected where it ENTERS, next to the other two
    ///     malformed-options guards. It is not a harmless "no grid ⇒ no draft":
    ///     <c>TimelineMath.TryBeatOffsetFromMs</c> fails for every event,
    ///     <c>SnapToGrid</c>'s failure arm answers <c>(FractionalPosition.Zero,
    ///     0.0)</c>, and a grid error of 0 is the HIGHEST confidence the shaping
    ///     can award — so the whole song used to pile onto beat 0, one note per
    ///     string, each of them outranking every honestly-placed note. Exactly
    ///     the collapse the clamped beat fraction produced, through the other door.
    /// </summary>
    [Fact]
    public void AnEmptyBeatGrid_IsRejectedAtTheEntryPoint()
    {
        var analyzer = new AudioToChartAnalyzer(rate =>
            new FakeDetector(rate, [PastGridEvent(0.10), PastGridEvent(0.40), PastGridEvent(0.70)]));

        var ex = Assert.Throws<ArgumentException>(() =>
            analyzer.Analyze(new float[Rate], Rate, []));
        Assert.Equal("beats", ex.ParamName);
    }

    /// <summary>The guard is on the GRID, not on "some beats": one beat is a
    /// legal grid (<c>TryBeatOffsetFromMs</c> extrapolates off it), so it must
    /// still draft.</summary>
    [Fact]
    public void ASingleBeat_IsStillAUsableGrid()
    {
        var analyzer = new AudioToChartAnalyzer(rate => new FakeDetector(rate, [PastGridEvent(0.50)]));

        var draft = analyzer.Analyze(new float[Rate], Rate,
            [new() { Time = 0.0 }]);

        Assert.Single(draft.Notes);
    }

    // ---- one grid slot, one string -------------------------------------

    /// <summary>
    ///     The used-string mask belongs to the grid SLOT, not to the event. At
    ///     120 BPM with <c>GridDenominator = 12</c> a slot is ~41.7 ms wide, so
    ///     two onsets 20 ms apart snap to the SAME <c>FractionalPosition</c> —
    ///     and with a per-event mask each of them took string 1, producing two
    ///     DraftNotes at one position AND one string. That is the shape the GP
    ///     path calls physically impossible and scrubs with
    ///     <c>GpPostProcessor.RemoveDuplicateSounds</c>.
    /// </summary>
    [Fact]
    public void TwoOnsetsInOneGridSlot_NeverShareAStringAtOnePosition()
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var events = new[]
        {
            new GuitarAudioEvent(1.000, false, 0.90f, [new GuitarPitchCandidate(45, 110f, 1f)]),
            new GuitarAudioEvent(1.020, false, 0.80f, [new GuitarPitchCandidate(45, 110f, 1f)])
        };
        var analyzer = new AudioToChartAnalyzer(rate => new FakeDetector(rate, events));

        var draft = analyzer.Analyze(new float[Rate * 2], Rate, beats, Monophonic);

        // The collision really happened — both notes are on ONE position. Without
        // this the distinctness assertion below would pass vacuously the moment
        // the snap stopped colliding.
        Assert.Equal(2, draft.Notes.Count);
        Assert.Single(draft.Notes.Select(n => n.Note.Position).Distinct());

        var slots = draft.Notes.Select(n => (n.Note.Position, n.Note.StringIndex)).ToList();
        Assert.Equal(slots.Count, slots.Distinct().Count());
    }

    /// <summary>
    ///     …and when the slot's only usable string is already taken, the higher
    ///     confidence wins rather than the arrival order. One open string makes
    ///     that the only outcome available.
    /// </summary>
    [Fact]
    public void WhenTheOnlyStringIsTaken_TheHigherConfidenceCandidateKeepsTheSlot()
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var oneString = new AudioToChartOptions
        {
            MaxNotesPerOnset = 1,
            OpenStringMidi = new[] { 45 }
        };
        var events = new[]
        {
            new GuitarAudioEvent(1.000, false, 0.30f, [new GuitarPitchCandidate(45, 110f, 1f)]),
            new GuitarAudioEvent(1.020, false, 0.90f, [new GuitarPitchCandidate(45, 110f, 1f)])
        };
        var analyzer = new AudioToChartAnalyzer(rate => new FakeDetector(rate, events));

        var draft = analyzer.Analyze(new float[Rate * 2], Rate, beats, oneString);

        var note = Assert.Single(draft.Notes);
        // The second event's score — 0.9 shaded by its off-grid penalty — which
        // is still comfortably above the first event's un-penalized 0.30.
        Assert.True(note.Confidence > 0.30f,
            $"the louder second onset should own the slot, but the kept note scores {note.Confidence}");
        Assert.Equal(1.020, note.TimeSeconds, 6);
    }

    /// <summary>
    ///     The corpus score (<c>AutoChartCorpusProbeTests</c>) detects a song once
    ///     and drafts every arrangement from the same events, so the split it uses
    ///     must BE <c>Analyze</c> — or the published number describes a pipeline
    ///     the dialog does not run. Real detector, three plucks, one fret map that
    ///     is not the default (Drop D, capo 2) so the options visibly reach the draft.
    /// </summary>
    [Fact]
    public void DetectThenDraft_IsTheSameDraftAsAnalyze()
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var options = new AudioToChartOptions { OpenStringMidi = [38, 45, 50, 55, 59, 64], MinFret = 2 };
        var analyzer = new AudioToChartAnalyzer();
        var fixture = ThreePluckFixture();

        var whole = analyzer.Analyze(fixture, Rate, beats, options);
        var split = AudioToChartAnalyzer.DraftFromEvents(analyzer.DetectEvents(fixture, Rate), beats, options);

        Assert.NotEmpty(whole.Notes);
        AssertSameDraft(whole, split);
    }

    private static GuitarAudioEvent PastGridEvent(double timeSeconds) =>
        new(timeSeconds, false, 1f, [new GuitarPitchCandidate(45, 110f, 1f)]);

    private static IEnumerable<ReadOnlyMemory<float>> SliceOdd(float[] samples)
    {
        var memory = samples.AsMemory();
        var offset = 0;
        while (offset < memory.Length)
        {
            var n = Math.Min(777, memory.Length - offset);
            yield return memory.Slice(offset, n);
            offset += n;
        }
    }

    private static void AssertSameDraft(AudioToChartDraft expected, AudioToChartDraft actual)
    {
        Assert.Equal(expected.Notes.Count, actual.Notes.Count);
        for (var i = 0; i < expected.Notes.Count; i++)
        {
            Assert.Equal(expected.Notes[i].TimeSeconds, actual.Notes[i].TimeSeconds);
            Assert.Equal(expected.Notes[i].Midi, actual.Notes[i].Midi);
            Assert.Equal(expected.Notes[i].Confidence, actual.Notes[i].Confidence);
            Assert.Equal(expected.Notes[i].Note.Position, actual.Notes[i].Note.Position);
            Assert.Equal(expected.Notes[i].Note.StringIndex, actual.Notes[i].Note.StringIndex);
            Assert.Equal(expected.Notes[i].Note.Fret, actual.Notes[i].Note.Fret);
        }
    }

    /// <summary>Releases every event on the first push, whatever its timestamp
    /// says — the seam <see cref="FakeDetector" /> cannot provide for a
    /// non-finite time.</summary>
    private sealed class ImmediateDetector : IGuitarEventDetector
    {
        private readonly GuitarAudioEvent[] _events;
        private bool _emitted;

        public ImmediateDetector(int sampleRate, GuitarAudioEvent[] events)
        {
            SampleRate = sampleRate;
            _events = events;
        }

        public int SampleRate { get; }

        public IReadOnlyList<GuitarAudioEvent> Push(ReadOnlySpan<float> samples)
        {
            if (_emitted) return [];
            _emitted = true;
            return _events;
        }

        public void Reset() => _emitted = false;
    }

    private sealed class FakeDetector : IGuitarEventDetector
    {
        private readonly GuitarAudioEvent[] _events;
        private long _pushed;
        private int _next;

        public FakeDetector(int sampleRate, GuitarAudioEvent[] events)
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
}
