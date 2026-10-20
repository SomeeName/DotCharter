using DotCharter.Services.Editing.DraftMerge;
using DotCharter.Services.Editing.FretAnchors;
using static DotCharter.Tests.Services.Editing.DraftMerge.DraftMergeFixture;

namespace DotCharter.Tests.Services.Editing.DraftMerge;

/// <summary>
///     Writing a reviewed chart-from-audio draft (#647): a merge verb is ONE undo node and one undo
///     restores the project exactly; a phrase that was empty and has notes now is refused, never
///     replaced; replacing takes the phrase's chords with the hand shapes over them and never touches
///     a shared chord template; a phrase that plays brings the anchor it needs, floored at the capo.
/// </summary>
public sealed class DraftMergeHostTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());

    private DraftMergeReviewHost Host() => new(_history, new FretAnchorGenerationService());

    private static string Notes(ChartLevel level) =>
        string.Join(";", level.Notes.Select(n => $"{n.Position.BeatId}:{n.StringIndex}/{n.Fret}"));

    [Fact]
    public void Merging_a_phrase_is_one_undo_node_and_undo_restores_the_project_exactly()
    {
        var f = new DraftMergeFixture();
        var before = ChartProjectCloner.Clone(f.Project);
        var host = Host();
        host.Hold(f.Plan([Note(0, 0, 3), Note(1, 1, 5), Note(9, 2, 7)]));
        var intro = host.Review!.Phrases[0];

        Assert.Equal(DraftMergeOutcome.Merged, host.Accept(intro));

        Assert.Equal("0:0/3;1:1/5", Notes(f.Level));
        Assert.DoesNotContain(f.Level.Notes, n => intro.Draft.Contains(n));
        Assert.NotEmpty(f.Level.Anchors);
        Assert.Equal(["chorus"], host.Review!.Phrases.Select(p => p.Name));
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();

        Assert.False(_history.CanUndo);
        Assert.Empty(DeepCompare.Diff("project", before, f.Project, new HashSet<string>()));
    }

    [Fact]
    public void Merge_all_is_one_undo_node_and_closes_the_review()
    {
        var f = new DraftMergeFixture();
        var before = ChartProjectCloner.Clone(f.Project);
        var host = Host();
        host.Hold(f.Plan([Note(0, 0, 3), Note(5, 1, 5), Note(9, 2, 7)]));

        Assert.Equal(DraftMergeOutcome.Merged, host.AcceptAll());

        Assert.Equal("0:0/3;5:1/5;9:2/7", Notes(f.Level));
        Assert.Null(host.Review);
        Assert.Single(_history.GetUndoSteps());
        _history.Undo();
        Assert.Empty(DeepCompare.Diff("project", before, f.Project, new HashSet<string>()));
    }

    [Fact]
    public void A_phrase_that_was_empty_and_has_notes_now_is_refused_and_leaves_the_review()
    {
        var f = new DraftMergeFixture();
        var host = Host();
        host.Hold(f.Plan([Note(0, 0, 3), Note(9, 2, 7)]));
        var intro = host.Review!.Phrases[0];
        f.Seed(Note(2, 4, 0));
        var before = ChartProjectCloner.Clone(f.Project);

        Assert.Equal(DraftMergeOutcome.Stale, host.Accept(intro));

        Assert.False(_history.CanUndo);
        Assert.Empty(DeepCompare.Diff("project", before, f.Project, new HashSet<string>()));
        Assert.Equal(["chorus"], host.Review!.Phrases.Select(p => p.Name));
    }

    [Fact]
    public void Replacing_takes_the_phrases_chords_and_their_hand_shapes_and_never_touches_a_template()
    {
        var f = new DraftMergeFixture();
        var template = new ChordTemplate { Id = 0, Name = "G5" };
        f.Arrangement.ChordTemplates.Add(template);
        f.Level.Chords.Add(Chord(5, 0, (0, 3), (1, 5)));
        f.Level.HandShapes.Add(new HandShape { Position = new FractionalPosition(5), EndPosition = new FractionalPosition(6), TemplateId = 0 });
        f.Seed(Note(4, 0, 3), Note(9, 0, 3));
        var templateBefore = template.Clone();
        var before = ChartProjectCloner.Clone(f.Project);
        var host = Host();
        host.Hold(f.Plan([Note(4, 0, 5), Note(6, 2, 2)], replaceExisting: true));
        var verse = Assert.Single(host.Review!.Phrases);

        Assert.Equal(DraftMergeOutcome.Merged, host.Accept(verse));

        Assert.Equal("4:0/5;6:2/2;9:0/3", Notes(f.Level));
        Assert.Empty(f.Level.Chords);
        Assert.Empty(f.Level.HandShapes);
        Assert.Same(template, Assert.Single(f.Arrangement.ChordTemplates));
        Assert.Empty(DeepCompare.Diff("template", templateBefore, template, new HashSet<string>()));

        _history.Undo();
        Assert.Empty(DeepCompare.Diff("project", before, f.Project, new HashSet<string>()));
    }

    [Fact]
    public void A_merged_phrase_restates_the_anchor_in_force_floored_at_the_capo()
    {
        var f = new DraftMergeFixture(capo: 3);
        f.Level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 1, 4));
        f.Seed(Note(0, 0, 0));
        var host = Host();
        host.Hold(f.Plan([Note(4, 1, 5), Note(5, 1, 7)]));

        host.Accept(host.Review!.Phrases.Single());

        var anchor = Assert.Single(f.Level.Anchors, a => a.Position == new FractionalPosition(4));
        Assert.Equal((4, 4), (anchor.Fret, anchor.Width));
    }

    [Fact]
    public void Rejecting_writes_nothing_and_the_last_row_closes_the_review()
    {
        var f = new DraftMergeFixture();
        var host = Host();
        var changes = 0;
        host.Changed += (_, _) => changes++;
        host.Hold(f.Plan([Note(0, 0, 3)]));

        host.Reject(host.Review!.Phrases[0]);

        Assert.Null(host.Review);
        Assert.False(_history.CanUndo);
        Assert.Empty(f.Level.Notes);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_review_whose_arrangement_is_gone_closes_without_writing()
    {
        var f = new DraftMergeFixture();
        var host = Host();
        host.Hold(f.Plan([Note(0, 0, 3)]));
        f.Project.Arrangements.Clear();

        Assert.Equal(DraftMergeOutcome.Gone, host.Accept(host.Review!.Phrases[0]));

        Assert.Null(host.Review);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void A_review_with_nothing_to_decide_is_not_held()
    {
        var f = new DraftMergeFixture();
        var host = Host();

        host.Hold(f.Plan([]));

        Assert.Null(host.Review);
    }
}
