using DotCharter.Services.Editing.Collisions;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     #88: an edit inside one placement of a link group is copied to every other placement by
///     the history, IN THE SAME UNDO STEP; undo and redo restore, and never propagate.
/// </summary>
public sealed class LinkedMeasuresHistoryTests
{
    /// <summary>The pattern's bent note (string 1, fret 7) in bar <paramref name="measure" />.</summary>
    private static FractionalPosition Bent(int measure) => At(measure, 1);

    [Fact]
    public void An_edit_inside_one_placement_reaches_every_copy_in_one_undo_step()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4));
        var steps = h.History.GetUndoSteps().Count;

        Assert.True(h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(4), 1), n => n.Fret = 8));

        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(8, LinkHarness.NoteAt(h.Lead, Bent(1), 1).Fret);
        Assert.Equal(8, LinkHarness.NoteAt(h.Lead, Bent(7), 1).Fret);
        Assert.Equal(8, LinkHarness.NoteAt(h.Rhythm, Bent(4), 1).Fret);
        // Not linked: Rhythm's bar 1 is an independent copy.
        Assert.Equal(7, LinkHarness.NoteAt(h.Rhythm, Bent(1), 1).Fret);
        LinkHarness.AssertLinkedAndEqual(h, group, 4);

        // ONE Ctrl+Z takes back the edit AND its propagation.
        h.History.Undo();
        Assert.All(new[] { (h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4) },
            r => Assert.Equal(7, LinkHarness.NoteAt(r.Item1, Bent(r.Item2), 1).Fret));
        LinkHarness.AssertLinkedAndEqual(h, h.Project.LinkGroups.Single(), 4);

        h.History.Redo();
        Assert.All(new[] { (h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4) },
            r => Assert.Equal(8, LinkHarness.NoteAt(r.Item1, Bent(r.Item2), 1).Fret));
    }

    /// <summary>
    ///     Undo and redo put back EXACTLY the state they recorded, stored content and all — and a
    ///     copy changed behind the history's back is not fanned out by an undo: undo never
    ///     propagates, it only restores.
    /// </summary>
    [Fact]
    public void Undo_and_redo_restore_exactly_and_never_propagate()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var before = h.Snapshot();

        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(1), 1), n => n.Sustain = Beats(0.75));
        var after = h.Snapshot();
        Assert.NotEqual(before, after);

        h.History.Undo();
        Assert.Equal(before, h.Snapshot());
        h.History.Redo();
        Assert.Equal(after, h.Snapshot());

        // An edit outside every copy, then a change the history never saw, to ONE copy…
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(3, 1.5), 4), n => n.Fret = 9);
        var nodes = h.History.GetTree().Count;
        // …is copied nowhere by undoing or redoing the outside edit: they restore, they never propagate.
        LinkHarness.NoteAt(h.Lead, Bent(7), 1).Fret = 2;
        h.History.Undo();
        AssertOnlyTheUnseenChange(2);
        LinkHarness.NoteAt(h.Lead, Bent(7), 1).Fret = 3;
        h.History.Redo();
        AssertOnlyTheUnseenChange(3);
        Assert.Equal(nodes, h.History.GetTree().Count);

        void AssertOnlyTheUnseenChange(int fret)
        {
            Assert.Equal(7, LinkHarness.NoteAt(h.Lead, Bent(1), 1).Fret);
            Assert.Equal(7, LinkHarness.NoteAt(h.Lead, Bent(4), 1).Fret);
            Assert.Equal(fret, LinkHarness.NoteAt(h.Lead, Bent(7), 1).Fret);
        }
    }

    /// <summary>A gesture (wheel ticks on one note) is one undo step, its propagation included.</summary>
    [Fact]
    public void A_merged_gesture_and_its_propagation_are_one_undo_step()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var steps = h.History.GetUndoSteps().Count;
        var note = LinkHarness.NoteAt(h.Lead, Bent(4), 1);

        for (var tick = 0; tick < 3; tick++)
            Assert.True(h.Edit(h.Lead, note, n => n.Fret++, MergeVerbs.Fret));

        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.All(new[] { 1, 4, 7 }, m => Assert.Equal(10, LinkHarness.NoteAt(h.Lead, Bent(m), 1).Fret));

        h.History.Undo();
        Assert.All(new[] { 1, 4, 7 }, m => Assert.Equal(7, LinkHarness.NoteAt(h.Lead, Bent(m), 1).Fret));
        h.History.Redo();
        Assert.All(new[] { 1, 4, 7 }, m => Assert.Equal(10, LinkHarness.NoteAt(h.Lead, Bent(m), 1).Fret));
    }

    /// <summary>
    ///     Across arrangements a chord template is resolved by GRIP in the target and minted there when
    ///     it has none — never by id, and never by changing the target's own templates; undo takes the
    ///     minted template back with the edit.
    /// </summary>
    [Fact]
    public void A_chord_edit_mints_its_grip_in_the_other_arrangement_and_undo_removes_it()
    {
        var h = LinkHarness.WithRhythm();
        var alt = h.Lead.ChordTemplates[0].Clone();
        alt.Id = 1;
        alt.Name = "A5 (alt)";
        alt.Fingers = new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 3 };
        h.Lead.ChordTemplates.Add(alt);
        var group = h.Link((h.Lead, 4), (h.Rhythm, 1), (h.Rhythm, 7));
        var rhythmTemplates = h.Rhythm.ChordTemplates.Count;

        var chord = LinkHarness.Hardest(h.Lead).Chords.Single(c => c.Position == At(4, 2));
        var level = LinkHarness.Hardest(h.Lead);
        var changed = chord.Clone();
        changed.TemplateId = 1;
        Assert.True(h.History.Do(new MutateChordsAction(level.Chords, [chord], [chord.Clone()], [changed],
            () => ChartSpacingEnforcer.EnforceLevel(level, h.Project.Beats, h.Config))));

        LinkHarness.AssertLinkedAndEqual(h, group, 3);
        Assert.Equal(rhythmTemplates + 1, h.Rhythm.ChordTemplates.Count);
        var minted = h.Rhythm.ChordTemplates[^1];
        Assert.Equal(alt.Fingers, minted.Fingers);
        Assert.Equal(1, h.Rhythm.ChordTemplates[0].Fingers[1]);
        Assert.Equal(3, h.Rhythm.ChordTemplates[0].Fingers[2]); // the existing grip is untouched
        Assert.All(LinkHarness.Hardest(h.Rhythm).Chords.Where(c => c.Position == At(1, 2) || c.Position == At(7, 2)),
            c => Assert.Equal(minted.Id, c.TemplateId));

        h.History.Undo();
        Assert.Equal(rhythmTemplates, h.Rhythm.ChordTemplates.Count);
        Assert.All(LinkHarness.Hardest(h.Rhythm).Chords, c => Assert.Equal(0, c.TemplateId));
    }

    /// <summary>
    ///     One action that changes two copies DIFFERENTLY cannot be propagated: the copies are
    ///     split by content (the untouched copy keeps the group) and every edit is kept.
    /// </summary>
    [Fact]
    public void Copies_changed_differently_by_one_action_are_unlinked_and_keep_their_edits()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 1));

        Assert.True(h.History.Do(new CompositeAction(
        [
            h.Mutate(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(4), 1), n => n.Fret = 8),
            h.Mutate(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(7), 1), n => n.Fret = 9)
        ])));

        LinkHarness.AssertLinkedAndEqual(h, group, 2);
        Assert.Equal([At(1, 0), At(1, 0)], group.Placements.Select(p => p.Position));
        Assert.Equal(7, LinkHarness.NoteAt(h.Lead, Bent(1), 1).Fret);
        Assert.Equal(8, LinkHarness.NoteAt(h.Lead, Bent(4), 1).Fret);
        Assert.Equal(9, LinkHarness.NoteAt(h.Lead, Bent(7), 1).Fret);

        h.History.Undo();
        LinkHarness.AssertLinkedAndEqual(h, h.Project.LinkGroups.Single(), 4);
    }

    /// <summary>
    ///     A direct push on a project the history has never read (the first edit after a load, by a
    ///     caller that applied it itself) has no "before" to propagate from: the copy it changed is
    ///     unlinked rather than left linked and different.
    /// </summary>
    [Fact]
    public void A_first_direct_push_on_an_unread_project_never_leaves_a_copy_linked_and_different()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var fresh = new LinkHarness(h.Project, h.Config); // a new history: it has read nothing
        var action = fresh.Mutate(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(4), 1), n => n.Fret = 8);
        action.Execute();

        Assert.True(fresh.History.PushAction(action));

        LinkHarness.AssertLinkedAndEqual(fresh, group, 2);
        Assert.Equal(8, LinkHarness.NoteAt(h.Lead, Bent(4), 1).Fret);
    }

    [Fact]
    public void An_edit_outside_every_placement_touches_no_copy()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var copies = new[] { 1, 4, 7 }.Select(m => h.Integrity(h.Lead, m)).ToList();

        // The filler on bar 3 (string 4), between two copies.
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(3, 1.5), 4), n => n.Fret = 9);

        Assert.Equal(copies, new[] { 1, 4, 7 }.Select(m => h.Integrity(h.Lead, m)));
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
    }

    /// <summary>A sustain held to the next sound is re-fitted per copy (the next sound differs), and
    /// the copies still compare equal afterwards — on a drifting tempo map in milliseconds mode.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    public void A_held_sustain_is_refitted_to_each_copys_own_neighbourhood(int seed)
    {
        var h = LinkHarness.WithRhythm(seed, Config(MinNoteDistanceMode.Milliseconds, 40));
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4));

        // The note on beat 4 rings over the barline: make it ring for two whole bars.
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(4, 3), 2), n => n.Sustain = Beats(8));

        LinkHarness.AssertLinkedAndEqual(h, group, 4);
    }
}
