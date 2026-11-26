using DotCharter.Services.Editing;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Tones;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     #1002: the AI structure pass (sections, phrases and the hand positions a phrase brings) and the
///     tone-slot verb applied to a project with a link group: the copies stay linked and identical
///     through apply, undo and redo.
/// </summary>
public sealed class LinkedCopiesAiStructureUndoTests
{
    private static AiStructureChange PhraseWithAnchor(Arrangement arrangement, int measure, double beat = 1)
    {
        var level = arrangement.Levels.IndexOf(LinkHarness.Hardest(arrangement));
        var at = At(measure, beat);
        return new AiStructureChange(AiStructureOp.Add, AiStructureKind.Phrase, null, null, at, "lick")
        {
            Anchors = [new AiStructureAnchor(level, at, 9, 4)]
        };
    }

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(1, 1.0)]
    [InlineData(4, 0.0)]
    [InlineData(4, 1.0)]
    [InlineData(4, 3.0)]
    [InlineData(7, 0.0)]
    [InlineData(7, 2.0)]
    [InlineData(0, 0.0)]
    [InlineData(2, 0.0)]
    [InlineData(3, 1.0)]
    [InlineData(3, 3.0)]
    [InlineData(5, 0.0)]
    [InlineData(6, 1.0)]
    public void An_ai_structure_pass_apply_undo_and_redo_keep_the_copies_identical(int measure, double beat)
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var action = AiStructureApplier.Build(h.Lead, [PhraseWithAnchor(h.Lead, measure, beat)], out var applied, out _);
        Assert.Equal(1, applied);

        Assert.True(h.History.Do(action!));
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
        var applyState = h.Snapshot();

        h.History.Undo();
        LinkHarness.AssertLinkedAndEqual(h, group, 3);

        h.History.Redo();
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
        Assert.Equal(applyState, h.Snapshot());
    }

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(1, 1.0)]
    [InlineData(4, 0.0)]
    [InlineData(4, 1.0)]
    [InlineData(4, 3.0)]
    [InlineData(3, 1.0)]
    [InlineData(5, 0.0)]
    public void An_ai_structure_pass_on_a_group_across_arrangements_keeps_the_copies_identical(int measure, double beat)
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Rhythm, 1), (h.Lead, 4), (h.Rhythm, 4));
        var rhythmChange = PhraseWithAnchor(h.Rhythm, measure, beat);
        var action = AiStructureApplier.Build(h.Rhythm, [rhythmChange], out _, out _);

        Assert.True(h.History.Do(action!));
        LinkHarness.AssertLinkedAndEqual(h, group, 4);
        var applyState = h.Snapshot();
        h.History.Undo();
        LinkHarness.AssertLinkedAndEqual(h, group, 4);
        h.History.Redo();
        LinkHarness.AssertLinkedAndEqual(h, group, 4);
        Assert.Equal(applyState, h.Snapshot());
    }

    [Fact]
    public void An_ai_pass_that_anchors_two_copies_differently_keeps_every_linked_copy_identical()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var level = h.Lead.Levels.IndexOf(LinkHarness.Hardest(h.Lead));
        var change = new AiStructureChange(AiStructureOp.Add, AiStructureKind.Phrase, null, null, At(4, 1), "lick")
        {
            Anchors = [new AiStructureAnchor(level, At(4, 1), 9, 4), new AiStructureAnchor(level, At(7, 1), 2, 4)]
        };
        var action = AiStructureApplier.Build(h.Lead, [change], out _, out _);

        Assert.True(h.History.Do(action!));
        AssertGroupsConsistent(h);
        h.History.Undo();
        AssertGroupsConsistent(h);
        h.History.Redo();
        AssertGroupsConsistent(h);
    }

    private static void AssertGroupsConsistent(LinkHarness h)
    {
        foreach (var group in h.Project.LinkGroups)
            Assert.Single(h.Keys(group).Select(k => k?.Canonical).Distinct());
    }

    [Fact]
    public void The_tone_slot_verb_apply_undo_and_redo_keep_the_copies_identical()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var before = ToneSlotAssignment.Capture(h.Lead);
        var after = ToneSlotAssignment.FromSlots("a", "a", "", "", "");

        Assert.True(h.History.Do(new AssignArrangementTonesAction(h.Lead, before, after, "tone-slots")));
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
        h.History.Undo();
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
        h.History.Redo();
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
    }

    [Fact]
    public void Copies_that_differ_after_an_edit_are_logged_as_after_an_edit_not_as_an_undo()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        // A copy changed behind the history's back (a writer that does not go through it).
        var drift = h.Mutate(h.Lead, LinkHarness.Hardest(h.Lead).Notes.First(n => n.Position >= At(4, 0) && n.Position < At(5, 0)), n => n.Fret++);
        drift.Execute();
        var log = new FakeAppLogger();
        var history = new HistoryService(log, new EditingScope(h.State));

        var elsewhere = LinkHarness.Hardest(h.Lead).Notes.First(n => n.Position >= At(2, 0) && n.Position < At(3, 0));
        var edit = h.Mutate(h.Lead, elsewhere, n => n.Fret++);
        Assert.True(history.Do(edit));

        var warnings = log.Entries.Where(e => e.Level == "WARN" && e.Message.Contains("different copies")).Select(e => e.Message).ToList();
        Assert.NotEmpty(warnings);
        Assert.All(warnings, m => Assert.DoesNotContain("undo/redo", m));
        Assert.Contains(warnings, m => m.Contains("after an edit"));
    }

    [Fact]
    public void Diverged_copies_name_the_arrangement_the_bar_and_the_sustain_that_differs()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        // The copy at measure 4 gets a different sustain on its first note (a writer outside the history).
        var note = LinkHarness.Hardest(h.Lead).Notes.First(n => n.Position >= At(4, 0) && n.Position < At(5, 0));
        h.Mutate(h.Lead, note, n => n.Sustain = FractionalPosition.Zero).Execute();
        var log = new FakeAppLogger();
        var history = new HistoryService(log, new EditingScope(h.State));

        history.Do(h.Mutate(h.Lead, LinkHarness.Hardest(h.Lead).Notes.First(n => n.Position >= At(2, 0) && n.Position < At(3, 0)), n => n.Fret++));

        var message = log.Entries.First(e => e.Level == "WARN" && e.Message.Contains("different copies")).Message;
        Assert.Contains(h.Lead.Name, message);
        Assert.Contains("note sustain", message);
        Assert.Contains("bar +", message);
    }
}
