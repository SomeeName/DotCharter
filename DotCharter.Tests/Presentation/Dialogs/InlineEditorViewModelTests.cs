using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The fret-hand position editor and the section popover that replace two dialogs on the timeline (#498):
///     the fret starts from the notes under the anchor, Enter commits the dialog edit as ONE undo step,
///     Escape leaves nothing behind, and "More..." carries the typed section to the full event dialog.
/// </summary>
public sealed class InlineEditorViewModelTests
{
    [Fact]
    public void A_new_anchor_is_prefilled_from_the_notes_under_it_and_commits_as_one_undo_step()
    {
        var rig = new InlineEditRig();
        rig.Chart.Editor.PlacedNotes.Add(new GuitarNote { Position = rig.Chart.Editor.SnapToGrid(2000), StringIndex = 1, Fret = 9 });
        rig.Chart.Editor.PlacedNotes.Add(new GuitarNote { Position = rig.Chart.Editor.SnapToGrid(2500), StringIndex = 2, Fret = 7 });

        var fhp = rig.Inline.Fhp;
        fhp.Begin(2000, null);

        Assert.True(fhp.IsOpen);
        Assert.Equal("7", fhp.FretText);
        Assert.Empty(rig.Chart.Editor.Anchors);

        fhp.FretText = "8";
        Assert.True(fhp.Commit());

        Assert.False(fhp.IsOpen);
        var added = Assert.Single(rig.Chart.Editor.Anchors);
        Assert.Equal(8, added.Fret);
        Assert.Equal(rig.Chart.Editor.SnapToGrid(2000), added.Position);
        Assert.Equal(1, rig.UndoSteps());
        Assert.Empty(rig.Chart.Editor.Anchors);
    }

    [Fact]
    public void A_new_anchor_takes_its_width_from_the_latest_anchor_by_position_not_the_last_one_stored()
    {
        var rig = new InlineEditRig();
        rig.Chart.Editor.Anchors.Add(new FretAnchor { Position = rig.Chart.Editor.SnapToGrid(1000), Fret = 9, Width = 5 });
        rig.Chart.Editor.Anchors.Add(new FretAnchor { Position = rig.Chart.Editor.SnapToGrid(0), Fret = 2, Width = 3 });

        rig.Inline.Fhp.Begin(2000, null);

        Assert.Equal("5", rig.Inline.Fhp.WidthText);
    }

    [Fact]
    public void Width_is_reachable_and_an_existing_anchor_is_edited_in_one_undo_step()
    {
        var rig = new InlineEditRig();
        var anchor = new FretAnchor { Position = rig.Chart.Editor.SnapToGrid(1000), Fret = 3, Width = 4 };
        rig.Chart.Editor.Anchors.Add(anchor);

        var fhp = rig.Inline.Fhp;
        fhp.Begin(1000, anchor);
        Assert.True(fhp.IsEditingExisting);
        Assert.Equal("3", fhp.FretText);
        Assert.Equal("4", fhp.WidthText);

        fhp.FretText = "5";
        fhp.WidthText = "6";
        Assert.True(fhp.Commit());

        Assert.Same(anchor, Assert.Single(rig.Chart.Editor.Anchors));
        Assert.Equal((5, 6), (anchor.Fret, anchor.Width));
        Assert.Equal(1, rig.UndoSteps());
        Assert.Equal((3, 4), (anchor.Fret, anchor.Width));
    }

    [Fact]
    public void Cancel_writes_nothing_and_leaves_no_undo_entry()
    {
        var rig = new InlineEditRig();

        rig.Inline.Fhp.Begin(1500, null);
        rig.Inline.Fhp.FretText = "12";
        rig.Inline.Fhp.Cancel();

        Assert.False(rig.Inline.Fhp.IsOpen);
        Assert.Empty(rig.Chart.Editor.Anchors);
        Assert.False(rig.Chart.History.CanUndo);
    }

    [Theory]
    [InlineData("", "4")]
    [InlineData("abc", "4")]
    [InlineData("0", "4")]
    [InlineData("25", "4")]
    [InlineData("5", "0")]
    public void A_box_that_is_not_a_fret_in_range_refuses_the_commit_and_stays_open(string fret, string width)
    {
        var rig = new InlineEditRig();
        rig.Inline.Fhp.Begin(1500, null);
        rig.Inline.Fhp.FretText = fret;
        rig.Inline.Fhp.WidthText = width;

        Assert.False(rig.Inline.Fhp.Commit());

        Assert.True(rig.Inline.Fhp.IsOpen);
        Assert.True(rig.Inline.Fhp.HasInvalidInput);
        Assert.Empty(rig.Chart.Editor.Anchors);
    }

    [Fact]
    public void A_section_name_commits_as_one_undo_step_with_the_dialog_fields()
    {
        var rig = new InlineEditRig();
        var section = rig.Inline.Section;

        section.Begin(1000, rig.Lead);
        section.Name = "chorus";
        Assert.True(section.CanCommit);
        Assert.True(section.Commit());

        Assert.False(section.IsOpen);
        var marker = Assert.Single(rig.Lead.EventPoints);
        Assert.Equal("chorus", marker.Section);
        Assert.Equal("chorus", marker.Phrase);
        Assert.Equal("chorus", rig.Events.LastSection);
        Assert.Equal(1, rig.UndoSteps());
        Assert.Empty(rig.Lead.EventPoints);
    }

    [Fact]
    public void A_partial_name_takes_the_first_suggestion_and_the_list_narrows_as_you_type()
    {
        var rig = new InlineEditRig();
        var section = rig.Inline.Section;
        section.Begin(1000, rig.Lead);

        section.Name = "";
        Assert.True(section.Suggestions.Count > 20);

        section.Name = "cho";
        Assert.Equal("chorus", section.Suggestions[0].Key);
        Assert.All(section.Suggestions, s => Assert.Contains("cho", s.Display + s.Key, StringComparison.OrdinalIgnoreCase));

        Assert.True(section.Commit());
        Assert.Equal("chorus", Assert.Single(rig.Lead.EventPoints).Section);
    }

    [Fact]
    public void A_name_that_is_no_section_refuses_the_commit_and_cancel_writes_nothing()
    {
        var rig = new InlineEditRig();
        var section = rig.Inline.Section;
        section.Begin(1000, rig.Lead);
        section.Name = "zzz";

        Assert.False(section.CanCommit);
        Assert.False(section.Commit());
        Assert.True(section.IsOpen);

        section.Cancel();
        Assert.False(section.IsOpen);
        Assert.Empty(rig.Lead.EventPoints);
        Assert.False(rig.Chart.History.CanUndo);
    }

    [Fact]
    public void The_box_starts_on_the_last_section_so_a_repeat_is_just_Enter()
    {
        var rig = new InlineEditRig();
        rig.Inline.Section.Begin(1000, rig.Lead);
        rig.Inline.Section.Name = "verse";
        rig.Inline.Section.Commit();

        rig.Inline.Section.Begin(3000, rig.Lead);

        Assert.Equal("verse", rig.Inline.Section.Name);
    }

    [Fact]
    public void More_hands_the_typed_name_to_the_full_dialog_and_writes_nothing()
    {
        var rig = new InlineEditRig();
        EventRequest? opened = null;
        rig.Inline.OpenEventDialog = request => opened = request;
        rig.Inline.Section.Begin(1000, rig.Lead);
        rig.Inline.Section.Name = "bridge";

        rig.Inline.ShowFullEventDialog();

        Assert.False(rig.Inline.Section.IsOpen);
        Assert.NotNull(opened);
        Assert.Equal("bridge", opened.SectionName);
        Assert.Same(rig.Lead, opened.Target);
        Assert.Empty(rig.Lead.EventPoints);
        Assert.False(rig.Chart.History.CanUndo);

        var dialog = new EventViewModel(rig.Chart.Services, rig.Events);
        dialog.Configure(opened);
        Assert.Equal("bridge", dialog.SectionName);
        Assert.Equal("bridge", dialog.PhraseName);
    }

    [Fact]
    public void One_editor_is_open_at_a_time_and_only_the_hosting_timeline_gets_one()
    {
        var rig = new InlineEditRig();
        var timeline = new object();
        Assert.False(rig.Inline.Hosts(timeline));
        rig.Inline.Host = timeline;
        Assert.True(rig.Inline.Hosts(timeline));
        Assert.False(rig.Inline.Hosts(new object()));

        rig.Inline.ShowFhp(1000, null);
        rig.Inline.ShowSection(1000, rig.Lead);

        Assert.False(rig.Inline.Fhp.IsOpen);
        Assert.True(rig.Inline.Section.IsOpen);

        rig.Inline.ShowLyric(1000, null);
        Assert.False(rig.Inline.Section.IsOpen);
        Assert.True(rig.Inline.Lyric.IsOpen);
        rig.Inline.ShowFhp(1000, null);
        Assert.False(rig.Inline.Lyric.IsOpen);
        Assert.True(rig.Inline.Fhp.IsOpen);
    }
}
