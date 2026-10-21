using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     #88: whole-chart tools that apply outside truth to the chart (Guitar Pro import, Sync
///     arrangement, Refret, the tempo-map tools) are NEVER propagated: a placement they make differ
///     is unlinked, its content kept. The generators keep their links instead
///     (<see cref="LinkedMeasuresGeneratorTests" />). And a change to a
///     placement's MEASURES (a beat inserted or removed inside it, a time signature) unlinks that
///     placement under every policy, while a tempo-only change never does.
/// </summary>
public sealed class LinkedMeasuresToolPolicyTests
{
    private static FractionalPosition Bent(int measure) => At(measure, 1);

    /// <summary>The Guitar Pro import's path (technique-only and diff re-imports apply through it).</summary>
    [Fact]
    public void A_single_undo_step_tool_that_changes_one_copy_unlinks_it_and_never_propagates()
    {
        var t = new LinkToolHarness();
        var group = t.H.Link((t.H.Lead, 1), (t.H.Lead, 4), (t.H.Lead, 7));

        Assert.True(t.Maintenance.RunAsSingleUndoStep(() => LinkHarness.NoteAt(t.H.Lead, Bent(4), 1).Fret = 12));

        Assert.Equal(12, LinkHarness.NoteAt(t.H.Lead, Bent(4), 1).Fret);
        Assert.Equal(7, LinkHarness.NoteAt(t.H.Lead, Bent(1), 1).Fret);
        Assert.Equal(7, LinkHarness.NoteAt(t.H.Lead, Bent(7), 1).Fret);
        LinkHarness.AssertLinkedAndEqual(t.H, group, 2);
        Assert.DoesNotContain(group.Placements, p => p.Position == At(4, 0));

        // One undo step: the tool and the unlink together.
        t.H.History.Undo();
        Assert.Equal(7, LinkHarness.NoteAt(t.H.Lead, Bent(4), 1).Fret);
        LinkHarness.AssertLinkedAndEqual(t.H, t.H.Project.LinkGroups.Single(), 3);
    }

