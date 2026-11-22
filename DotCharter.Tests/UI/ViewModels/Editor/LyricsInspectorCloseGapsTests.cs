using DotCharter.Core.Domain.Vocals;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class LyricsInspectorCloseGapsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCommandPreservesDeliberateShorteningAndItsReviewReasons(bool alreadyAtLimit)
    {
        var gap = new FractionalPosition(new Fraction(1, 16));
        var shortEnd = alreadyAtLimit ? new FractionalPosition(4) - gap : new FractionalPosition(2);
        var shortened = new LyricEvent
        {
            Position = new(1), EndPosition = shortEnd,
            IsSustainShortened = true, ReviewReasons = LyricReviewReason.LowScore
        };
        var overlaps = new LyricEvent { Position = new(4), EndPosition = new(9), ReviewReasons = LyricReviewReason.Gap };
        var alreadySpaced = new LyricEvent
        {
            Position = new(6), EndPosition = new FractionalPosition(8) - gap, ReviewReasons = LyricReviewReason.CapHit
        };
        var last = new LyricEvent { Position = new(8), EndPosition = new(9), IsPhraseEnd = true };
        var (history, inspector) = BuildInspector(new FakeConfigurationService(), shortened, overlaps, alreadySpaced, last);

        inspector.CloseGapsCommand.Execute(null);

        Assert.Equal(shortEnd, shortened.EndPosition);
        Assert.True(shortened.IsSustainShortened);
        Assert.Equal(LyricReviewReason.LowScore, shortened.ReviewReasons);
        Assert.Equal(alreadySpaced.Position - gap, overlaps.EndPosition);
        Assert.Equal(LyricReviewReason.None, overlaps.ReviewReasons);
        Assert.Equal(LyricReviewReason.CapHit, alreadySpaced.ReviewReasons);
        Assert.Single(history.GetUndoSteps());
        history.Undo();
        Assert.Equal(new FractionalPosition(9), overlaps.EndPosition);
        Assert.Equal(LyricReviewReason.Gap, overlaps.ReviewReasons);
    }

    [Fact]
    public void TheCommandWithWordSpanningOffKeepsMeasuredWordEndsAndSpansInsideWords()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.LyricSpanWordGap = false;
        var wordEnd = new LyricEvent { Position = new(1), EndPosition = new(2), ReviewReasons = LyricReviewReason.LowScore };
        var wordPart = new LyricEvent { Position = new(4), EndPosition = new(5), IsWordPart = true };
        var last = new LyricEvent { Position = new(6), EndPosition = new(7), IsPhraseEnd = true };
        var (history, inspector) = BuildInspector(config, wordEnd, wordPart, last);

        inspector.CloseGapsCommand.Execute(null);

        Assert.Equal(new FractionalPosition(2), wordEnd.EndPosition);
        Assert.Equal(LyricReviewReason.LowScore, wordEnd.ReviewReasons);
        Assert.Equal(last.Position - new FractionalPosition(new Fraction(1, 16)), wordPart.EndPosition);
        history.Undo();
        Assert.Equal(new FractionalPosition(5), wordPart.EndPosition);
    }

    private static (HistoryService History, LyricsInspectorViewModel Inspector) BuildInspector(
        FakeConfigurationService config, params LyricEvent[] lyrics)
    {
        var chart = new ChartEditHarness();
        var phrase = new LyricPhrase { Position = lyrics[0].Position, EndPosition = lyrics[^1].EndPosition, Lyrics = [.. lyrics] };
        var track = new VocalTrack { Phrases = [phrase] };
        chart.Project.VocalTracks.Add(track);
        chart.Editor.ActiveVocalTrack = track;
        return (chart.History, LyricDraftRig.NewInspector(chart.Editor, chart.History, chart.State, config: config));
    }

    [Fact]
    public void TheCommandUsesTheConfiguredGapAcrossTheTrackAndUndoesBothEnds()
    {
        var chart = new ChartEditHarness();
        var config = new FakeConfigurationService();
        config.AppConfig.MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction;
        config.AppConfig.MinNoteDistanceDivisor = 8;
        var early = new LyricEvent { Position = new(1), EndPosition = new(2) };
        var overlaps = new LyricEvent { Position = new(4), EndPosition = new(8) };
        var lineEnd = new LyricEvent { Position = new(6), EndPosition = new(7), IsPhraseEnd = true };
        var otherLine = new LyricEvent { Position = new(9), EndPosition = new(10), IsPhraseEnd = true };
        var track = new VocalTrack
        {
            Phrases =
            [
                new LyricPhrase { Position = new(1), EndPosition = new(7), Lyrics = [early, overlaps, lineEnd] },
                new LyricPhrase { Position = new(9), EndPosition = new(10), Lyrics = [otherLine] }
            ]
        };
        chart.Project.VocalTracks.Add(track);
        chart.Editor.ActiveVocalTrack = track;
        var inspector = LyricDraftRig.NewInspector(chart.Editor, chart.History, chart.State, config: config);
        var lyrics = track.Phrases.SelectMany(phrase => phrase.Lyrics).ToArray();
        var starts = lyrics.Select(lyric => lyric.Position).ToArray();
        var originalEnds = lyrics.Select(lyric => lyric.EndPosition).ToArray();
        var gap = new FractionalPosition(new Fraction(1, 8));

        inspector.CloseGapsCommand.Execute(null);
        inspector.CloseGapsCommand.Execute(null);

        Assert.Equal(starts, lyrics.Select(lyric => lyric.Position));
        Assert.Equal(overlaps.Position - gap, early.EndPosition);
        Assert.Equal(lineEnd.Position - gap, overlaps.EndPosition);
        Assert.Equal(originalEnds.Skip(2), lyrics.Skip(2).Select(lyric => lyric.EndPosition));
        Assert.Single(chart.History.GetUndoSteps());
        chart.History.Undo();
        Assert.Equal(originalEnds, lyrics.Select(lyric => lyric.EndPosition));
        chart.History.Redo();
        Assert.Equal(overlaps.Position - gap, early.EndPosition);
        Assert.Equal(lineEnd.Position - gap, overlaps.EndPosition);
    }
}
