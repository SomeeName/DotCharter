using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Live judging under a tuning-match transpose (#312). The SONG is shifted to the player's
///     tuning and the frets stay as charted, so every expected pitch moves by the same step: an
///     E♭-standard chart played on an E-standard guitar expects each charted fret one semitone
///     HIGHER than the chart sounds. Judging the charted pitch would grade the player against the
///     note the shift just told them not to play.
/// </summary>
public sealed class TuningMatchJudgingTests
{
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    /// <summary>E♭ standard, one note at 1000 ms: low string, fret 3 — G♭2, MIDI 42, as charted.</summary>
    private static Arrangement EFlatChart(Action<ChartLevel>? seed = null)
    {
        var arr = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            StringCount = 6,
            Tuning = ["Eb", "Ab", "Db", "Gb", "Bb", "eb"]
        };
        foreach (var _ in Enumerable.Range(0, 6)) arr.TuningPitches.Add(new TuningOffset(-1));
        var level = new ChartLevel();
        if (seed != null) seed(level);
        else level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
        arr.Levels.Add(level);
        return arr;
    }

    private static LiveNoteMatcher StringAware(int transpose) =>
        LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal, LiveChordVoicingStrictness.Standard,
            stringAware: true, transposeSemitones: transpose);

    [Fact]
    public void Shifted_TheChartedFretAtThePlayersPitch_IsAHit()
    {
        var arr = EFlatChart();
        var matcher = StringAware(transpose: 1);
        matcher.Load(arr, arr.Levels[0], Beats());

        // Fret 3 on an E-standard low string is G2 = 43.
        var result = matcher.OfferNote(midi: 43, timeMs: 1000);

        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    /// <summary>…and the chart's own pitch is no longer the target: the player is not in E♭.</summary>
    [Fact]
    public void Shifted_TheChartsUnshiftedPitch_IsNotAHit()
    {
        var arr = EFlatChart();
        var matcher = StringAware(transpose: 1);
        matcher.Load(arr, arr.Levels[0], Beats());

        var result = matcher.OfferNote(midi: 42, timeMs: 1000);

        Assert.True(result is not { Verdict: LivePlayVerdict.Hit }, "the unshifted pitch was credited");
        Assert.Equal(0, matcher.Stats.Hits);
    }

    /// <summary>The default is the chart's own pitch — the editor's Live Play never shifts.</summary>
    [Fact]
    public void Unshifted_TheChartsOwnPitch_IsStillTheTarget()
    {
        var arr = EFlatChart();
        var matcher = StringAware(transpose: 0);
        matcher.Load(arr, arr.Levels[0], Beats());

        Assert.Equal(LivePlayVerdict.Hit, matcher.OfferNote(midi: 42, timeMs: 1000)!.Value.Verdict);
    }

    /// <summary>The CQT chart-verifier is asked for the SHIFTED pitches — chords, slides and
    /// single notes alike, because they all come through the one expected-pitch helper.</summary>
    [Fact]
    public void CqtMode_TheVerifierIsAskedForTheShiftedPitches()
    {
        var arr = EFlatChart(level =>
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(2), StringIndex = 1, Fret = 2, SlideTo = 4
            });
            var chord = new Chord { Position = new FractionalPosition(6) };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
            level.Chords.Add(chord);
        });

        var asked = new List<ExpectedSound>();
        var matcher = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            LiveChordVoicingStrictness.Standard, stringAware: false,
            chartVerify: sound =>
            {
                asked.Add(sound);
                return new ChartVerifyOutcome(true, 1f);
            },
            transposeSemitones: 2);
        matcher.Load(arr, arr.Levels[0], Beats());
        matcher.AdvanceTo(6000);

        // As charted: A♭ string fret 2 = 46, slide to fret 4 = 48; open E♭ = 39.
        var note = Assert.Single(asked, s => s.MidiNotes.Length == 1);
        Assert.Equal(46 + 2, note.MidiNotes[0]);
        Assert.Equal(48 + 2, note.SlideToMidi);
        var chord = Assert.Single(asked, s => s.MidiNotes.Length == 2);
        Assert.Equal(new[] { 39 + 2, 46 + 2 }, chord.MidiNotes.Order());
    }

    /// <summary>A session started with the shift judges with it: the options are what Play Mode
    /// hands Live Play, and the matcher is built inside <c>Start</c>.</summary>
    [Fact]
    public void ASessionStartedWithTheShift_JudgesWithIt()
    {
        var arr = EFlatChart();
        var session = new LivePlaySessionService();

        session.Start(arr, arr.Levels[0], Beats(),
            new LivePlaySessionOptions { TransposeSemitones = 1, StringAwareVerification = true });

        Assert.Equal(1, session.Matcher!.TransposeSemitones);
        Assert.Equal(LivePlayVerdict.Hit, session.Matcher.OfferNote(midi: 43, timeMs: 1000)!.Value.Verdict);
    }
}
