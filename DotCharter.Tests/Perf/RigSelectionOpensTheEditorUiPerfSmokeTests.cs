using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     W3 (2026-09-16) — CLICKING A GEAR BLOCK ALWAYS OPENS ITS SETTINGS.
///     <para>
///         The owner's report was "it does nothing visible when the preset
///         browser is open", and that was the whole of it: the tone drawer held
///         the workspace, so the editor the click opened was a header stub under
///         it. Selecting a block is the later gesture and wins — the drawer falls
///         back to its stub, the editor for the clicked block comes up, and it
///         comes up at the TOP of its face rather than wherever the previous unit
///         of that family left the scroller.
///     </para>
///     <para>
///         Swept over every family <c>RigEditorTemplateSelector</c> knows,
///         because the editor host builds one view per family and keeps it
///         attached: a fault that only bites when a family view is REUSED is
///         invisible to a single-module case.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigSelectionOpensTheEditorUiPerfSmokeTests(ITestOutputHelper output)
{
    /// <summary>One module per family the template selector dispatches on, plus
    /// the junction that opens the routing console. Kept as ids rather than
    /// families so the case fails if the catalogue drops one.</summary>
    private static readonly string[] OneModulePerFamily =
    [
        "namamp",     // RigAmpFaceView
        "cab",        // RigCabinetEditorView
        "graphiceq",  // RigGraphicEqView
        "eq",         // RigParametricEqView
        "wah",        // RigExpressionView
        "miku",       // RigVocalView
        "compressor", // RigDynamicsView
        "overdrive",  // RigPedalEditorView (the fallback)
        "delay",      // …and a second pedal, so the fallback view is REUSED
    ];

    private static void WithRig(Action<AmpFxRigView, AmpFxRigViewModel, PedalboardViewModel, Window> body,
        double width = 1920, double height = 1080)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var chain = new FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
        foreach (var id in OneModulePerFamily)
            rig.AddGear(catalogue.Single(m => m.Id == id));

        // the side rack is the DETAILS column; the scroll case reads it, so it is open
        var model = new AmpFxRigViewModel(rig) { IsDetailsOpen = true };
        var view = new AmpFxRigView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
            body(view, model, rig, window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    ///     THE ACCEPTANCE CRITERION, from both paths the owner uses: the board
    ///     tile (through <c>SelectUnitCommand</c>, which is what the tile's
    ///     Button raises) and the dock bar's → . With the preset browser OPEN,
    ///     every block type must bring its own editor up, measured.
    ///     <para>
    ///         Proven non-vacuous by removing <c>_rig.IsBrowserOpen = false</c>
    ///         (W4's successor to <c>IsDrawerExpanded = false</c>) from
    ///         <c>AmpFxRigViewModel.RevealSelectedEditor</c>: every module then
    ///         failed on "the editor for … did not come up".
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("tile")]
    [InlineData("next")]
    public async Task WithTheBrowserOpen_SelectingABlock_OpensThatBlocksEditor(string path)
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, rig, window) =>
        {
            var dockBody = view.FindControl<Border>("RigDockBody")!;
            // In the Tones VIEW since 2026-09-27 — its own name scope and its own
            // template, so it is in the visual tree only once the mode has been
            // laid out: found after the first entry below, not before.
            Grid? drawerBody = null;
            var host = view.GetVisualDescendants().OfType<RigModuleEditorView>().First();

            foreach (var id in OneModulePerFamily)
            {
                // Re-enter the TONES MODE before EVERY pick: the bug is about a
                // click landing while the tone surface is up, so a run that
                // entered it once would only test the first module. It is a real
                // LEAVE + ENTER because that is the gesture — picking a block
                // LEAVES the mode (W4: the editor is not on screen at all while
                // it is up, so the last-gesture rule sits one structure higher
                // than the drawer's did).
                rig.IsBrowserOpen = false;
                rig.IsBrowserOpen = true;
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                Assert.True(model.IsTonesMode, "the Tones mode did not come up");
                drawerBody ??= view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "RigBrowserBody");

                var node = rig.Nodes.First(n => n.ModuleTypeId == id);
                if (path == "tile")
                {
                    model.SelectUnitCommand.Execute(node);
                }
                else
                {
                    // step → until it lands on this module, the dock bar's path
                    for (var step = 0; step <= rig.Nodes.Count; step++)
                    {
                        model.SelectNextUnitCommand.Execute(null);
                        if (ReferenceEquals(rig.SelectedNode, node)) break;
                    }
                }

                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                Assert.True(ReferenceEquals(rig.SelectedNode, node),
                    $"the '{path}' path did not select {id}");
                Assert.True(model.IsDockBodyVisible,
                    $"the editor for {id} did not come up — dockOpen={model.IsDockOpen} " +
                    $"hasUnit={model.HasDockUnit} tonesMode={model.IsTonesMode}");
                Assert.False(model.IsTonesMode,
                    $"picking {id} did not leave the Tones mode");
                Assert.True(!drawerBody!.IsEffectivelyVisible,
                    $"the Tones list is still up over {id}'s editor");
                Assert.True(dockBody is { Bounds.Width: > 1, Bounds.Height: > 1 },
                    $"{id}'s editor measured {dockBody.Bounds} — it is up but has no size");

                var family = host.ActiveFamilyView;
                Assert.True(family is { IsEffectivelyVisible: true, Bounds.Height: > 1 },
                    $"no family view is showing for {id}");
                Assert.Same(model.DockUnit, family!.DataContext);

                output.WriteLine($"{path,-5} {id,-11} -> {family.GetType().Name,-24} " +
                                 $"dock {dockBody.Bounds.Width:F0}x{dockBody.Bounds.Height:F0}");
            }
        }), CancellationToken.None);
    }

    /// <summary>
    ///     …and a NEW unit opens at the TOP of its editor. The host keeps every
    ///     view it has built ATTACHED, so without this the next unit inherits
    ///     wherever the last one was left — the header names one module while the
    ///     column shows another's assignments.
    ///     <para>
    ///         It is measured on the RACK (<c>RigEditorRackScroll</c>), not on
    ///         the face: since lane E every face is folded into the dock row by
    ///         <c>RigScaleToFit</c>, so a face's extent FITS its viewport
    ///         (measured: 391 px of 391) and its offset can never be anything but
    ///         zero. The rack is deliberately not scaled — shrinking a column of
    ///         combos and toggles past reading size is worse than a scrollbar —
    ///         so it is the one that really carries the offset, and its three
    ///         cards are this unit's macros, this unit's control assignments and
    ///         this unit's MIDI input.
    ///     </para>
    ///     <para>
    ///         Proven non-vacuous by deleting the rack half of
    ///         <c>ScrollNewUnitIntoView</c>: the second unit then opened at the
    ///         first one's offset and this failed on "did not come back to the
    ///         top".
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ANewUnit_OpensAtTheTopOfItsEditor_AndTheSameUnitKeepsItsPlace()
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, rig, window) =>
        {
            ScrollViewer Rack()
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                var s = view.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(v => v.Name == "RigEditorRackScroll" && v.IsEffectivelyVisible);
                Assert.True(s is not null, "the module editor's side rack is not showing");
                return s!;
            }

            var first = rig.Nodes.First(n => n.ModuleTypeId == "overdrive");
            var second = rig.Nodes.First(n => n.ModuleTypeId == "delay");

            model.SelectUnitCommand.Execute(first);
            // the controller / assignment / MIDI panels start folded (2026-09-27);
            // open them, which is what makes the rack taller than its row
            Rack();
            view.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "RigEditorControllers").IsExpanded = true;
            var rack = Rack();
            Assert.True(rack.Extent.Height > rack.Viewport.Height + 1,
                $"the rack wants {rack.Extent.Height:F0} of a {rack.Viewport.Height:F0} px viewport — " +
                "it cannot be scrolled, so this case is not exercising anything");

            var parked = new Vector(0, Math.Min(120, rack.Extent.Height - rack.Viewport.Height));
            rack.Offset = parked;
            for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
            Assert.Equal(parked.Y, rack.Offset.Y, 1);

            // …a DIFFERENT unit comes up at the top
            model.SelectUnitCommand.Execute(second);
            var afterSwitch = Rack();
            output.WriteLine($"parked at {parked.Y:F0}; after switching to delay -> {afterSwitch.Offset.Y:F0}");
            Assert.Equal(0, afterSwitch.Offset.Y, 1);

            // …and re-selecting the SAME unit does not move it
            afterSwitch.Offset = parked;
            for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
            model.SelectUnitCommand.Execute(second);
            var afterReselect = Rack();
            output.WriteLine($"re-selecting delay at {parked.Y:F0} -> {afterReselect.Offset.Y:F0}");
            Assert.Equal(parked.Y, afterReselect.Offset.Y, 1);
        }), CancellationToken.None);
    }

    /// <summary>
    ///     HIDE is momentary, not a mode (W3). Collapsing the dock and then
    ///     picking another block must bring the editor back — otherwise "a block
    ///     is selected and its editor is not showing" survives in a second shape,
    ///     one door over from the drawer.
    /// </summary>
    [Fact]
    public async Task AfterHide_ThePickAfterIt_BringsTheEditorBack()
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, rig, window) =>
        {
            var dockBody = view.FindControl<Border>("RigDockBody")!;

            model.SelectUnitCommand.Execute(rig.Nodes.First(n => n.ModuleTypeId == "cab"));
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Assert.True(model.IsDockBodyVisible);

            model.ToggleDockCommand.Execute(null); // HIDE
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Assert.False(model.IsDockBodyVisible, "HIDE must still collapse the unit you are looking at");

            model.SelectUnitCommand.Execute(rig.Nodes.First(n => n.ModuleTypeId == "wah"));
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
            Assert.True(model.IsDockOpen, "the pick after a HIDE must re-open the dock");
            Assert.True(model.IsDockBodyVisible);
            Assert.True(dockBody.Bounds.Height > 1, $"the editor measured {dockBody.Bounds}");
            output.WriteLine($"after HIDE + pick: dock {dockBody.Bounds.Width:F0}x{dockBody.Bounds.Height:F0}");
        }), CancellationToken.None);
    }
}
