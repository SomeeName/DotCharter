using System.Xml.Linq;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.State;

/// <summary>An edit that settles a flagged syllable's timing clears its auto-sync review reasons, undoably (#627).</summary>
public sealed class LyricReviewClearingTests
{
    private const LyricReviewReason Flagged = LyricReviewReason.LowScore | LyricReviewReason.Gap;
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(900);

    private static LyricEvent FlaggedLyric() => new()
    {
        Position = new FractionalPosition(4),
        EndPosition = new FractionalPosition(5),
        Text = "la",
        AlignmentConfidence = 0.02,
        ReviewReasons = Flagged
    };

    [Fact]
    public void MovingAFlaggedSyllableClearsItsReviewReasonsAndUndoRestoresThem()
    {
        var history = new HistoryService(new FakeAppLogger());
        var lyric = FlaggedLyric();

        Assert.True(history.Do(new MoveLyricAction(lyric, lyric.Position, lyric.EndPosition,
            new FractionalPosition(6), new FractionalPosition(7))));
        Assert.Equal(LyricReviewReason.None, lyric.ReviewReasons);
        Assert.Equal(0.02, lyric.AlignmentConfidence);

        history.Undo();
        Assert.Equal(Flagged, lyric.ReviewReasons);
        Assert.Equal(new FractionalPosition(4), lyric.Position);

        history.Redo();
        Assert.Equal(LyricReviewReason.None, lyric.ReviewReasons);
    }

    [Fact]
    public void AMoveThatChangesNothingLeavesTheReviewReasons()
    {
        var history = new HistoryService(new FakeAppLogger());
        var lyric = FlaggedLyric();

        Assert.False(history.Do(new MoveLyricAction(lyric, lyric.Position, lyric.EndPosition, lyric.Position, lyric.EndPosition)));

        Assert.Equal(Flagged, lyric.ReviewReasons);
    }

    [Fact]
    public void ResizingAFlaggedSyllableClearsItsReviewReasonsAndUndoRestoresThem()
    {
        var history = new HistoryService(new FakeAppLogger());
        var lyric = FlaggedLyric();

        Assert.True(history.Do(new ResizeLyricAction(lyric, lyric.EndPosition, new FractionalPosition(6))));
        Assert.Equal(LyricReviewReason.None, lyric.ReviewReasons);

        history.Undo();
        Assert.Equal(Flagged, lyric.ReviewReasons);
        Assert.Equal(new FractionalPosition(5), lyric.EndPosition);
    }

    [Fact]
    public void AMergedResizeRunRestoresTheReasonsItsFirstTickSaw()
    {
        var now = new DateTime(2026, 7, 28, 15, 43, 0, DateTimeKind.Utc);
        var history = new HistoryService(new FakeAppLogger(), clock: () => now, mergeWindow: Window);
        var lyric = FlaggedLyric();

        for (var beat = 5; beat < 8; beat++)
        {
            history.Do(new ResizeLyricAction(lyric, new FractionalPosition(beat), new FractionalPosition(beat + 1), MergeVerbs.LyricLength));
            now += TimeSpan.FromMilliseconds(100);
        }

        Assert.Single(history.GetUndoSteps());
        Assert.Equal(LyricReviewReason.None, lyric.ReviewReasons);
        history.Undo();
        Assert.Equal(Flagged, lyric.ReviewReasons);
        Assert.Equal(new FractionalPosition(5), lyric.EndPosition);
    }

    [Fact]
    public void MarkingReviewedClearsTheReasonsWithoutTouchingTheTiming()
    {
        var history = new HistoryService(new FakeAppLogger());
        var lyric = FlaggedLyric();

        Assert.True(history.Do(new MarkLyricReviewedAction(lyric)));
        Assert.Equal(LyricReviewReason.None, lyric.ReviewReasons);
        Assert.Equal(new FractionalPosition(4), lyric.Position);
        Assert.Equal(new FractionalPosition(5), lyric.EndPosition);
        Assert.False(history.Do(new MarkLyricReviewedAction(lyric)));

        history.Undo();
        Assert.Equal(Flagged, lyric.ReviewReasons);
    }

    [Fact]
    public void AClearedSyllableIsSavedWithoutAReviewAttributeAndAFlaggedOneReadsBack()
    {
        var lyric = FlaggedLyric();
        var flagged = new XElement("vocal");
        LyricReviewSerialization.Write(flagged, lyric);
        new MoveLyricAction(lyric, lyric.Position, lyric.EndPosition, new FractionalPosition(6), new FractionalPosition(7)).Execute();
        var cleared = new XElement("vocal");
        LyricReviewSerialization.Write(cleared, lyric);

        Assert.Equal(Flagged, LyricReviewSerialization.Read(flagged, new LyricEvent()).ReviewReasons);
        Assert.Null(cleared.Attribute("dcAlignReview"));
        Assert.Equal(LyricReviewReason.None, LyricReviewSerialization.Read(cleared, new LyricEvent()).ReviewReasons);
    }
}
