using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     <c>ResizeLyricAction</c> was not <see cref="IMergeableAction" /> at all —
///     one history node per WHEEL TICK, which is the exact pattern the merge
///     machinery was added to kill (the 2026-07-28 session's 111-node hand-shape
///     resize). Stretching a syllable is done by rolling the wheel over it, so
///     the tick count measures the user's wrist rather than their intent.
///     <para>
///         Injected clock, node COUNTS only — never elapsed time.
///     </para>
/// </summary>
public sealed class ResizeLyricGestureMergeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(900);

    private static LyricEvent LyricAt(int beat, string text = "la") => new()
    {
        Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(beat),
        Text = text
    };

    private static ResizeLyricAction Tick(LyricEvent lyric, int fromBeat, int toBeat) =>
        new(lyric, new FractionalPosition(fromBeat), new FractionalPosition(toBeat), MergeVerbs.LyricLength);

    private sealed class Clock
    {
        private DateTime _nowUtc = new(2026, 7, 28, 15, 43, 0, DateTimeKind.Utc);
        public DateTime Read() => _nowUtc;
        public void Advance(TimeSpan by) => _nowUtc += by;
    }

    [Fact]
    public void AWheelRunOverOneSyllableCollapsesToOneUndoNode()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var lyric = LyricAt(4);

        for (var i = 0; i < 20; i++)
        {
            history.Do(Tick(lyric, 4 + i, 5 + i));
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }

        Assert.Equal(new FractionalPosition(24), lyric.EndPosition);
        Assert.Single(history.GetUndoSteps());

        // ONE Ctrl+Z restores the syllable's original end, not the 19th tick's.
        history.Undo();
        Assert.Equal(new FractionalPosition(4), lyric.EndPosition);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void RedoReplaysTheWholeRun()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var lyric = LyricAt(4);

        for (var i = 0; i < 5; i++)
        {
            history.Do(Tick(lyric, 4 + i, 5 + i));
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }

        history.Undo();
        Assert.Equal(new FractionalPosition(4), lyric.EndPosition);

        history.Redo();
        Assert.Equal(new FractionalPosition(9), lyric.EndPosition);
    }

    /// <summary>Rule (c): targets compare by REFERENCE, so wheeling over the next
    /// syllable is a new gesture even inside the window.</summary>
    [Fact]
    public void TwoDifferentSyllablesNeverMerge()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var first = LyricAt(4);
        var second = LyricAt(8);

        history.Do(Tick(first, 4, 5));
        history.Do(Tick(second, 8, 9));

        Assert.Equal(2, history.GetUndoSteps().Count);
    }

    [Fact]
    public void APauseLongerThanTheWindowStartsANewNode()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var lyric = LyricAt(4);

        history.Do(Tick(lyric, 4, 5));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        history.Do(Tick(lyric, 5, 6));

        clock.Advance(Window + TimeSpan.FromMilliseconds(1));
        history.Do(Tick(lyric, 6, 7));

        Assert.Equal(2, history.GetUndoSteps().Count);
        history.Undo();
        Assert.Equal(new FractionalPosition(6), lyric.EndPosition);
    }

    /// <summary>The opt-in rule: an action built WITHOUT a verb keeps its own
    /// node, so a deliberate single resize from some other call site is
    /// unaffected.</summary>
    [Fact]
    public void AResizeWithNoVerbNeverMerges()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var lyric = LyricAt(4);

        for (var i = 0; i < 3; i++)
        {
            history.Do(new ResizeLyricAction(lyric,
                new FractionalPosition(4 + i), new FractionalPosition(5 + i)));
        }

        Assert.Equal(3, history.GetUndoSteps().Count);
    }

    /// <summary>A lyric resize and a lyric MOVE are different gestures over the
    /// same object — the move carries no verb at all, so it can never fold into
    /// a resize run.</summary>
    [Fact]
    public void AResizeRunAndAMoveDoNotFoldTogether()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var lyric = LyricAt(4);

        history.Do(Tick(lyric, 4, 5));
        history.Do(new MoveLyricAction(lyric,
            new FractionalPosition(4), new FractionalPosition(5),
            new FractionalPosition(6), new FractionalPosition(7)));

        Assert.Equal(2, history.GetUndoSteps().Count);
    }

    /// <summary>
    ///     …and the WHEEL, which is the only caller that repeats, must actually
    ///     ask for the merge. The verb is opt-in per call site, so every test
    ///     above passes with the call site unchanged — that gap is exactly how a
    ///     mergeable action ships still recording one node per tick.
    ///     <para>
    ///         A SOURCE LINT because <c>TimelineWheelAdjuster</c> takes a
    ///         <c>MainWindowViewModel</c>, which needs the whole app booted — the
    ///         same reason <c>StartupWorkDeferralTests</c> and
    ///         <c>PenEscapeReachabilityTests</c> read source. It has no
    ///         behavioural test of any kind today.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheWheelCallSitePassesTheMergeVerb()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Input", "TimelineWheelAdjuster.cs")));

        var start = source.IndexOf("TryAdjustLyricSustain(", StringComparison.Ordinal);
        Assert.True(start >= 0, "TryAdjustLyricSustain is the lyric wheel rule; it must still exist.");

        var construction = source.IndexOf("new ResizeLyricAction(", start, StringComparison.Ordinal);
        Assert.True(construction > start, "The lyric wheel rule must still push a ResizeLyricAction.");

        var end = source.IndexOf(';', construction);
        Assert.Contains("MergeVerbs.LyricLength", source[construction..end], StringComparison.Ordinal);
    }
}
