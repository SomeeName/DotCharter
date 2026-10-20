using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     A DOUBLE STOP the chart spells as two separate notes.
///     <para>
///         Two strings struck together are ONE onset carrying both pitches, but
///         RS-XML, EOF and the Guitar Pro importers all write them as two
///         <c>note</c> elements at the same position rather than as a chord.
///         <c>OfferPitches</c> judged the first and returned, and nothing else
///         was ever offered to the second — there is no second onset — so it sat
///         unjudged until its window expired. A perfectly played double stop
///         scored 50%, broke the streak and reset the multiplier, every time.
///     </para>
/// </summary>
public sealed class SimultaneousSoundJudgingTests
{
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static Arrangement NewArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    private static GuitarNote Note(int beat, int fret, int stringIdx)
        => new() { Position = new FractionalPosition(beat), Fret = fret, StringIndex = stringIdx };

    private static GuitarNote Note(int beat, Fraction within, int fret, int stringIdx)
        => new() { Position = new FractionalPosition(beat, within), Fret = fret, StringIndex = stringIdx };

    private static LiveNoteMatcher Loaded(Arrangement arr)
    {
        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    /// <summary>Low E fret 5 (A2 = 45) and A fret 7 (E3 = 52), struck together.</summary>
    private static Arrangement DoubleStopAtBeatTwo()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(2, fret: 5, stringIdx: 0));
        arr.Levels[0].Notes.Add(Note(2, fret: 7, stringIdx: 1));
        return arr;
    }

    private static LivePitch[] Both() => [new(45, 0f, 1f), new(52, 0f, 0.8f)];

    [Fact]
    public void OneAttack_JudgesEverySoundAtThatChartTime()
    {
        var matcher = Loaded(DoubleStopAtBeatTwo());
        var extra = new List<LivePlayEvent>();

        var first = matcher.OfferPitches(Both(), timeMs: 1000, alsoJudged: extra);

        Assert.NotNull(first);
        Assert.Equal(LivePlayVerdict.Hit, first!.Value.Verdict);
        Assert.Single(extra);
        Assert.Equal(LivePlayVerdict.Hit, extra[0].Verdict);
        Assert.Equal(2, matcher.Stats.Hits);
        Assert.Equal(0, matcher.Stats.Misses);
    }

    /// <summary>The consequence the player actually felt.</summary>
    [Fact]
    public void APerfectlyPlayedDoubleStop_DoesNotBreakTheStreak()
    {
        var arr = DoubleStopAtBeatTwo();
        // …and a single note a beat later, so the streak has somewhere to go.
        arr.Levels[0].Notes.Add(Note(3, fret: 3, stringIdx: 0)); // G2 = 43 at 1500 ms
        var matcher = Loaded(arr);

        matcher.OfferPitches(Both(), timeMs: 1000);
        matcher.OfferNote(midi: 43, timeMs: 1500);
        matcher.AdvanceTo(4000);

        Assert.Equal(3, matcher.Stats.Hits);
        Assert.Equal(0, matcher.Stats.Misses);
        Assert.Equal(3, matcher.Stats.Streak);
        Assert.Equal(1.0, matcher.Stats.Accuracy);
    }

    /// <summary>
    ///     The tallies are right whether or not the caller wants the extra
    ///     events — couch co-op's seats discard them, and their score must not
    ///     depend on that.
    /// </summary>
    [Fact]
    public void TheExtraJudgmentsHappenEvenWithNoListToReportThemIn()
    {
        var matcher = Loaded(DoubleStopAtBeatTwo());

        matcher.OfferPitches(Both(), timeMs: 1000);

        Assert.Equal(2, matcher.Stats.Hits);
    }

    /// <summary>
    ///     Leniency is on the SPELLING, not on the evidence. Only the low string
    ///     sounded, so only the low string's note is a hit.
    /// </summary>
    [Fact]
    public void ADoubleStopWithOnlyOneStringSounded_StillMissesTheOther()
    {
        var matcher = Loaded(DoubleStopAtBeatTwo());
        var extra = new List<LivePlayEvent>();

        Span<LivePitch> lowOnly = [new(45, 0f, 1f)];
        var first = matcher.OfferPitches(lowOnly, timeMs: 1000, alsoJudged: extra);
        matcher.AdvanceTo(4000);

        Assert.NotNull(first);
        Assert.Empty(extra);
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(1, matcher.Stats.Misses);
    }

    /// <summary>
    ///     The window is one millisecond, not the hit window: two notes a
    ///     subdivision apart are two attacks and must each be earned. A 16th at
    ///     120 BPM is 125 ms — comfortably inside the 150/250 hit window, so
    ///     without the tolerance this attack would clear the next note too.
    /// </summary>
    [Fact]
    public void ALaterNoteInsideTheSameHitWindow_IsNotClearedByOneAttack()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(2, fret: 5, stringIdx: 0));    // 1000 ms
        arr.Levels[0].Notes.Add(Note(2, new Fraction(1, 4), fret: 5, stringIdx: 0)); // 1125 ms, same pitch
        var matcher = Loaded(arr);
        var extra = new List<LivePlayEvent>();

        Span<LivePitch> a2 = [new(45, 0f, 1f)];
        matcher.OfferPitches(a2, timeMs: 1000, alsoJudged: extra);

        Assert.Empty(extra);
        Assert.Equal(1, matcher.Stats.Hits);

        // The second note is still open and still earnable.
        Assert.NotNull(matcher.OfferPitches(a2, timeMs: 1125));
        Assert.Equal(2, matcher.Stats.Hits);
        Assert.Equal(0, matcher.Stats.Misses);
    }

    /// <summary>
    ///     Through the session, because that is what tallies sections and drives
    ///     the HUD — and because <c>Offer</c> owns the CLEAR of the caller's
    ///     reused list.
    /// </summary>
    [Fact]
    public void TheSession_TalliesBothAndClearsTheCallersList()
    {
        var arr = DoubleStopAtBeatTwo();
        var session = new LivePlaySessionService();
        session.Start(arr, arr.Levels[0], Beats(), new LivePlaySessionOptions());

        var extra = new List<LivePlayEvent> { default }; // stale entry from a previous attack
        var first = session.Offer(Both(), [], 1000,
            LiveTechniqueFlags.None, 0f, extra);

        Assert.NotNull(first);
        Assert.Single(extra);
        Assert.Equal(2, session.Stats!.Hits);
        Assert.Equal(2, session.Sections[0].Hits);
    }
}
