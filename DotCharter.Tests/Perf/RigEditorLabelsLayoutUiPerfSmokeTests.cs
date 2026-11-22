using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Spectrum;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode.RigEditors;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Label layout in the rig module editors (#339, #340, #341), asserted on
///     measured geometry and resolved brushes, in every headless culture: the
///     Signal card's "Spectrum" caption keeps its natural width (#339), the
///     selected Pre/Post segment reads (#340), and the cabinet's IR blend slider
///     clears its label while the IR knob captions keep a gap (#341).
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigEditorLabelsLayoutUiPerfSmokeTests
{
    private sealed class Renderer : IOfflineToneRenderer
    {
        public bool TryRender(DcRigPreset rig, float[] left, float[] right, int sampleRate) => true;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
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

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (hi < lo) (hi, lo) = (lo, hi);
        return (hi + 0.05) / (lo + 0.05);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(520)]
    public async Task TheSignalCard_KeepsTheSpectrumCaptionWhole_AndTheSelectedSegmentReadable(int width)
    {
        var chain = new FakeDspChainService();
        var node = chain.AddNode("overdrive")!.NodeId;
        var spectrum = new RigNodeSpectrumViewModel(() => new Renderer(), () => chain, () => node);
        await spectrum.MeasureCommand.ExecuteAsync(null);

        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var meters = new RigNodeMetersViewModel(() => chain, () => node) { Spectrum = spectrum };
            var view = new RigNodeMetersView { DataContext = meters, Width = width };
            var window = new Window { Width = width + 40, Height = 420, Content = view };
            window.Show();
            try
            {
                Settle(window);
                var host = view.GetVisualDescendants().OfType<RigNodeSpectrumView>().Single();
                var caption = host.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Classes.Contains("FireEyebrow"));
                var natural = new TextBlock
                {
                    Text = caption.Text, FontFamily = caption.FontFamily, FontSize = caption.FontSize,
                    FontWeight = caption.FontWeight, FontStyle = caption.FontStyle, LetterSpacing = caption.LetterSpacing,
                };
                natural.Measure(Size.Infinity);
                Assert.True(caption.Bounds.Width >= natural.DesiredSize.Width - 0.5,
                    $"'{caption.Text}' is {caption.Bounds.Width:0.#} px wide but needs {natural.DesiredSize.Width:0.#}");
                Capture(window, $"signal-card-{width}.png");

                // the selected tap (Post by default) draws its text in a readable
                // colour on its fill, through the brushes the control resolved
                var post = host.FindControl<ToggleButton>("RigSpectrumPost")!;
                Assert.True(post.IsChecked);
                var text = post.GetVisualDescendants().OfType<TextBlock>().First();
                var presenter = post.GetVisualDescendants().OfType<ContentPresenter>().First();
                var fg = Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color;
                var fill = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
                var under = Over(fill, Color.FromRgb(20, 20, 24));
                var ratio = Contrast(Over(fg, under), under);
                Assert.True(ratio >= 4.5, $"selected segment text {fg} on {fill}: contrast {ratio:0.0}:1");
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task TheCabinet_BlendSliderClearsItsLabel_AndIrKnobCaptionsKeepAGap() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "cab"));
            rig.SelectNodeAt(0);
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                Settle(window);
                var cab = Assert.Single(view.GetVisualDescendants().OfType<RigCabinetEditorView>());
                var slider = cab.GetVisualDescendants().OfType<Slider>()
                    .Single(s => s.Classes.Contains("FireSlider") && s.Parent is Grid g && g.Children.OfType<TextBlock>().Any());
                Assert.True(slider.IsEffectivelyVisible, "the IR blend slider is not shown");
                var label = ((Grid)slider.Parent!).Children.OfType<TextBlock>().First();
                var labelRight = label.TranslatePoint(new Point(label.Bounds.Width, 0), cab)!.Value.X;
                var sliderLeft = slider.TranslatePoint(default, cab)!.Value.X;
                Assert.True(sliderLeft - labelRight >= 6, $"the blend slider starts {sliderLeft - labelRight:0.#} px after its label");

                var slots = cab.FindControl<ItemsControl>("RigCabSlots")!;
                var knobs = slots.GetVisualDescendants().OfType<FireKnob>().Where(k => k.IsEffectivelyVisible).ToList();
                Assert.True(knobs.Count >= 4, $"{knobs.Count} IR knobs");
                var pairs = 0;
                foreach (var a in knobs)
                {
                    var ap = a.TranslatePoint(default, cab)!.Value;
                    foreach (var b in knobs)
                    {
                        var bp = b.TranslatePoint(default, cab)!.Value;
                        if (ReferenceEquals(a, b) || Math.Abs(ap.Y - bp.Y) > 1 || bp.X <= ap.X) continue;
                        pairs++;
                        var gap = bp.X - a.TranslatePoint(new Point(a.Bounds.Width, 0), cab)!.Value.X;
                        Assert.True(gap >= 4, $"IR knob captions '{a.Label}' and '{b.Label}' are {gap:0.#} px apart");
                    }
                }

                Assert.True(pairs >= 2, $"{pairs} adjacent IR knob pairs");
                Capture(window, "cab-labels.png");
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
}
