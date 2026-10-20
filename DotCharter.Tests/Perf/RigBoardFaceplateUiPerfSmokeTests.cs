using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The AmpliTube / BIAS FX layout: the chain shows each unit as its own gear,
///     small (a split region shares the height between its lanes), and the
///     selected one opens BIG in the dock with live knobs and a live footswitch. With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///     <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, each case writes a PNG for review.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigBoardFaceplateUiPerfSmokeTests
{
    /// <summary>The smallest on-screen knob a player can still grab and turn: its
    /// hit box (the cap plus the value arc around it), in screen pixels.</summary>
    private const double GrabbableKnob = 24;

    /// <summary>
    ///     The AmpliTube / BIAS FX layout: the chain shows every unit as its own
    ///     gear, small, each knob drawn at its live value; clicking one opens the
    ///     SAME gear big in the dock, where its knobs turn and a stompbox's printed
    ///     footswitch bypasses it — and the small picture on the chain follows.
    /// </summary>
    [Fact]
    public Task TheChainShowsSmallGear_AndTheSelectedGearOpensBigBelow() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            foreach (var id in new[] { "compressor", "rat", "namamp", "cab", "delay" })
                rig.AddGear(catalogue.Single(m => m.Id == id));
            rig.SelectNodeAt(-1);
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 6; pass++) window.UpdateLayout();

                Capture(window, "board-series.png");
                var cards = Cards(view);
                Assert.Equal(rig.Nodes.Count, cards.Count);
                // an overview: recognisable gear, never past the thumbnail ceiling
                Assert.InRange(model.BoardFaceHeight, 100, RigBoardFaceSize.Max);
                var scale = BoardScale(view);
                Assert.True(scale > 0.95, $"the chain was shrunk to {scale:0.00} instead of taking shorter faces");
                foreach (var card in cards)
                {
                    var node = (PedalNodeViewModel)card.DataContext!;
                    var face = Assert.Single(card.GetVisualDescendants().OfType<RigFaceplateView>());
                    Assert.True(face.HasFaceplate, $"{node.ModuleTypeId}: no faceplate on the chain");
                    Assert.False(face.IsInteractive || face.IsHitTestVisible,
                        $"{node.ModuleTypeId}: the chain's picture takes the click the card needs");
                    Assert.Equal(model.BoardFaceHeight, face.Bounds.Height, 1);
                    Assert.Empty(face.Children.OfType<FireKnob>());
                }

                // Clicking a unit on the chain opens it big below.
                var ratCard = cards.Single(c => ((PedalNodeViewModel)c.DataContext!).ModuleTypeId == "rat");
                var rat = (PedalNodeViewModel)ratCard.DataContext!;
                var click = ratCard.TranslatePoint(new Point(ratCard.Bounds.Width / 2, ratCard.Bounds.Height / 3), window)!.Value;
                window.MouseDown(click, MouseButton.Left);
                window.MouseUp(click, MouseButton.Left);
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                Assert.True(rat.IsSelected, "clicking the unit did not select it");
                Assert.Same(rat, model.DockUnit?.Node);

                var editor = Assert.Single(view.GetVisualDescendants().OfType<RigModuleEditorView>());
                var big = editor.GetVisualDescendants().OfType<RigFaceplateView>().Single(f => f.IsEffectivelyVisible);
                Assert.True(big.IsInteractive && ReferenceEquals(big.Node, rat));
                var fit = Assert.IsType<RigScaleToFit>(big.GetVisualAncestors().OfType<RigScaleToFit>().First());
                var bigHeight = big.Bounds.Height * fit.Scale;
                Assert.True(bigHeight > model.BoardFaceHeight * 2.5,
                    $"the selected gear is {bigHeight:0} px tall below a {model.BoardFaceHeight:0} px chain — not BIG");

                // Its knobs are the params, and big enough to grab.
                var knobs = big.Children.OfType<FireKnob>().ToList();
                Assert.NotEmpty(knobs);
                Assert.All(knobs, k => Assert.True(k.Bounds.Width * fit.Scale >= GrabbableKnob,
                    $"a {k.Bounds.Width * fit.Scale:0} px knob on the big face"));
                var knob = knobs.First(k => rat.Params.Single(p => p.Id == ((RigFaceControl)k.Tag!).Param) is { Step: 0, HasValueLabels: false });
                var param = rat.Params.Single(p => p.Id == ((RigFaceControl)knob.Tag!).Param);
                var target = param.Minimum + (param.Maximum - param.Minimum) * 0.31f;
                knob.Value = target;
                Assert.Equal(target, param.Value, 2);

                // A stompbox's printed footswitch is its bypass: stomp it.
                var foot = big.Children.OfType<Avalonia.Controls.Primitives.ToggleButton>()
                    .Single(t => t.Classes.Contains("FireFaceFootswitch"));
                Assert.True(foot.Bounds.Width * fit.Scale >= 32, $"the footswitch is a {foot.Bounds.Width * fit.Scale:0} px target");
                var stomp = foot.TranslatePoint(new Point(foot.Bounds.Width / 2, foot.Bounds.Height / 2), window)!.Value;
                Assert.False(rat.Bypassed);
                var knobCentre = knob.TranslatePoint(new Point(knob.Bounds.Width / 2, knob.Bounds.Height / 2), window)!.Value;
                var engagedKnob = UiSweepRunner.CaptureEnabled ? Luminance(window, knobCentre) : 0;
                window.MouseDown(stomp, MouseButton.Left);
                window.MouseUp(stomp, MouseButton.Left);
                Assert.True(rat.Bypassed, "stomping the face's footswitch did not bypass the unit");
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                // Bypassed, the knob DIMS; it never turns see-through. A translucent
                // black cap over the chrome nut reads as a knob that has vanished
                // (owner, 2026-09-27): the nut shining through made its centre
                // BRIGHTER than the engaged knob's.
                Capture(window, "board-series-bypassed.png");
                Assert.Contains(knobs, k => k.CapFile is { Length: > 0 });
                Assert.All(knobs.Where(k => k.CapFile is { Length: > 0 }), k => Assert.True(k.Opacity >= 0.999,
                    $"a bypassed cap knob faded to {k.Opacity:0.00}: the chrome nut shows through it"));
                // the pixels need real Skia drawing (DOTCHARTER_UI_SWEEP_DUMP=1)
                var bypassedKnob = UiSweepRunner.CaptureEnabled ? Luminance(window, knobCentre) : 0;
                Assert.True(bypassedKnob <= engagedKnob + 6,
                    $"the bypassed knob's centre is brighter ({bypassedKnob:0}) than the engaged one ({engagedKnob:0}) at {knobCentre} - the nut shows through");
                window.MouseDown(stomp, MouseButton.Left);
                window.MouseUp(stomp, MouseButton.Left);
                Assert.False(rat.Bypassed);

                Capture(window, "board-series-selected.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task ASplitBoard_SharesTheHeightBetweenItsLanes() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "overdrive"));
            rig.AddGear(catalogue.Single(m => m.Id == "splitter"));
            var delay = rig.AddGear(catalogue.Single(m => m.Id == "delay"))!;
            var chorus = rig.AddGear(catalogue.Single(m => m.Id == "chorus"))!;
            var merger = rig.Nodes.First(n => n.IsMerger);
            Assert.True(rig.DropToRow(delay, rig.Nodes.IndexOf(merger), 1));
            Assert.True(rig.DropToRow(chorus, rig.Nodes.IndexOf(merger), 2));
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 6; pass++) window.UpdateLayout();

                var board = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RigBoard");
                Assert.Equal(RigBoardFaceSize.For(board.Bounds.Height, 2), model.BoardFaceHeight);
                var laneCards = view.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("FireLaneRow"))
                    .SelectMany(l => l.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("FireUnitCard")))
                    .ToList();
                Assert.Equal(2, laneCards.Count);
                foreach (var card in laneCards)
                {
                    var face = Assert.Single(card.GetVisualDescendants().OfType<RigFaceplateView>());
                    Assert.True(face.HasFaceplate && !face.IsInteractive);
                    Assert.Equal(model.BoardFaceHeight, face.Bounds.Height, 1);
                }

                Capture(window, "board-split.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     The dock under the chain opens the selected NAM amp as its big faceplate
    ///     (never the old amp artwork, which disagreed with the chain), beside it the
    ///     capture library (seeded with forty long titles — an empty library hides
    ///     every layout fault) and its exact values folded; the controller/MIDI rack
    ///     starts folded too.
    /// </summary>
    [Fact]
    public Task TheDock_OpensTheSelectedGearBig_WithItsCaptureLibrary() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "overdrive"));
            var amp = rig.AddGear(catalogue.Single(m => m.Id == "namamp"))!;
            rig.AddGear(catalogue.Single(m => m.Id == "cab"));
            var library = new SeededLibrary(40);
            var model = new AmpFxRigViewModel(rig, library);
            rig.SelectNodeAt(rig.Nodes.IndexOf(amp));
            // the capture library and the exact values are the unit's DETAILS: the
            // gear bar's toggle opens them beside the stage
            Assert.False(model.IsDetailsOpen);
            model.IsDetailsOpen = true;
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 6; pass++) window.UpdateLayout();
                Capture(window, "dock-nam-inspector.png");

                var unit = model.DockUnit!;
                Assert.Same(amp, unit.Node);
                Assert.Equal(RigEditorFamily.Pedal, unit.Family);
                var editor = Assert.Single(view.GetVisualDescendants().OfType<RigModuleEditorView>());
                Assert.IsType<DotCharter.Tone.UI.Views.PlayMode.RigEditors.RigPedalEditorView>(editor.ActiveFamilyView);
                // the selected gear, big: the same face the chain shows small
                var big = editor.GetVisualDescendants().OfType<RigFaceplateView>().Single(f => f.IsEffectivelyVisible);
                Assert.True(big.IsInteractive && big.HasFaceplate && ReferenceEquals(big.Node, amp));

                // every exact value, folded: the NAM face carries all its params
                var values = Named<Expander>(editor, "RigUnitValues");
                Assert.True(values.IsEffectivelyVisible);
                Assert.False(values.IsExpanded);
                values.IsExpanded = true;
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                var rows = values.GetVisualDescendants().OfType<Grid>().Count(g => g.Classes.Contains("FireValueRow"));
                Assert.Equal(unit.EnclosureControls.Count, rows);

                var captures = Named<ItemsControl>(editor, "RigCaptureList");
                Assert.True(captures.IsEffectivelyVisible && captures.Bounds.Height > 100, $"capture list {captures.Bounds}");
                Assert.Equal(40, captures.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("FireListRow")));
                // the list stays inside its column: a long title trims
                var aside = Named<StackPanel>(editor, "RigPedalAside");
                Assert.All(captures.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("FireListRow")),
                    b => Assert.True(b.Bounds.Width <= aside.Bounds.Width + 0.5, $"a capture row {b.Bounds.Width} wide in a {aside.Bounds.Width} column"));

                var controllers = Named<Expander>(editor, "RigEditorControllers");
                Assert.False(controllers.IsExpanded);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    internal sealed class SeededLibrary(int count) : DotCharter.Tone.Online.Tone3000.INamCaptureLibrary
    {
        private readonly List<DotCharter.Tone.Online.Tone3000.NamCaptureEntry> _entries =
            Enumerable.Range(1, count).Select(i => new DotCharter.Tone.Online.Tone3000.NamCaptureEntry(
                new DotCharter.Tone.Online.Tone3000.NamCaptureManifest(
                    ToneId: i, ModelId: i, Title: $"Capture {i:00} · Marshall JCM800 2203 Lead Channel Gain {i}",
                    Author: "capturist", GearType: "amp", Format: "nam", SizeTier: "standard", License: "CC-BY",
                    LicenseUrl: "", AttributionUrl: "", FileName: $"{i}.nam", Sha256: "00", FileBytes: 5,
                    DownloadedUtc: DateTimeOffset.UnixEpoch),
                $@"X:\nam\{i}.nam")).ToList();

        public string RootDirectory => @"X:\nam";
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<DotCharter.Tone.Online.Tone3000.NamCaptureEntry> GetAll() => _entries;
        public bool IsModelDownloaded(long modelId) => true;
        public bool IsToneDownloaded(long toneId) => true;

        public Task<DotCharter.Tone.Online.OnlineResult<DotCharter.Tone.Online.Tone3000.NamCaptureEntry>> DownloadAsync(
            DotCharter.Tone.Online.Tone3000.Tone3000Tone tone, DotCharter.Tone.Online.Tone3000.Tone3000Model model,
            IProgress<double>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public bool Delete(long modelId) => false;
        public int MigrateFileExtensions() => 0;
        public bool ImportBundled(string sourceFilePath, DotCharter.Tone.Online.Tone3000.BundledNamCapture meta) => false;
    }

    private static IReadOnlyList<DspModuleType> Catalogue()
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        return metadata.Catalog;
    }

    private static List<Border> Cards(Control view) =>
        view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("FireUnitCard")).ToList();

    /// <summary>The board's fit/zoom scale: what a logical DIP on the board measures on screen.</summary>
    private static double BoardScale(Control view)
    {
        var host = view.GetVisualDescendants().OfType<LayoutTransformControl>().Single(h => h.Name == "RigBoardZoomHost");
        return host.LayoutTransform is ScaleTransform s ? s.ScaleX : 1.0;
    }

    /// <summary>The rendered frame's luminance (0..255) averaged over a 3x3 patch.</summary>
    private static double Luminance(Window window, Point at)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var x = (int)Math.Round(at.X) - 1;
        var y = (int)Math.Round(at.Y) - 1;
        var pixels = new byte[3 * 3 * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            frame!.CopyPixels(new PixelRect(x, y, 3, 3), handle.AddrOfPinnedObject(), pixels.Length, 3 * 4);
        }
        finally
        {
            handle.Free();
        }

        double sum = 0;
        for (var i = 0; i < 9; i++) sum += 0.114 * pixels[i * 4] + 0.587 * pixels[i * 4 + 1] + 0.299 * pixels[i * 4 + 2]; // BGRA
        return sum / 9;
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
