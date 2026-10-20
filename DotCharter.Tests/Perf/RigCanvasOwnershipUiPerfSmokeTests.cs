using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     W1 (2026-09-16) — LAYOUT OWNERSHIP IN THE AMP &amp; FX RIG.
///     <para>
///         The routing canvas (<c>RigBoard</c>) used to be the <c>*</c> row above
///         an <c>Auto</c> row that held EITHER the module dock OR the tone
///         drawer, so selecting a block pushed a 244+ px editor body into that
///         row and the canvas shrank: the picture of the signal path resized
///         every time the editor opened or closed. Everything below the canvas
///         is one host (<c>RigWorkspace</c>) at one persisted height now, and
///         these cases are the pin — measured at the three window sizes the
///         capture harnesses use.
///     </para>
///     <para>
///         <c>*UiPerfSmokeTests</c> + <c>[Collection("HeadlessUI")]</c>: needs the
///         real Avalonia session, so it runs in the headless suite's own
///         <c>dotnet test</c>, not the default gate.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigCanvasOwnershipUiPerfSmokeTests(ITestOutputHelper output)
{
    /// <summary>The rig screen over a real chain, attached and laid out.
    /// 1600×900 is the smallest window the capture harnesses use.</summary>
    private static void WithRig(Action<AmpFxRigView, AmpFxRigViewModel, PedalboardViewModel, Window> body,
        IEnumerable<string>? gear = null, double width = 1920, double height = 1080)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var chain = new FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
        foreach (var id in gear ?? ["overdrive", "amp", "cab"])
            rig.AddGear(catalogue.Single(m => m.Id == id));

        var model = new AmpFxRigViewModel(rig);
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

    private static Rect Settle(Window window, Control c)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
        return c.Bounds;
    }

    // ══ 1. the canvas does not move ══════════════════════════════════════════

    /// <summary>
    ///     THE ACCEPTANCE CRITERION. The canvas is the same height with the
    ///     module editor closed and open, at every supported window size —
    ///     and the gear rail beside it does not move either, because opening a
    ///     panel may not resize an unrelated one.
    ///     <para>
    ///         Proven non-vacuous by putting the workspace row back on
    ///         <c>GridLength.Auto</c> in <c>AmpFxRigView.SyncWorkspaceRow</c>:
    ///         the canvas then measured 852 → 372 px at 1600×900 (open vs
    ///         closed) and this failed at the first size.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task TheRoutingCanvas_IsTheSameHeight_WhetherTheEditorIsOpenOrNot(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, rig, window) =>
        {
            var canvas = view.FindControl<Border>("RigBoard")!;
            var gear = view.FindControl<Border>("RigGearPanel")!;

            // CLOSED: nothing selected, so the dock is its bar.
            rig.SelectNodeAt(-1);
            model.IsDockOpen = false;
            var closedCanvas = Settle(window, canvas);
            var closedGear = gear.Bounds;
            Assert.False(model.IsDockBodyVisible);

            // OPEN: a block picked, the editor body up.
            model.IsDockOpen = true;
            rig.SelectNodeAt(1);
            var openCanvas = Settle(window, canvas);
            var openGear = gear.Bounds;
            Assert.True(model.IsDockBodyVisible, "selecting a unit must open the editor");

            output.WriteLine($"{width}x{height}: canvas closed={closedCanvas.Height:F1} open={openCanvas.Height:F1}" +
                             $" · gear closed={closedGear.Size} open={openGear.Size}");

            Assert.True(closedCanvas.Height > 1, "the canvas did not lay out");
            Assert.True(Math.Abs(closedCanvas.Height - openCanvas.Height) < 0.5,
                $"the routing canvas is {closedCanvas.Height:F1} px with the editor closed and " +
                $"{openCanvas.Height:F1} px with it open — the workspace row is sizing to its content again");
            Assert.True(Math.Abs(closedCanvas.Width - openCanvas.Width) < 0.5,
                $"the canvas WIDTH moved {closedCanvas.Width:F1} -> {openCanvas.Width:F1}");

            Assert.True(closedGear.Width > 1, "the gear panel did not lay out");
            Assert.True(Math.Abs(closedGear.Width - openGear.Width) < 0.5,
                $"the gear panel is {closedGear.Width:F1} px wide with the editor closed and " +
                $"{openGear.Width:F1} px with it open");
            Assert.True(Math.Abs(closedGear.Height - openGear.Height) < 0.5,
                $"the gear panel is {closedGear.Height:F1} px tall with the editor closed and " +
                $"{openGear.Height:F1} px with it open");
        }, width: width, height: height), CancellationToken.None);
    }

    /// <summary>
    ///     …and the TONES MODE is the same question one door over. The tone
    ///     designer and the Tones list used to be a DRAWER sharing the
    ///     workspace with the editor, so the canvas had to not move while it
    ///     was open; since W4 they are a mode that REPLACES the canvas body, so
    ///     what has to hold is that the canvas comes back exactly as it was.
    ///     <para>
    ///         That is a stronger claim than the drawer's, not a weaker one: the
    ///         mode is a sibling of <c>RigFlatBody</c> rather than a re-layout
    ///         of it, so nothing inside the canvas body is touched at all. A
    ///         mode that collapsed or re-rowed it would fail here.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task TheRoutingCanvas_ComesBackUnchanged_FromTheTonesMode(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, rig, window) =>
        {
            var canvas = view.FindControl<Border>("RigBoard")!;

            rig.SelectNodeAt(1);
            var editing = Settle(window, canvas);
            var selected = rig.SelectedNode;

            rig.IsBrowserOpen = true;
            for (var pass = 0; pass < 5; pass++) window.UpdateLayout();
            Assert.True(model.IsTonesMode, "the pill did not switch into the Tones mode");
            Assert.False(canvas.IsEffectivelyVisible, "the canvas is still drawn inside the Tones mode");

            rig.IsBrowserOpen = false;
            var back = Settle(window, canvas);
            Assert.Same(selected, rig.SelectedNode);
            Assert.True(model.IsDockBodyVisible, "leaving the mode must bring the editor back as it was");

            output.WriteLine($"{width}x{height}: canvas editing={editing.Height:F1} back={back.Height:F1}");
            Assert.True(Math.Abs(editing.Height - back.Height) < 0.5,
                $"the canvas was {editing.Height:F1} px before the Tones mode and {back.Height:F1} px after");
            Assert.True(Math.Abs(editing.Width - back.Width) < 0.5,
                $"the canvas WIDTH moved {editing.Width:F1} -> {back.Width:F1}");
        }, width: width, height: height), CancellationToken.None);
    }

    /// <summary>
    ///     THE CAPTURE DUMP for the three-size check, same idiom as
    ///     <see cref="RigDesignCompareUiPerfSmokeTests" />: inert unless BOTH
    ///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (which is what flips the session off
    ///     <c>UseHeadlessDrawing</c>, so <c>CaptureRenderedFrame</c> returns a
    ///     bitmap rather than null) and <c>DOTCHARTER_RIG_CAPTURE_DIR=&lt;dir&gt;</c>
    ///     are set. Writes <c>rig-&lt;w&gt;x&lt;h&gt;-&lt;closed|editor|drawer&gt;.png</c>.
    ///     <para>
    ///         It still LAYS OUT all three states with the variables unset, and
    ///         asserts each one arranged, so the case is not a no-op in CI — but
    ///         the canvas equality itself is asserted by the two cases above, not
    ///         here. Reading the numbers off a PNG is the human half.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task TheThreeStates_LayOut_AndAreDumpedWhenAsked(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, rig, window) =>
        {
            var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
            var dumping = UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir);
            if (dumping) Directory.CreateDirectory(dir!);

            void Shoot(string state)
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                // The Tones MODE replaces the canvas body (W4), so read
                // whichever of the two is up rather than always the canvas one.
                var host = state == "drawer"
                    ? (Control)view.FindControl<RigTonesView>("RigTonesMode")!
                    : view.FindControl<Grid>("RigFlatBody")!;
                Assert.True(host.Bounds.Height > 1, $"the '{state}' state did not lay out");

                if (!dumping) return;
                using var frame = window.CaptureRenderedFrame();
                Assert.True(frame is not null,
                    "CaptureRenderedFrame returned null — DOTCHARTER_UI_SWEEP_DUMP=1 has to be set "
                    + "BEFORE the session starts, or UseHeadlessDrawing is still true");
                frame!.Save(Path.Combine(dir!, $"rig-{width:F0}x{height:F0}-{state}.png"),
                    new PngBitmapEncoderOptions());
            }

            // CLOSED: a block picked but the editor collapsed with HIDE, which is
            // the state the canvas is compared against.
            rig.SelectNodeAt(1);
            model.IsDockOpen = false;
            Shoot("closed");

            // EDITOR: the same block, its editor up.
            model.IsDockOpen = true;
            rig.SelectNodeAt(1);
            Assert.True(model.IsDockBodyVisible);
            Shoot("editor");

            // DRAWER: the TONES MODE, which replaces the canvas body. The
            // capture keeps the file name so the set stays comparable across
            // W1 -> W4.
            rig.IsBrowserOpen = true;
            Assert.True(model.IsTonesMode);
            Shoot("drawer");
        }, width: width, height: height), CancellationToken.None);
    }

    // ══ 2. six blocks fit ════════════════════════════════════════════════════

    /// <summary>
    ///     A SIX-BLOCK CHAIN MUST NOT PRODUCE A HORIZONTAL SCROLLBAR. The board
    ///     is fitted to the canvas by <c>AmpFxRigView.BoardFitScale</c> — the
    ///     same transform the Fit button drives, applied continuously — so the
    ///     scroller's extent stays inside its viewport.
    ///     <para>
    ///         Proven non-vacuous by returning 1.0 from <c>BoardFitScale</c>:
    ///         the extent then ran 1,742 px into a 1,262 px viewport at
    ///         1600×900 and this failed.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task ASixBlockChain_FitsTheCanvas_WithNoHorizontalScrollbar(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, _, rig, window) =>
        {
            var scroll = view.FindControl<ScrollViewer>("RigBoardScroll")!;
            for (var pass = 0; pass < 5; pass++) window.UpdateLayout();

            Assert.Equal(6, rig.Nodes.Count);
            output.WriteLine($"{width}x{height}: extent {scroll.Extent.Width:F0} in viewport " +
                             $"{scroll.Viewport.Width:F0} (zoom {rig.Zoom:F2})");

            Assert.True(scroll.Viewport.Width > 1, "the board did not lay out");
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                $"six blocks want {scroll.Extent.Width:F0} px of a {scroll.Viewport.Width:F0} px canvas — " +
                "the chain is not being fitted and the rig grows a horizontal scrollbar");

            // …and the bar itself is not up. `Visible` would reserve the track
            // whether or not it was needed, which is what W1 removed.
            Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
            var bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                .FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
            Assert.True(bar is null or { IsVisible: false },
                "the board's horizontal scrollbar is up over a chain that fits");
        }, gear: ["overdrive", "compressor", "amp", "cab", "delay", "reverb"],
            width: width, height: height), CancellationToken.None);
    }

    /// <summary>
    ///     …and the FIT is what does it, which the six-block case alone does not
    ///     prove: a six-block chain fits a 1600 px canvas at scale 1 anyway.
    ///     Twelve blocks do not — their natural width runs past the canvas — so
    ///     this is the case that fails if <c>BoardFitScale</c> stops engaging.
    ///     <para>
    ///         Proven non-vacuous by returning 1.0 from <c>BoardFitScale</c>: the
    ///         extent then ran past the viewport and this failed at 1600×900.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    public async Task ALongChain_IsFittedToTheCanvas_NotScrolledSideways(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, _, rig, window) =>
        {
            var scroll = view.FindControl<ScrollViewer>("RigBoardScroll")!;
            var path = view.FindControl<Grid>("RigBoardPath")!;
            for (var pass = 0; pass < 5; pass++) window.UpdateLayout();

            output.WriteLine($"{width}x{height}: {rig.Nodes.Count} blocks, path {path.Bounds.Width:F0} logical, " +
                             $"extent {scroll.Extent.Width:F0} in viewport {scroll.Viewport.Width:F0}");

            Assert.True(path.Bounds.Width > scroll.Viewport.Width + 1,
                $"twelve blocks measured {path.Bounds.Width:F0} px against a {scroll.Viewport.Width:F0} px " +
                "canvas — this case no longer exercises a chain that needs fitting");
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                $"the chain wants {scroll.Extent.Width:F0} px of a {scroll.Viewport.Width:F0} px canvas — " +
                "it is being scrolled sideways instead of fitted");
        }, gear: ["overdrive", "compressor", "gate", "eq", "graphiceq", "amp",
                  "cab", "delay", "reverb", "chorus", "flanger", "phaser"],
            width: width, height: height), CancellationToken.None);
    }

    // ══ 3. the drawer's two halves — SUPERSEDED BY W4 ═══════════════════════
    //
    // `TheDesignerAndTheTonesList_EachOwnTheirContainer_AndCollapseToAStub`
    // lived here. It asserted that the designer and the Tones list each had
    // their own scroller inside the drawer and that both fell back to a header
    // STUB when the module editor took the workspace. Both halves of that are
    // gone by design: the two are a MODE with ONE scroller between them and
    // nothing to stub, because they no longer share a host with the editor.
    // What replaced it is `Perf/RigTonesModeUiPerfSmokeTests` —
    // `TheTonesMode_HasExactlyOneScrollRegion` for the container half and
    // `EnteringAndLeavingTheMode_RestoresTheCanvasAndTheEditor` for the
    // mutual-exclusion half.

    /// <summary>The region of <paramref name="v" /> that actually reaches the
    /// screen: its own box intersected with every clipping ancestor's, in
    /// <paramref name="root" /> coordinates.</summary>
    internal static Rect PaintedRect(Visual v, Visual root)
    {
        Rect InRoot(Visual x, Rect local) => new(x.TranslatePoint(local.TopLeft, root)!.Value, local.Size);

        var rect = InRoot(v, new Rect(v.Bounds.Size));
        foreach (var a in v.GetVisualAncestors())
        {
            if (a.ClipToBounds) rect = rect.Intersect(InRoot(a, new Rect(a.Bounds.Size)));
            if (a.Clip is { } g) rect = rect.Intersect(InRoot(a, g.Bounds));
            if (ReferenceEquals(a, root)) break;
        }

        return rect;
    }
}
