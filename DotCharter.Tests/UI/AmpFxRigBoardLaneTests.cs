using System.Collections.Specialized;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Fire board's LANE FOLD (<c>AmpFxRigViewModel.BoardSegments</c>): a
///     serial chain is one horizontal run; a live split region folds into
///     stacked lanes between its junction pair and merges back — the owner's
///     "it should split and let above and below have units and then merge back",
///     which the flat board could not draw. The fold goes through the SAME pure
///     <c>SplitRegionLayout</c> the native routing contract is mirrored
///     through, and it is RECONCILED, never cleared — the same no-Reset,
///     instance-survival rules <c>AmpFxRigChainMirrorTests</c> pins for the
///     chain mirrors.
/// </summary>
public sealed class AmpFxRigBoardLaneTests
{
    // The default fake catalogue has no routing nodes; the split flow needs them.
    private static IReadOnlyList<DspModuleType> SplitCatalog() =>
    [
        new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("reverb", "Reverb", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("amp", "Amp", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f)]),
        new DspModuleType("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new DspModuleType("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f)]),
    ];

    private static (AmpFxRigViewModel Screen, PedalboardViewModel Rig) NewScreen()
    {
        var rig = TestRigBuilders.Pedalboard(
            new FakeDspChainService(SplitCatalog()), new FakeDspTonePresetService(), FakeLocalization.English());
        return (new AmpFxRigViewModel(rig), rig);
    }

    private static PedalNodeViewModel Add(PedalboardViewModel rig, string id) =>
        rig.AddGear(rig.AvailableModules.First(m => m.Id == id))!;

    /// <summary>Builds od → splitter [delay(A) | reverb(B)] merger → amp — the
    /// canonical split chain the assertions below read.</summary>
    private static (AmpFxRigViewModel Screen, PedalboardViewModel Rig,
        PedalNodeViewModel Delay, PedalNodeViewModel Reverb) SplitChain()
    {
        var (screen, rig) = NewScreen();
        Add(rig, "overdrive");
        Add(rig, "splitter"); // auto-pairs the merger right after it
        var delay = Add(rig, "delay");
        var reverb = Add(rig, "reverb");
        Add(rig, "amp");
        // Seat the two effects INSIDE the region (before the merger), one per lane.
        var merger = rig.Nodes.First(n => n.IsMerger);
        Assert.True(rig.DropToRow(delay, rig.Nodes.IndexOf(merger), branch: 1));
        Assert.True(rig.DropToRow(reverb, rig.Nodes.IndexOf(merger), branch: 2));
        return (screen, rig, delay, reverb);
    }

    [Fact]
    public void ASerialChain_IsOneSerialRun_InChainOrder()
    {
        var (screen, rig) = NewScreen();
        Add(rig, "overdrive");
        Add(rig, "amp");

        var segment = Assert.IsType<RigSerialSegment>(Assert.Single(screen.BoardSegments));
        Assert.Equal(rig.Nodes.ToArray(), segment.Items.ToArray());
    }

    [Fact]
    public void ASplitChain_FoldsIntoLanes_WithMembershipByBranch()
    {
        var (screen, rig, delay, reverb) = SplitChain();

        Assert.Equal(3, screen.BoardSegments.Count);
        var head = Assert.IsType<RigSerialSegment>(screen.BoardSegments[0]);
        var split = Assert.IsType<RigSplitSegment>(screen.BoardSegments[1]);
        var tail = Assert.IsType<RigSerialSegment>(screen.BoardSegments[2]);

        Assert.Equal(["Overdrive"], head.Items.Select(n => n.DisplayName));
        Assert.Equal(["Amp"], tail.Items.Select(n => n.DisplayName));

        Assert.True(split.Splitter.IsSplitter);
        Assert.NotNull(split.Merger);
        Assert.False(split.HasAutoMerge);
        Assert.Equal(2, split.Lanes.Count);
        Assert.Equal("A", split.Lanes[0].Label);
        Assert.Equal("B", split.Lanes[1].Label);
        Assert.Same(delay, Assert.Single(split.Lanes[0].Items));
        Assert.Same(reverb, Assert.Single(split.Lanes[1].Items));
    }

    /// <summary>A branch re-tag never touches the Nodes collection — the fold
    /// must still move the unit between lanes (the routing properties are a
    /// LAYOUT input), and it must do it by RECONCILING: no Reset anywhere, and
    /// the untouched lane keeps its instances.</summary>
    [Fact]
    public void RetaggingABranch_MovesTheUnitBetweenLanes_WithoutAnyReset()
    {
        var (screen, rig, delay, reverb) = SplitChain();
        var split = Assert.IsType<RigSplitSegment>(screen.BoardSegments[1]);
        var laneA = split.Lanes[0];
        var laneB = split.Lanes[1];

        var resets = 0;
        void Count(object? s, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        }

        screen.BoardSegments.CollectionChanged += Count;
        split.Lanes.CollectionChanged += Count;
        laneA.Items.CollectionChanged += Count;
        laneB.Items.CollectionChanged += Count;

        // delay: lane A → lane B, same chain slot.
        Assert.True(rig.DropToRow(delay, rig.Nodes.IndexOf(delay), branch: 2));

        Assert.Equal(0, resets);
        Assert.Same(split, screen.BoardSegments[1]); // the segment survived
        Assert.Same(laneA, split.Lanes[0]);          // …and so did both lanes
        Assert.Same(laneB, split.Lanes[1]);
        Assert.Empty(laneA.Items);
        Assert.Equal(
            [delay, reverb],
            laneB.Items.ToHashSet());
    }

    [Fact]
    public void WideningTheRegion_AddsLaneC_AndKeepsLanesAAndB()
    {
        var (screen, rig, _, _) = SplitChain();
        var split = Assert.IsType<RigSplitSegment>(screen.BoardSegments[1]);
        var laneA = split.Lanes[0];
        var laneB = split.Lanes[1];

        rig.RoutingSplit3Command.Execute(null);

        Assert.Same(split, screen.BoardSegments[1]);
        Assert.Equal(3, split.Lanes.Count);
        Assert.Same(laneA, split.Lanes[0]);
        Assert.Same(laneB, split.Lanes[1]);
        Assert.Equal("C", split.Lanes[2].Label);
        Assert.Empty(split.Lanes[2].Items);
    }

    /// <summary>SERIES removes the junction pair; the body pedals fold back into
    /// ONE serial run, keeping their view-model instances (their realized cards
    /// keep their containers in every host).</summary>
    [Fact]
    public void RoutingSerial_FoldsBackToOneRun_AndTheUnitsKeepTheirInstances()
    {
        var (screen, rig, delay, reverb) = SplitChain();

        rig.RoutingSerialCommand.Execute(null);

        var segment = Assert.IsType<RigSerialSegment>(Assert.Single(screen.BoardSegments));
        Assert.Equal(rig.Nodes.ToArray(), segment.Items.ToArray());
        Assert.Contains(delay, segment.Items);
        Assert.Contains(reverb, segment.Items);
    }

    /// <summary>A region whose merger is gone runs to the chain end and the
    /// engine unity-averages the branches — the segment says so.</summary>
    [Fact]
    public void ARegionWithoutAMerger_ReportsAutoMerge()
    {
        var (screen, rig, _, _) = SplitChain();
        var merger = rig.Nodes.First(n => n.IsMerger);

        rig.RemoveCommand.Execute(merger);

        var split = screen.BoardSegments.OfType<RigSplitSegment>().Single();
        Assert.True(split.HasAutoMerge);
        Assert.Null(split.Merger);
        // …and the trailing amp is INSIDE the region now (row 1), not a tail run.
        Assert.Equal(2, screen.BoardSegments.Count);
    }

    // ── the lane-aware insertion gap ─────────────────────────────────────────

    [Fact]
    public void ArmingAGapInsideALane_CarriesTheLanesBranch()
    {
        var (screen, _, _, reverb) = SplitChain();

        screen.InsertBeforeCommand.Execute(reverb);

        Assert.True(screen.HasInsertPoint);
        Assert.Equal(2, screen.InsertBranch);

        // …and clearing the gap clears the lane half with it.
        screen.ClearInsertPointCommand.Execute(null);
        Assert.Null(screen.InsertBranch);
    }

    [Fact]
    public void AddingGearAtALaneGap_SeatsTheUnitInThatLane()
    {
        var (screen, rig, _, _) = SplitChain();
        var split = screen.BoardSegments.OfType<RigSplitSegment>().Single();

        // The empty-lane path: widen to 3 lanes, arm lane C's trailing gap.
        rig.RoutingSplit3Command.Execute(null);
        screen.InsertIntoLaneCommand.Execute(split.Lanes[2]);
        Assert.True(screen.HasInsertPoint);
        Assert.Equal(3, screen.InsertBranch);

        var od = screen.GearGroups.SelectMany(g => g.Items).First(i => i.Type.Id == "overdrive");
        screen.AddGearCommand.Execute(od);

        var added = Assert.Single(split.Lanes[2].Items);
        Assert.Equal("Overdrive", added.DisplayName);
        Assert.Equal(3, added.Branch);
        Assert.False(screen.HasInsertPoint); // spent
        Assert.Null(screen.InsertBranch);
    }

    /// <summary>A SERIAL gap must stay branchless — otherwise every insert in
    /// front of the amp would tag the new unit into a lane.</summary>
    [Fact]
    public void ArmingASerialGap_CarriesNoBranch()
    {
        var (screen, rig, _, _) = SplitChain();
        var amp = rig.Nodes.First(n => n.DisplayName == "Amp");

        screen.InsertBeforeCommand.Execute(amp);

        Assert.True(screen.HasInsertPoint);
        Assert.Null(screen.InsertBranch);
    }

    // ── the drop controller's lane seams ─────────────────────────────────────

    /// <summary>An empty lane is a REAL drop target now: its fallback slot is
    /// the region's body start (right after the splitter), and a lane that is
    /// no longer on the board refuses rather than guesses.</summary>
    [Fact]
    public void BodyStartOf_IsTheSlotAfterTheSplitter_AndRefusesAStrayLane()
    {
        var (screen, rig, _, _) = SplitChain();
        var split = screen.BoardSegments.OfType<RigSplitSegment>().Single();

        var splitterIndex = rig.Nodes.IndexOf(split.Splitter);
        Assert.Equal(splitterIndex + 1, screen.BodyStartOf(split.Lanes[0]));
        Assert.Equal(splitterIndex + 1, screen.BodyStartOf(split.Lanes[1]));

        Assert.Equal(-1, screen.BodyStartOf(new RigBoardLane(2, FakeLocalization.English())));
    }

    /// <summary>The lane's <c>IsEmpty</c> is what swaps the run for the
    /// full-size drop zone, so it must be OBSERVABLE — a flag the fold flips
    /// without an event leaves the zone painted over the first card.</summary>
    [Fact]
    public void ALane_ReportsIsEmpty_Observably()
    {
        var (screen, rig, delay, _) = SplitChain();
        var split = screen.BoardSegments.OfType<RigSplitSegment>().Single();
        var laneA = split.Lanes[0];
        var laneB = split.Lanes[1];

        Assert.False(laneA.IsEmpty);
        var raised = 0;
        laneA.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RigBoardLane.IsEmpty)) raised++;
        };

        // delay: lane A → lane B; lane A drains and says so.
        Assert.True(rig.DropToRow(delay, rig.Nodes.IndexOf(delay), branch: 2));
        Assert.True(laneA.IsEmpty);
        Assert.False(laneB.IsEmpty);
        Assert.True(raised > 0, "IsEmpty changed without a PropertyChanged");
    }

    /// <summary>
    ///     The CROSS-RUN drop, end to end at the view-model seam: the pure gap
    ///     mapping (<c>TargetFlatIndexInto</c>) + <c>DropToRow</c> move a unit
    ///     from the serial head into a lane, between lanes, and into an EMPTY
    ///     lane via the body-start fallback — the exact commit the drag
    ///     controller runs on release.
    /// </summary>
    [Fact]
    public void DroppingAcrossRuns_ReBranchesAndReorders_LikeTheDragCommit()
    {
        var (screen, rig, delay, reverb) = SplitChain();
        var od = rig.Nodes.First(n => n.DisplayName == "Overdrive");

        // The split segment is RE-QUERIED after every drop, exactly as the
        // drag controller resolves its target per gesture: moving the serial
        // head INTO the region removes the head run, and the index-keyed
        // reconcile then rebuilds the segment at its new index — a cached
        // lane object is a detached mirror after that.
        RigSplitSegment Split() => screen.BoardSegments.OfType<RigSplitSegment>().Single();

        // serial → lane B, tail gap (gap 1 of a one-item run).
        var laneB = Split().Lanes[1];
        var runFlat = laneB.Items.Select(n => rig.Nodes.IndexOf(n)).ToList();
        var target = DotCharter.Tone.UI.Views.PlayMode.RigBoardDragController.TargetFlatIndexInto(
            runFlat, rig.Nodes.IndexOf(od), gapInRun: 1, screen.BodyStartOf(laneB));
        Assert.True(rig.DropToRow(od, target, laneB.Branch));
        laneB = Split().Lanes[1];
        Assert.Equal([reverb, od], laneB.Items.ToArray()); // tail gap really is AFTER reverb

        // lane B → lane A (delay's lane), head gap: od re-branches again.
        var laneA = Split().Lanes[0];
        runFlat = laneA.Items.Select(n => rig.Nodes.IndexOf(n)).ToList();
        target = DotCharter.Tone.UI.Views.PlayMode.RigBoardDragController.TargetFlatIndexInto(
            runFlat, rig.Nodes.IndexOf(od), gapInRun: 0, screen.BodyStartOf(laneA));
        Assert.True(rig.DropToRow(od, target, laneA.Branch));
        Assert.Equal([od, delay], Split().Lanes[0].Items.ToArray());

        // drain lane B, then drop delay into the EMPTY lane via the fallback.
        Assert.True(rig.DropToRow(reverb, rig.Nodes.IndexOf(reverb), branch: 1));
        laneB = Split().Lanes[1];
        Assert.True(laneB.IsEmpty);
        target = DotCharter.Tone.UI.Views.PlayMode.RigBoardDragController.TargetFlatIndexInto(
            [], rig.Nodes.IndexOf(delay), gapInRun: 0, screen.BodyStartOf(laneB));
        Assert.True(target >= 0);
        Assert.True(rig.DropToRow(delay, target, laneB.Branch));
        Assert.Same(delay, Assert.Single(Split().Lanes[1].Items));
    }
}
