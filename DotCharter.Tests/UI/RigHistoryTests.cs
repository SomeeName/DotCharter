using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig's undo / redo (Ctrl+Z / Ctrl+Y) and its unit clipboard (Ctrl+C / X /
///     V, Ctrl+D, Del, Ctrl+B): the pure history's rules, then the rig itself over a
///     fake chain with the REAL preset service, so an undo really puts the chain back.
///     The keys and the right-click menu are <c>Perf/RigShortcutsUiPerfSmokeTests</c>.
/// </summary>
public sealed class RigHistoryTests
{
    private sealed record State(string Name);

    private static RigHistory<State> NewHistory() => new((a, b) => a.Name == b.Name);

    [Fact]
    public void ABurstOfEdits_IsOneStep_AndAPauseStartsTheNext()
    {
        var h = NewHistory();
        h.Reset(new State("a"));
        h.Edited(new State("b"), 1000);
        h.Edited(new State("c"), 1100);          // same drag
        h.Edited(new State("d"), 1000 + 5000);   // a new edit, later

        Assert.Equal(2, h.UndoDepth);
        Assert.Equal("c", h.Undo(new State("d"))!.Name);
        Assert.Equal("a", h.Undo(new State("c"))!.Name);
        Assert.Null(h.Undo(new State("a")));
        Assert.Equal("c", h.Redo(new State("a"))!.Name);
        Assert.Equal("d", h.Redo(new State("c"))!.Name);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void AnEditThatChangedNothing_IsNoStep_AndANewEditForksHistory()
    {
        var h = NewHistory();
        h.Reset(new State("a"));
        h.Edited(new State("a"), 1000);
        Assert.False(h.CanUndo);

        h.Edited(new State("b"), 5000);
        h.Undo(new State("b"));
        Assert.True(h.CanRedo);
        h.Edited(new State("x"), 20000);
        Assert.False(h.CanRedo); // a new edit after an undo forks
        Assert.Equal("a", h.Undo(new State("x"))!.Name);
    }

    [Fact]
    public void ANoteThatChangedNothing_OpensNoBurst_SoTheRealEditRightAfterIsAStep()
    {
        // a card raises "Value" before its write reaches the chain, so the first
        // note of a drag can read the rig unchanged
        var h = NewHistory();
        h.Reset(new State("a"));
        h.Edited(new State("a"), 1000);
        h.Edited(new State("b"), 1100);   // inside what would have been that burst
        Assert.Equal("a", h.Undo(new State("b"))!.Name);
    }

    [Fact]
    public void AClockThatWentBackwards_StartsANewStep()
    {
        var h = NewHistory();
        h.Reset(new State("a"));
        h.Edited(new State("b"), 90_000_000);
        h.Edited(new State("c"), 1_000);   // a different clock took over
        Assert.Equal(2, h.UndoDepth);
    }

    [Fact]
    public void TheStack_IsBounded_AndResetEmptiesIt()
    {
        var h = NewHistory();
        h.Reset(new State("0"));
        for (var i = 1; i <= RigHistory<State>.MaxDepth + 20; i++) h.Edited(new State(i.ToString()), i * 10_000L);
        Assert.Equal(RigHistory<State>.MaxDepth, h.UndoDepth);
        h.Reset(new State("fresh"));
        Assert.False(h.CanUndo || h.CanRedo);
    }

    // ── the rig ──────────────────────────────────────────────────────────────

    private static readonly DspModuleType Drive = new("overdrive", "Overdrive", [new("drive", "Drive", 0, 1, 0.5f), new("level", "Level", 0, 1, 0.5f)]);
    private static readonly DspModuleType Delay = new("delay", "Delay", [new("mix", "Mix", 0, 1, 0.3f)]);

    private sealed class Clock
    {
        public long Now = 1_000_000;
        public void Pause() => Now += 10_000;
    }

    private static (PedalboardViewModel Rig, FakeDspChainService Chain, Clock Clock) NewRig()
    {
        var chain = new FakeDspChainService(new[] { Drive, Delay });
        var rig = TestRigBuilders.Pedalboard(chain, new DspTonePresetService(chain, new FakeConfigurationService()), FakeLocalization.English());
        var clock = new Clock();
        rig.HistoryClock = () => clock.Now;
        return (rig, chain, clock);
    }

    [Fact]
    public void UndoAndRedo_PutAKnobTurnBackAndForward_AsOneStepPerDrag()
    {
        var (rig, _, clock) = NewRig();
        using var _ = rig;
        rig.AddGear(Drive);
        clock.Pause();
        var drive = rig.Nodes[0].Params.Single(p => p.Id == "drive");

        // one drag: many writes close together
        foreach (var v in new[] { 0.55f, 0.6f, 0.7f, 0.8f }) drive.Value = v;
        clock.Pause();
        rig.Nodes[0].Bypassed = true;
        clock.Pause();
        Assert.True(rig.CanUndo);

        // the step before the bypass is where the drag ENDED, not one write short
        rig.UndoCommand.Execute(null);
        Assert.False(rig.Nodes[0].Bypassed);
        Assert.Equal(0.8f, rig.Nodes[0].Params.Single(p => p.Id == "drive").Value, 3);

        rig.UndoCommand.Execute(null);                           // the whole drag is one step
        Assert.Equal(0.5f, rig.Nodes[0].Params.Single(p => p.Id == "drive").Value, 3);
        Assert.True(rig.CanRedo);

        rig.RedoCommand.Execute(null);
        Assert.Equal(0.8f, rig.Nodes[0].Params.Single(p => p.Id == "drive").Value, 3);
    }

    [Fact]
    public void Undo_PutsARemovedUnitBack_AndCanWalkBackToTheEmptyRig()
    {
        var (rig, _, clock) = NewRig();
        using var _ = rig;
        rig.AddGear(Drive);
        clock.Pause();
        rig.AddGear(Delay);
        clock.Pause();
        rig.SelectNodeAt(1);
        rig.DeleteSelectedCommand.Execute(null);                 // Del
        Assert.Equal(["overdrive"], rig.Nodes.Select(n => n.ModuleTypeId));

        rig.UndoCommand.Execute(null);
        Assert.Equal(["overdrive", "delay"], rig.Nodes.Select(n => n.ModuleTypeId));
        rig.UndoCommand.Execute(null);
        Assert.Equal(["overdrive"], rig.Nodes.Select(n => n.ModuleTypeId));
        rig.UndoCommand.Execute(null);
        Assert.Empty(rig.Nodes);
        Assert.False(rig.CanUndo);
    }

    [Fact]
    public void CopyPaste_PutsACopyWithItsValuesAfterTheSelection_AndCutMovesIt()
    {
        var (rig, _, clock) = NewRig();
        using var _ = rig;
        var drive = rig.AddGear(Drive)!;
        rig.AddGear(Delay);
        drive.Params.Single(p => p.Id == "drive").Value = 0.9f;
        drive.Bypassed = true;
        clock.Pause();

        rig.SelectNodeAt(0);
        Assert.False(rig.CanPaste);
        rig.CopyCommand.Execute(null);                           // Ctrl+C: the selection
        Assert.True(rig.CanPaste);
        rig.SelectNodeAt(1);
        rig.PasteCommand.Execute(null);                          // Ctrl+V: after the selection
        Assert.Equal(["overdrive", "delay", "overdrive"], rig.Nodes.Select(n => n.ModuleTypeId));
        var copy = rig.Nodes[2];
        Assert.Same(copy, rig.SelectedNode);
        Assert.Equal(0.9f, copy.Params.Single(p => p.Id == "drive").Value, 3);
        Assert.True(copy.Bypassed);

        rig.SelectNodeAt(1);
        rig.CutCommand.Execute(null);                            // Ctrl+X
        Assert.Equal(["overdrive", "overdrive"], rig.Nodes.Select(n => n.ModuleTypeId));
        rig.SelectNodeAt(0);
        rig.PasteCommand.Execute(null);
        Assert.Equal(["overdrive", "delay", "overdrive"], rig.Nodes.Select(n => n.ModuleTypeId));
    }

    [Fact]
    public void DuplicateAndBypass_ActOnTheSelection()
    {
        var (rig, _, _) = NewRig();
        using var _ = rig;
        rig.AddGear(Drive);
        rig.SelectNodeAt(0);
        rig.ToggleSelectedBypassCommand.Execute(null);           // Ctrl+B
        Assert.True(rig.Nodes[0].Bypassed);
        rig.DuplicateSelectedCommand.Execute(null);              // Ctrl+D
        Assert.Equal(2, rig.Nodes.Count);
        rig.SelectNodeAt(-1);
        rig.DeleteSelectedCommand.Execute(null);                 // nothing selected: nothing removed
        Assert.Equal(2, rig.Nodes.Count);
    }
}
