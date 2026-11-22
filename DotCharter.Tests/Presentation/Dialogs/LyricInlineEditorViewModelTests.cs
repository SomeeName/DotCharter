using DotCharter.Core.Domain.Vocals;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Lyrics;
using DotCharter.Services.Import.Lyrics;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Presentation.Dialogs;

public sealed class LyricInlineEditorViewModelTests
{
    [Theory]
    [InlineData(" sing- ", "sing", true)]
    [InlineData("song", "song", false)]
    [InlineData(" two words ", "two words", false)]
    [InlineData(" - ", "", false)]
    [InlineData("  ", "", false)]
    public void Trailing_hyphen_is_the_word_part_flag(string input, string text, bool wordPart) =>
        Assert.Equal((text, wordPart), LyricInlineEditorViewModel.ParseText(input));

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(4, -1)]
    [InlineData(5, 1)]
    [InlineData(7, 1)]
    [InlineData(8, -1)]
    public void Placement_chooses_only_a_line_that_spans_the_position(int beat, int expected)
    {
        var phrases = new[]
        {
            new LyricPhrase { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(3) },
            new LyricPhrase { Position = new FractionalPosition(5), EndPosition = new FractionalPosition(7) }
        };
        var actual = LyricInlineEditorViewModel.FindPhrase(phrases, new FractionalPosition(beat));
        if (expected < 0) Assert.Null(actual);
        else Assert.Same(phrases[expected], actual);
    }

    [Theory]
    [InlineData(1970)]
    [InlineData(3000)]
    public void Placement_snaps_and_joins_the_spanning_line_without_replacing_it(double timeMs)
    {
        var rig = new InlineEditRig();
        var track = new VocalTrack();
        rig.Chart.Project.VocalTracks.Add(track);
        rig.Chart.Editor.ActiveVocalTrack = track;
        var first = new LyricEvent { Position = rig.Chart.Editor.SnapToGrid(500), Text = "a", IsPhraseEnd = true };
        var phrase = new LyricPhrase
        {
            Position = first.Position, EndPosition = rig.Chart.Editor.SnapToGrid(3000), Lyrics = [first]
        };
        track.Phrases.Add(phrase);

        rig.Inline.ShowLyric(timeMs, null);
        Assert.Equal(rig.Chart.Editor.SnapToGrid(timeMs).GetPositionMs(rig.Chart.Editor.Beats), rig.Inline.Lyric.PlacementMs);
        Assert.Single(phrase.Lyrics);
        rig.Inline.Lyric.Text = "sing-";
        Assert.True(rig.Inline.Lyric.Commit());

        Assert.Same(phrase, Assert.Single(track.Phrases));
        var added = phrase.Lyrics[1];
        Assert.Equal(rig.Chart.Editor.SnapToGrid(timeMs), added.Position);
        Assert.Equal("sing", added.Text);
        // The track's last line: no next line to join, so the line end stays and the word part goes.
        Assert.False(added.IsWordPart);
        Assert.True(added.IsPhraseEnd);
        Assert.False(first.IsPhraseEnd);
        Assert.Same(added, rig.Chart.Editor.Selection.SelectedLyric);
        var committedEnd = phrase.EndPosition;
        Assert.True(committedEnd >= added.EndPosition);
        if (timeMs == 3000) Assert.True(committedEnd > rig.Chart.Editor.SnapToGrid(3000));
        Assert.Equal(1, rig.UndoSteps());
        Assert.Same(first, Assert.Single(phrase.Lyrics));
        Assert.True(first.IsPhraseEnd);
        Assert.Equal(rig.Chart.Editor.SnapToGrid(3000), phrase.EndPosition);
        rig.Chart.History.Redo();
        Assert.Same(added, phrase.Lyrics[1]);
        Assert.Equal(committedEnd, phrase.EndPosition);
        Assert.False(first.IsPhraseEnd);
        Assert.True(added.IsPhraseEnd);
        Assert.False(added.IsWordPart);
        Assert.Equal("sing", added.Text);
    }

    [Theory]
    [InlineData(1500, "la hel- lo")]
    [InlineData(3500, "la|hel- lo")]
    public void A_word_part_placed_after_a_line_end_joins_the_next_line_in_one_undo_step(double timeMs, string lines)
    {
        var rig = new InlineEditRig();
        var track = new VocalTrack();
        rig.Chart.Project.VocalTracks.Add(track);
        rig.Chart.Editor.ActiveVocalTrack = track;
        var first = new LyricEvent
        {
            Position = rig.Chart.Editor.SnapToGrid(500), EndPosition = rig.Chart.Editor.SnapToGrid(1000),
            Text = "la", IsPhraseEnd = true
        };
        var next = new LyricEvent
        {
            Position = rig.Chart.Editor.SnapToGrid(6000), EndPosition = rig.Chart.Editor.SnapToGrid(6500),
            Text = "lo", IsPhraseEnd = true
        };
        track.Phrases.Add(new LyricPhrase
        {
            Position = first.Position, EndPosition = rig.Chart.Editor.SnapToGrid(2000), Lyrics = [first]
        });
        track.Phrases.Add(new LyricPhrase { Position = next.Position, EndPosition = next.EndPosition, Lyrics = [next] });

        rig.Inline.ShowLyric(timeMs, null);
        rig.Inline.Lyric.Text = "hel-";
        Assert.True(rig.Inline.Lyric.Commit());

        var added = rig.Chart.Editor.Selection.SelectedLyric!;
        Assert.Equal(lines, string.Join("|", Lines(track)));
        Assert.DoesNotContain(track.Phrases.SelectMany(p => p.Lyrics), l => l.IsWordPart && l.IsPhraseEnd);
        Assert.Equal(1, rig.UndoSteps());
        Assert.Equal(["la", "lo"], Lines(track));
        Assert.True(first.IsPhraseEnd);
        Assert.True(next.IsPhraseEnd);
        rig.Chart.History.Redo();
        Assert.Contains(track.Phrases, p => p.Lyrics.Contains(added));
        Assert.Equal(lines, string.Join("|", Lines(track)));
        Assert.DoesNotContain(track.Phrases.SelectMany(p => p.Lyrics), l => l.IsWordPart && l.IsPhraseEnd);
    }

    private static List<string> Lines(VocalTrack track)
        => [.. track.Phrases.Select(p => string.Join(" ", p.Lyrics.Select(l => l.Text + (l.IsWordPart ? "-" : ""))))];

    [Fact]
    public void Placement_in_a_gap_creates_a_sorted_line_and_undo_restores_the_track()
    {
        var rig = new InlineEditRig();
        var track = new VocalTrack();
        rig.Chart.Project.VocalTracks.Add(track);
        rig.Chart.Editor.ActiveVocalTrack = track;
        var later = new LyricPhrase
        {
            Position = rig.Chart.Editor.SnapToGrid(4000), EndPosition = rig.Chart.Editor.SnapToGrid(5000)
        };
        track.Phrases.Add(later);
        rig.Inline.ShowLyric(1000, null);
        rig.Inline.Lyric.Text = "new";
        Assert.True(rig.Inline.Lyric.Commit());
        Assert.Equal(2, track.Phrases.Count);
        Assert.Same(later, track.Phrases[1]);
        Assert.Equal("new", Assert.Single(track.Phrases[0].Lyrics).Text);
        Assert.Equal(1, rig.UndoSteps());
        Assert.Same(later, Assert.Single(track.Phrases));
    }

    [Fact]
    public void First_placement_creates_the_track_only_on_commit_and_undo_removes_it()
    {
        var rig = new InlineEditRig();
        rig.Inline.ShowLyric(1000, null);
        Assert.Empty(rig.Chart.Project.VocalTracks);
        rig.Inline.Lyric.Text = "hello";
        Assert.True(rig.Inline.Lyric.Commit());
        var track = Assert.Single(rig.Chart.Project.VocalTracks);
        Assert.Same(track, rig.Chart.Editor.ActiveVocalTrack);
        Assert.Equal("hello", Assert.Single(Assert.Single(track.Phrases).Lyrics).Text);
        Assert.Equal(1, rig.UndoSteps());
        Assert.Empty(rig.Chart.Project.VocalTracks);
        Assert.Null(rig.Chart.Editor.ActiveVocalTrack);
        rig.Chart.History.Redo();
        Assert.Same(track, Assert.Single(rig.Chart.Project.VocalTracks));
        Assert.Same(track, rig.Chart.Editor.ActiveVocalTrack);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" - ")]
    public void Empty_new_text_cancels_without_a_track_or_undo_step(string text)
    {
        var rig = new InlineEditRig();
        rig.Inline.ShowLyric(1000, null);
        rig.Inline.Lyric.Text = text;
        Assert.True(rig.Inline.Lyric.Commit());
        Assert.False(rig.Inline.Lyric.IsOpen);
        Assert.Empty(rig.Chart.Project.VocalTracks);
        Assert.False(rig.Chart.History.CanUndo);
    }

    [Fact]
    public void Rename_preserves_the_phrase_end_and_tab_walks_in_both_directions()
    {
        var rig = new InlineEditRig();
        rig.Inline.ShowLyric(1000, null);
        rig.Inline.Lyric.Text = "one";
        rig.Inline.Lyric.Commit();
        var first = rig.Chart.Editor.Selection.SelectedLyric!;
        rig.Inline.ShowLyric(2000, null);
        rig.Inline.Lyric.Text = "two";
        rig.Inline.Lyric.Commit();
        var second = rig.Chart.Editor.Selection.SelectedLyric!;
        rig.Chart.History.Clear();

        rig.Inline.ShowLyric(1000, first);
        rig.Inline.Lyric.Text = "won";
        Assert.True(rig.Inline.Lyric.Commit(1));
        Assert.True(rig.Inline.Lyric.IsOpen);
        Assert.Same(second, rig.Chart.Editor.Selection.SelectedLyric);
        Assert.Equal("two", rig.Inline.Lyric.Text);
        Assert.True(first.IsPhraseEnd);
        Assert.False(first.IsWordPart);
        Assert.True(rig.Inline.Lyric.Commit(-1));
        Assert.Same(first, rig.Chart.Editor.Selection.SelectedLyric);
        Assert.Equal("won", rig.Inline.Lyric.Text);
        rig.Inline.Lyric.Text = "discard";
        rig.Inline.Lyric.Cancel();
        Assert.Equal("won", first.Text);
        Assert.Equal(1, rig.UndoSteps());
        Assert.Equal("one", first.Text);
    }

    [Fact]
    public void A_trailing_hyphen_on_a_line_end_joins_the_next_line_in_the_same_undo_step()
    {
        var rig = new InlineEditRig();
        rig.Inline.ShowLyric(1000, null);
        rig.Inline.Lyric.Text = "one";
        rig.Inline.Lyric.Commit();
        var first = rig.Chart.Editor.Selection.SelectedLyric!;
        rig.Inline.ShowLyric(2000, null);
        rig.Inline.Lyric.Text = "two";
        rig.Inline.Lyric.Commit();
        var track = rig.Chart.Editor.ActiveVocalTrack!;
        Assert.Equal(2, track.Phrases.Count);
        rig.Chart.History.Clear();

        rig.Inline.ShowLyric(1000, first);
        rig.Inline.Lyric.Text = "won-";
        Assert.True(rig.Inline.Lyric.Commit());

        Assert.Equal(["won", "two"], Assert.Single(track.Phrases).Lyrics.Select(l => l.Text));
        Assert.True(first.IsWordPart);
        Assert.False(first.IsPhraseEnd);
        Assert.Equal(1, rig.UndoSteps());
        Assert.Equal(2, track.Phrases.Count);
        Assert.True(first.IsPhraseEnd);
        Assert.False(first.IsWordPart);
    }

    [Fact]
    public void A_project_switch_cancels_the_pending_edit()
    {
        var rig = new InlineEditRig();
        rig.Inline.ShowLyric(1000, null);
        rig.Inline.Lyric.Text = "stale";
        rig.Chart.State.ActiveProject = new ChartProject();
        Assert.True(rig.Inline.Lyric.Commit());
        Assert.Empty(rig.Chart.Project.VocalTracks);
        Assert.False(rig.Chart.History.CanUndo);
    }

    [Fact]
    public void A_removed_target_cannot_be_renamed_by_a_pending_editor()
    {
        var rig = new InlineEditRig();
        rig.Inline.ShowLyric(1000, null);
        rig.Inline.Lyric.Text = "before";
        rig.Inline.Lyric.Commit();
        var lyric = rig.Chart.Editor.Selection.SelectedLyric!;
        rig.Inline.ShowLyric(1000, lyric);
        rig.Inline.Lyric.Text = "after";
        rig.Chart.Editor.ActiveVocalTrack!.Phrases.Clear();
        rig.Chart.History.Clear();
        Assert.True(rig.Inline.Lyric.Commit());
        Assert.Equal("before", lyric.Text);
        Assert.False(rig.Chart.History.CanUndo);
    }

    [Fact]
    public void Manual_edits_update_the_inspector_and_leave_its_armed_queue_unchanged()
    {
        var rig = new InlineEditRig();
        var inspector = new LyricsInspectorViewModel(rig.Chart.Editor, rig.Chart.History, rig.Chart.State,
            new FakeAppLogger(), new FakeFileStorageService(), RecordingProxy.Create<ILyricsFileService>().Proxy,
            RecordingProxy.Create<ILyricAutoSyncRunner>().Proxy, FakeLocalization.English())
        {
            RawLyrics = "hel-lo world"
        };
        inspector.ApplyCommand.Execute(null);
        var queued = (inspector.RemainingCount, inspector.NextPreview);

        rig.Inline.ShowLyric(1000, null);
        rig.Inline.Lyric.Text = "manual-";
        rig.Inline.Lyric.Commit();
        Assert.Equal("manual", inspector.SelectedLyricText);
        // The only line ends here, so the line end stays and the typed word part is dropped.
        Assert.False(inspector.SelectedLyricIsWordPart);
        Assert.True(inspector.SelectedLyricIsPhraseEnd);
        Assert.True(inspector.IsArmed);
        Assert.Equal(queued, (inspector.RemainingCount, inspector.NextPreview));

        rig.Inline.ShowLyric(1000, rig.Chart.Editor.Selection.SelectedLyric);
        rig.Inline.Lyric.Text = "edited";
        rig.Inline.Lyric.Commit();
        Assert.Equal("edited", inspector.SelectedLyricText);
        rig.Chart.History.Undo();
        Assert.Equal("manual", inspector.SelectedLyricText);
        rig.Chart.History.Redo();
        Assert.Equal("edited", inspector.SelectedLyricText);
        Assert.Equal(queued, (inspector.RemainingCount, inspector.NextPreview));
    }

    [Fact]
    public void Placement_and_rename_retain_linked_measures()
    {
        var rig = new InlineEditRig();
        var history = new CapturingHistory();
        var edit = new ChartEditDialogServices(rig.Chart.Editor, history, rig.Chart.State,
            rig.Chart.Services.Config, FakeLocalization.English(), new FakeAppLogger());
        var inline = new LyricInlineEditorViewModel(edit);
        inline.Begin(1000, null);
        inline.Text = "first";
        inline.Commit();
        Assert.Equal(LinkPolicy.Retain, LinkPolicies.Of(history.Last!));
        inline.Begin(1000, rig.Chart.Editor.Selection.SelectedLyric);
        inline.Text = "second";
        inline.Commit();
        Assert.Equal(LinkPolicy.Retain, LinkPolicies.Of(history.Last!));
    }

    private sealed class CapturingHistory() : HistoryService(new FakeAppLogger())
    {
        public IChartAction? Last { get; private set; }

        public override bool Do(IChartAction action)
        {
            Last = action;
            return base.Do(action);
        }
    }
}
