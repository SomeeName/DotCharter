using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Lyrics tab's review list in inspector design D's cards (#751): every syllable in time order or only the
///     flagged ones, a row picked is the editor's selected syllable (and the playhead moves there), the editor's
///     selection is the list's, Previous / Next step through the rows shown, and an undoable edit re-reads the list.
/// </summary>
public sealed class LyricSyllableListTests
{
    [Fact]
    public void TheList_ShowsEverySyllableInTimeOrder_OrOnlyTheFlaggedOnes()
    {
        var (inspector, editor, _) = LyricReviewNavigationTests.Build();
        var lyrics = editor.ActiveVocalTrack!.Phrases.Single().Lyrics;
        lyrics[1].ReviewReasons = LyricReviewReason.Gap;
        var list = inspector.Syllables;
        list.Refresh();

        Assert.Equal(lyrics, list.Rows.Select(r => r.Lyric));
        Assert.Equal([false, true, false], list.Rows.Select(r => r.NeedsReview));
        Assert.True(list.HasRows);

        list.FilterIndex = LyricSyllableList.ShowNeedsReview;
        Assert.Same(lyrics[1], Assert.Single(list.Rows).Lyric);

        lyrics[1].ReviewReasons = LyricReviewReason.None;
        list.Refresh();
        Assert.Empty(list.Rows);
        Assert.True(list.FilterHidesAll);
    }

    [Fact]
    public void APickedRow_SelectsTheSyllableAndSeeks_AndTheListFollowsTheEditor()
    {
        var (inspector, editor, selection) = LyricReviewNavigationTests.Build();
        var lyrics = editor.ActiveVocalTrack!.Phrases.Single().Lyrics;
        var list = inspector.Syllables;

        list.SelectedRow = list.Rows[2];
        Assert.Same(lyrics[2], selection.SelectedLyric);
        Assert.Equal(lyrics[2].Position.GetPositionMs(editor.Beats), editor.PlayheadMs);

        selection.SelectedLyric = lyrics[0];
        Assert.Same(lyrics[0], list.SelectedRow?.Lyric);

        // a selection the filter hides is no row
        list.FilterIndex = LyricSyllableList.ShowNeedsReview;
        Assert.Null(list.SelectedRow);
        Assert.Same(lyrics[0], selection.SelectedLyric);
    }

    [Fact]
    public void PreviousAndNext_StepThroughTheRowsShown()
    {
        var (inspector, editor, selection) = LyricReviewNavigationTests.Build();
        var lyrics = editor.ActiveVocalTrack!.Phrases.Single().Lyrics;
        var list = inspector.Syllables;
        selection.SelectedLyric = lyrics[0];

        Assert.False(list.PreviousCommand.CanExecute(null));
        list.NextCommand.Execute(null);
        Assert.Same(lyrics[1], selection.SelectedLyric);
        list.NextCommand.Execute(null);
        Assert.Same(lyrics[2], selection.SelectedLyric);
        Assert.False(list.NextCommand.CanExecute(null));
        list.PreviousCommand.Execute(null);
        Assert.Same(lyrics[1], selection.SelectedLyric);
    }

    /// <summary>A history change that left the vocal track as it was rebuilds nothing; one that changed it does.</summary>
    [Fact]
    public void AHistoryChange_RebuildsTheRowsOnlyWhenTheTrackChanged()
    {
        var (inspector, editor, _) = LyricReviewNavigationTests.Build(out var history);
        var list = inspector.Syllables;
        var rows = list.Rows;
        var builds = list.BuildCount;

        history.Do(new SetSongMetadataAction(editor.CurrentChart!, SongMetadataFields.From(editor.CurrentChart!),
            SongMetadataFields.From(editor.CurrentChart!) with { SongName = "x" }));

        Assert.Same(rows, list.Rows);
        Assert.Equal(builds, list.BuildCount);

        var lyric = editor.ActiveVocalTrack!.Phrases.Single().Lyrics[1];
        history.Do(new RenameLyricAction(lyric, lyric.Text, "lo", false, false, false, false));
        Assert.Equal(builds + 1, list.BuildCount);
        Assert.Equal("lo", list.Rows[1].Text);
    }

    /// <summary>While the Lyrics tab is hidden a change only marks the list stale; showing it catches up once.</summary>
    [Fact]
    public void WhileHidden_TheListWaits_AndCatchesUpOnceWhenShown()
    {
        var (inspector, editor, _) = LyricReviewNavigationTests.Build(out var history);
        var list = inspector.Syllables;
        list.IsShown = false;
        var builds = list.BuildCount;
        var lyric = editor.ActiveVocalTrack!.Phrases.Single().Lyrics[1];

        history.Do(new RenameLyricAction(lyric, lyric.Text, "lo", false, false, false, false));
        history.Do(new RenameLyricAction(lyric, "lo", "li", false, false, false, false));
        Assert.Equal(builds, list.BuildCount);

        list.IsShown = true;
        Assert.Equal(builds + 1, list.BuildCount);
        Assert.Equal("li", list.Rows[1].Text);
        list.IsShown = true; // already current: no second build
        Assert.Equal(builds + 1, list.BuildCount);
    }

    /// <summary>Picking the row that is already picked seeks to its syllable again.</summary>
    [Fact]
    public void SeekSelected_SeeksToThePickedRowAgain()
    {
        var (inspector, editor, _) = LyricReviewNavigationTests.Build();
        var list = inspector.Syllables;
        list.SelectedRow = list.Rows[2];
        editor.ExecuteSeek(0);

        list.SeekSelected();

        Assert.Equal(list.Rows[2].Lyric.Position.GetPositionMs(editor.Beats), editor.PlayheadMs);
    }

    /// <summary>The status line is re-worded in place when the language switches.</summary>
    [Fact]
    public void TheStatusLine_FollowsALanguageSwitch()
    {
        var loc = FakeLocalization.English();
        var (inspector, _, _) = LyricReviewNavigationTests.Build(out _, loc);
        Assert.Equal(loc["Inspector_Lyrics_StatusIdle"], inspector.StatusText);

        loc.SwitchTo("de-DE");

        Assert.Equal(loc["Inspector_Lyrics_StatusIdle"], inspector.StatusText);
        Assert.Equal("Liedtext einfügen und Übernehmen drücken.", inspector.StatusText);
    }

    [Fact]
    public void MarkingReviewed_DropsTheRowFromTheReviewFilter_AndUndoBringsItBack()
    {
        var (inspector, editor, selection) = LyricReviewNavigationTests.Build(out var history);
        var lyrics = editor.ActiveVocalTrack!.Phrases.Single().Lyrics;
        lyrics[0].ReviewReasons = LyricReviewReason.LowScore;
        lyrics[2].ReviewReasons = LyricReviewReason.Gap;
        var list = inspector.Syllables;
        list.FilterIndex = LyricSyllableList.ShowNeedsReview;
        Assert.Equal([lyrics[0], lyrics[2]], list.Rows.Select(r => r.Lyric));
        selection.SelectedLyric = lyrics[0];

        inspector.MarkReviewedCommand.Execute(null);
        Assert.Same(lyrics[2], Assert.Single(list.Rows).Lyric);

        history.Undo();
        Assert.Equal([lyrics[0], lyrics[2]], list.Rows.Select(r => r.Lyric));
    }
}
