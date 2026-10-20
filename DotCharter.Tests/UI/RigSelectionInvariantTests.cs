using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     W3 (2026-09-16) — THE BLOCK SELECTED ON THE CANVAS AND THE BLOCK IN THE
///     EDITOR HEADER CAN NEVER DISAGREE, and the editor is always showing.
///     <para>
///         Both halves are here because they fail differently. The BEHAVIOURAL
///         one drives every public entry point that can move the selection and
///         compares identities; the SOURCE one pins that there is only one place
///         either side can be written from, so a second writer cannot appear and
///         make the first half true-by-luck for a while.
///     </para>
///     <para>
///         Pure + source-reading, so it runs in the DEFAULT gate. The visible
///         half — the editor measured non-zero with the drawer open — is
///         <c>Perf/RigSelectionOpensTheEditorUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class RigSelectionInvariantTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
    [
        new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("reverb", "Reverb", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("amp", "Amp", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f)]),
    ];

    private static (PedalboardViewModel Rig, AmpFxRigViewModel Screen) Screen()
    {
        var chain = new FakeDspChainService(Catalog());
        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        foreach (var t in chain.Catalog) rig.AddGear(t);
        return (rig, new AmpFxRigViewModel(rig));
    }

    /// <summary>
    ///     Every public entry point that moves the selection, checked after each
    ///     one: the editor's unit IS the canvas's selected node (same instance),
    ///     the editor is showing, and neither the drawer nor a previous HIDE can
    ///     leave it hidden.
    ///     <para>
    ///         Proven non-vacuous by removing the
    ///         <c>NodeSelectionRequested</c> subscription from
    ///         <c>AmpFxRigViewModel</c>: the re-select and browse-then-click
    ///         steps failed with the editor still hidden.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheEditorsUnit_IsAlwaysTheCanvasSelection_AndIsAlwaysShowing()
    {
        var (rig, screen) = Screen();
        var log = new List<string>();

        void Check(string step)
        {
            log.Add(step);
            if (rig.SelectedNode is null)
            {
                Assert.Null(screen.DockUnit);
                Assert.False(screen.HasDockUnit, $"after '{step}': the dock claims a unit with nothing selected");
                return;
            }

            Assert.True(screen.DockUnit is not null, $"after '{step}': nothing is docked but a node is selected");
            Assert.Same(rig.SelectedNode, screen.DockUnit!.Node);
            Assert.True(rig.SelectedNode.IsSelected,
                $"after '{step}': the canvas does not draw the node the editor is showing");
            Assert.True(screen.IsDockBodyVisible,
                $"after '{step}': a block is selected and its editor is NOT showing — " +
                $"dockOpen={screen.IsDockOpen} tonesMode={screen.IsTonesMode}");
            Assert.Single(rig.Nodes, n => n.IsSelected);
        }

        // 1. the app's own opening selection
        screen.EnsureEditorSelection();
        Check("EnsureEditorSelection");

        // 2. a tile click (SelectUnitCommand is what the board's Button raises)
        screen.SelectUnitCommand.Execute(rig.Nodes[2]);
        Check("tile click");

        // 3. …and a click on the block that is ALREADY selected. This one raises
        // no property change at all, which is why it needs its own signal.
        screen.ToggleDockCommand.Execute(null); // HIDE first, so the re-click has work to do
        Assert.False(screen.IsDockBodyVisible);
        screen.SelectUnitCommand.Execute(rig.Nodes[2]);
        Check("re-click the selected block");

        // 4. the drag controller's click-without-travel goes through the RIG's
        // own command, not the screen's
        rig.SelectNodeCommand.Execute(rig.Nodes[0]);
        Check("board drag controller click");

        // 5. the dock bar's ← / →
        screen.SelectNextUnitCommand.Execute(null);
        Check("dock next");
        screen.SelectPreviousUnitCommand.Execute(null);
        Check("dock previous");

        // 6. browse, then pick — the reported bug. Since W4 the tone surface is
        // a MODE, so the pick LEAVES it: the editor is not on screen at all
        // while it is up, and "a block is selected and its editor is not
        // showing" is exactly the state this case exists to rule out.
        rig.ToggleBrowserCommand.Execute(null);
        Assert.True(screen.IsTonesMode, "the Tones mode did not come up");
        screen.SelectUnitCommand.Execute(rig.Nodes[1]);
        Check("pick a block while browsing");
        Assert.False(rig.IsBrowserOpen, "picking a block must LEAVE the Tones mode");

        // 7. clearing the selection is the one state with no editor, and it is
        // the state the dock bar prints "nothing selected" for
        rig.SelectNodeAt(-1);
        Check("selection cleared");

        Assert.True(log.Count >= 8, $"only {log.Count} entry points exercised");
    }

    /// <summary>
    ///     ONE SOURCE EACH SIDE, so a second writer cannot appear.
    ///     <list type="bullet">
    ///         <item>the editor header prints <c>DockUnit.…</c>, and
    ///             <c>DockUnit</c> is DERIVED from
    ///             <c>PedalboardViewModel.SelectedNode</c> — there is no settable
    ///             docked-unit property for anything else to write;</item>
    ///         <item>the canvas draws its highlight from
    ///             <c>PedalNodeViewModel.IsSelected</c>, and the only writer of
    ///             that is <c>OnSelectedNodeChanged</c>.</item>
    ///     </list>
    ///     <para>
    ///         Proven non-vacuous by adding a second
    ///         <c>node.IsSelected = true</c> in <c>PedalboardViewModel</c>'s
    ///         chain edits: the writer count went to 3 and this failed.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheHeaderAndTheCanvas_ReadOneSourceEach()
    {
        // ── the header side: the dock bar's name comes from DockUnit
        var header = View().Descendants().Single(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RigDockBar"));
        var names = header.Descendants()
            .Select(e => e.Attribute("Text")?.Value)
            .Where(v => v is not null && v.Contains("Binding", StringComparison.Ordinal))
            .Select(v => v!)
            .ToList();
        Assert.Contains(names, v => v.Contains("DockUnit.", StringComparison.Ordinal));
        Assert.DoesNotContain(names, v =>
            v.Contains("SelectedNode", StringComparison.Ordinal)
            || v.Contains("Rig.Nodes", StringComparison.Ordinal));

        // …and DockUnit is derived, never assigned from outside its own getter.
        var vm = Source("ViewModels/PlayMode/AmpFxRigViewModel.cs");
        Assert.DoesNotContain("public RigDockUnit? DockUnit { get; set; }", vm, StringComparison.Ordinal);
        Assert.Contains("_dockUnit ??= _rig.SelectedNode is { } node", vm, StringComparison.Ordinal);
        // The `??=` in the getter is asserted above and deliberately does not
        // match this pattern; what is counted is the PLAIN assignments, and both
        // of them are `= null` resets that force the getter to re-derive.
        var dockWrites = Regex.Matches(Strip(vm), @"_dockUnit\s*=\s*(?!=)").ToList();
        Assert.True(dockWrites.Count == 2,
            $"{dockWrites.Count} plain writers of _dockUnit — expected exactly 2, both `= null`. "
            + "Anything else is a second source for the editor header, independent of the canvas selection.");
        Assert.All(dockWrites, m => Assert.Contains("null",
            Strip(vm).Substring(m.Index, Math.Min(24, Strip(vm).Length - m.Index)), StringComparison.Ordinal));

        // ── the canvas side: ONE writer of the node's own highlight
        var rig = Source("ViewModels/PlayMode/PedalboardViewModel.cs");
        var selWrites = Regex.Matches(Strip(rig), @"\.IsSelected\s*=\s*(?!=)").Count;
        Assert.True(selWrites == 2,
            $"{selWrites} writers of PedalNodeViewModel.IsSelected in PedalboardViewModel — expected exactly "
            + "2 (OnSelectedNodeChanged clearing the old node and setting the new one). Another writer means "
            + "the canvas can highlight a block the editor is not showing.");
        Assert.Contains("partial void OnSelectedNodeChanged(", rig, StringComparison.Ordinal);

        // …and the selection REQUEST signal that covers a re-click is raised by
        // both public entry points, not just one.
        Assert.Equal(2, Regex.Matches(rig, @"NodeSelectionRequested\?\.Invoke\(").Count);
    }

    /// <summary>Comments stripped, so a `see cref` or an example in a doc block
    /// cannot count as a writer — the mistake the reachability lints exist to
    /// avoid.</summary>
    private static string Strip(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static XDocument View() =>
        XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "AmpFxRigView.axaml"));

    private static string Source(string relative)
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(File.Exists(path), $"{relative} moved — this guard is blind");
        return UiSourceTree.ReadAllText(path);
    }
}
