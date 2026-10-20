using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The LAYOUT harness for the rig's module editors (owner, 2026-09-08:
///     "speed up the layout design thing by designing it in a separate
///     smaller test suite that is only for the layout without anything wired
///     up"). One family editor per case, over the fake chain — no engine, no
///     board, no gear panel, no header — hosted the way <c>RigDockBody</c>
///     hosts it, at the width the owner's dock actually has (a ~2000 px
///     window with the gear panel closed leaves ~1700 px for the editor),
///     and, with the capture env vars set, written to
///     <c>editor-&lt;module&gt;.png</c>:
///     <code>
///     DOTCHARTER_UI_SWEEP_DUMP=1 DOTCHARTER_RIG_CAPTURE_DIR=&lt;dir&gt; dotnet test
///       --project DotCharter.Tests/DotCharter.Tests.csproj --no-build
///       --filter-class "*RigEditorCaptureUiPerfSmokeTests"
///     </code>
///     Twelve seconds a round, against a full build plus a launch. Without
///     the env vars it is a plain construction + attach + layout smoke test
///     and stays in the headless suite. Own process (headless suite).
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigEditorCaptureUiPerfSmokeTests
{
    /// <summary>The dock as the owner sees it: the editor row of a 2000 px
    /// window with the gear panel closed. Change these to look at another box.</summary>
    private const int DockWidth = 1700;
    private const int DockHeight = 720;

    [Theory]
    [InlineData("namamp")]
    [InlineData("amp")]
    [InlineData("cab")]
    [InlineData("chorus")]
    [InlineData("overdrive")]
    [InlineData("delay")]
    [InlineData("graphiceq")]
    [InlineData("eq")]
    [InlineData("wah")]
    [InlineData("compressor")]
    [InlineData("miku")]
    [InlineData("splitter")]
    [InlineData("gain")] // the one-knob enclosure (Clean Boost)
    public async Task TheEditorAlone_LaysOutAtTheOwnersDockWidth(string moduleId)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var type = catalogue.SingleOrDefault(m => m.Id == moduleId);
            Assert.SkipWhen(type is null, $"the native catalogue has no '{moduleId}'");

            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(type!);
            if (moduleId == "splitter") rig.AddGear(catalogue.Single(m => m.Id == "merger"));
            rig.SelectNodeAt(0);
            var screen = new AmpFxRigViewModel(rig)
            {
                // the side rack is the unit's DETAILS column, opened by the gear bar's toggle
                IsDetailsOpen = true
            };
            var unit = screen.DockUnit;
            Assert.NotNull(unit);

            // RigDockBody's frame, and nothing else of the screen.
            // The dock's ground: the Fire screen fill with the sunken wash over it,
            // flattened to one colour here so a capture reads like the app.
            var host = new Border
            {
                Padding = new Avalonia.Thickness(22),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E0B15")),
                Child = new RigModuleEditorView { DataContext = unit }
            };
            var window = new Window { Width = DockWidth, Height = DockHeight, Content = host };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                var editor = Assert.Single(host.GetVisualDescendants().OfType<RigModuleEditorView>());
                Assert.True(editor.Bounds.Width > 1000 && editor.Bounds.Height > 300,
                    $"the editor host measured {editor.Bounds} in a {DockWidth}×{DockHeight} dock");
                var rack = editor.FindControl<Border>("RigEditorRack")!;
                Assert.True(rack.Bounds.Width >= 300, $"the side rack measured {rack.Bounds}");
                var family = editor.FindControl<Panel>("RigEditorFamilyHost")!;
                Assert.True(family.Bounds.Width > 800, $"the family host measured {family.Bounds}");

                // A fader's cap stripe is the FAMILY accent the view put on the
                // Slider's Foreground (green on the EQ, teal on the wah) — not the
                // style's default violet. Read the painted brush, not the markup.
                foreach (var fader in editor.GetVisualDescendants().OfType<Slider>().Where(s => s.Classes.Contains("FireEqFader")))
                {
                    var cap = Assert.Single(fader.GetVisualDescendants().OfType<Border>(), b => b.Name == "FaderCap");
                    var stripe = Assert.IsType<Border>(cap.Child);
                    var want = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(fader.Foreground).Color;
                    var got = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(stripe.Background).Color;
                    Assert.True(want == got, $"{moduleId}: the fader stripe is {got}, the slider's accent is {want}");
                    Assert.True(want != Avalonia.Media.Color.Parse("#A78BFA"), $"{moduleId}: the fader still wears the style's default accent");
                }

                var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                if (UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(dir, $"editor-{moduleId}.png"), new PngBitmapEncoderOptions());
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }
}