    /// <summary>Ordinary edits wrapped with the unlink policy: same outcome.</summary>
    [Fact]
    public void An_unlink_policy_composite_unlinks_the_copy_it_changed()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));

        Assert.True(h.History.Do(new CompositeAction([h.Mutate(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(7), 1), n => n.Fret = 5)])
        {
            LinkPolicyOverride = LinkPolicy.UnlinkOnDivergence
        }));

        Assert.Equal(7, LinkHarness.NoteAt(h.Lead, Bent(1), 1).Fret);
        Assert.Equal(5, LinkHarness.NoteAt(h.Lead, Bent(7), 1).Fret);
        LinkHarness.AssertLinkedAndEqual(h, group, 2);
    }

    /// <summary>
    ///     Sync arrangement copies Lead's fingering onto Rhythm sound by sound. Lead differs in bar 4
    ///     only, so Rhythm's bar-4 copy comes out different and is unlinked; bars 1 and 7 stay linked
    ///     and keep their own fingering.
    /// </summary>
    [Fact]
    public void Sync_arrangement_unlinks_the_copy_it_made_differ()
    {
        var t = new LinkToolHarness();
        var group = t.H.Link((t.H.Rhythm, 1), (t.H.Rhythm, 4), (t.H.Rhythm, 7));
        LinkHarness.NoteAt(t.H.Lead, Bent(4), 1).LeftHandFinger = 2;

        t.Maintenance.SyncArrangement(t.H.Lead, t.H.Rhythm);

        Assert.Equal(2, LinkHarness.NoteAt(t.H.Rhythm, Bent(4), 1).LeftHandFinger);
        Assert.Equal(3, LinkHarness.NoteAt(t.H.Rhythm, Bent(1), 1).LeftHandFinger);
        Assert.Equal(3, LinkHarness.NoteAt(t.H.Rhythm, Bent(7), 1).LeftHandFinger);
        LinkHarness.AssertLinkedAndEqual(t.H, group, 2);
        Assert.DoesNotContain(group.Placements, p => p.Position == At(4, 0));
    }

    /// <summary>
    ///     Refret rewrites the whole chart. Whatever it does, the links never change its result (the
    ///     chart is exactly what Refret makes of the same chart without links), and every link that
    ///     survives still holds equal copies.
    /// </summary>
    [Fact]
    public void Refret_output_is_never_altered_by_links_and_surviving_links_hold_equal_copies()
    {
        var linked = new LinkToolHarness();
        var group = linked.H.Link((linked.H.Lead, 1), (linked.H.Lead, 4), (linked.H.Lead, 7));
        var plain = new LinkToolHarness();
        var before = linked.H.Snapshot();

        linked.Maintenance.Refret();
        plain.Maintenance.Refret();

        Assert.NotEqual(before, linked.H.Snapshot()); // Refret really rewrote the chart
        Assert.Equal(plain.H.Snapshot(), linked.H.Snapshot());
        foreach (var survivor in linked.H.Project.LinkGroups)
            LinkHarness.AssertLinkedAndEqual(linked.H, survivor, survivor.Placements.Count);
        _ = group;
    }

    [Fact]
    public void Inserting_a_beat_inside_a_placement_unlinks_it_and_shifts_the_others()
    {
        var t = new LinkToolHarness();
        var group = t.H.Link((t.H.Lead, 1), (t.H.Lead, 4), (t.H.Lead, 7));
        t.H.State.CurrentTimeMs = t.H.Project.Beats[18].Time; // bar 4, beat 3

        t.Maintenance.InsertBeatAtPlayhead();

        LinkHarness.AssertLinkedAndEqual(t.H, group, 2);
        Assert.Equal([new FractionalPosition(4), new FractionalPosition(29)], group.Placements.Select(p => p.Position));

        t.H.History.Undo();
        LinkHarness.AssertLinkedAndEqual(t.H, t.H.Project.LinkGroups.Single(), 3);
    }

    [Fact]
    public void Removing_a_beat_inside_a_placement_unlinks_it()
    {
        var t = new LinkToolHarness();
        var group = t.H.Link((t.H.Lead, 1), (t.H.Lead, 4), (t.H.Lead, 7));
        t.H.State.CurrentTimeMs = t.H.Project.Beats[6].Time; // bar 1, beat 3

        t.Maintenance.RemoveBeatAtPlayhead();

        LinkHarness.AssertLinkedAndEqual(t.H, group, 2);
        Assert.DoesNotContain(group.Placements, p => p.Position == At(1, 0));
    }

    /// <summary>A time-signature change inside a placement unlinks it — even under the
    /// <see cref="LinkPolicy.Retain" /> policy a beat edit carries.</summary>
    [Fact]
    public void A_time_signature_change_inside_a_placement_unlinks_it()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var beats = h.Project.Beats;
        var newBeats = beats.Select(b => b.Clone()).ToList();
        newBeats[16].BeatsInMeasure = 3;

        Assert.True(h.History.Do(new MutateBeatsAction(beats, beats.Select(b => b.Clone()), newBeats)));

        LinkHarness.AssertLinkedAndEqual(h, group, 2);
        Assert.DoesNotContain(group.Placements, p => p.Position == At(4, 0));
    }

    /// <summary>A capo change makes Rhythm unable to share with Lead: its placements are unlinked
    /// under the Retain policy the settings edit carries, and undo links them again.</summary>
    [Fact]
    public void An_arrangement_that_can_no_longer_share_is_unlinked()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4));
        var before = ChartProjectCloner.CloneArrangement(h.Rhythm);
        var after = ChartProjectCloner.CloneArrangement(h.Rhythm);
        after.Capo = 2;

        Assert.True(h.History.Do(new DotCharter.Services.Editing.Arrangements.MutateArrangementSettingsAction(h.Rhythm, before, after, null)));

        LinkHarness.AssertLinkedAndEqual(h, group, 3);
        Assert.All(group.Placements, p => Assert.Same(h.Lead, p.Arrangement));
        h.History.Undo();
        LinkHarness.AssertLinkedAndEqual(h, h.Project.LinkGroups.Single(), 4);
    }

    /// <summary>Tempo-only changes never unlink — not even in milliseconds spacing, where a slower
    /// bar changes how many beats the clean-up gap is.</summary>
    [Theory]
    [InlineData(MinNoteDistanceMode.MeasureFraction)]
    [InlineData(MinNoteDistanceMode.Milliseconds)]
    public void A_tempo_only_change_never_unlinks(MinNoteDistanceMode mode)
    {
        var h = LinkHarness.WithRhythm(5, Config(mode, 60));
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var beats = h.Project.Beats;
        var newBeats = beats.Select(b => b.Clone()).ToList();
        // Bar 4 slows to 40 % of its tempo; everything after it moves later.
        var time = beats[16].Time;
        for (var i = 17; i < newBeats.Count; i++)
        {
            var gap = beats[i].Time - beats[i - 1].Time;
            time += i <= 20 ? gap * 2.5 : gap;
            newBeats[i].Time = time;
        }

        Assert.True(h.History.Do(new MutateBeatsAction(beats, beats.Select(b => b.Clone()), newBeats)));

        Assert.Equal(3, group.Placements.Count);
        Assert.Contains(group, h.Project.LinkGroups);
    }
}
