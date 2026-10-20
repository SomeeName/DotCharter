using DotCharter.Localization;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Equalizer;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Renders the inspector's EQ module to a PNG at every width and height the
///     dockable pane can actually take, on the mockup's own page ground — so the
///     result can be put beside `EQ and Filters.dc.html` and COMPARED.
/// </summary>
/// <remarks>
///     <para>
///         Same reason <see cref="PopoverPixelUiPerfSmokeTests" /> exists: a
///         previous UI pass on this repo wrote markup, never looked at it, and
///         shipped something the owner rejected on sight. No bounds assertion can
///         catch "wrong colour", "the curve is drawn but the handles are not" or
///         "the chip row wrapped at 420".
///     </para>
///     <para>
///         The mockup draws 420 and 600 and states the principle "fits the
///         inspector at any dock width". The pane is genuinely variable — it is
///         dockable AND floats into its own window — so the sweep adds 800, a
///         case wider than the mockup ever drew, and a DELIBERATELY SHORT
///         height. Those are where a layout breaks, and neither is in the
///         drawing.
///     </para>
///     <para>
///         It also sweeps the four STATES no mockup draws and every one of which
///         the owner has now seen: no engine, nothing playing, the section
///         bypassed, and a gainless band selected. A state nobody has looked at
///         is how "it ships visibly disabled" becomes "it ships looking broken".
///     </para>
///     <para>
///         Without <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> the captures are skipped and
///         the ASSERTIONS still run. That half is the regression test; the
///         pictures are the review tool.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class EqModulePixelUiPerfSmokeTests
{
    /// <summary>The mockup's page ground, <c>#08070e</c>.</summary>
    private static readonly Color PageGround = Color.FromRgb(0x08, 0x07, 0x0E);

    private const double Inset = 16;

    private static string ArtifactDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestResults", "eq-module");

    /// <summary>
    ///     The height that means "let the module be as tall as it wants".
    ///     <para>
    ///         <b>That is the REAL host, and pinning a number here would have
    ///         been the harness testing the harness.</b> The module hangs inside
    ///         a vertically scrolling inspector, so what it is measured against
    ///         is infinity and what it gets is its own desired height — which is
    ///         exactly the mechanism the 58 px plot came out of. A capture in a
    ///         fixed-height window shows the plot ABSORBING whatever is left
    ///         over, i.e. the one size the product never renders.
    ///     </para>
    /// </summary>
    private const double Natural = 0;

    /// <summary>How much page the natural cases are drawn on. The module takes
    /// what it wants off the top; the rest is ground, like the mockup's page.</summary>
    private const double PageHeight = 470;

    public static TheoryData<string, double, double> Sizes => new()
    {
        // The mockup's two widths, verbatim.
        { "docked-narrow", 420, Natural },
        { "inspector-width", 600, Natural },
        { "inspector-wide", 800, Natural },
        // Deliberately short: the inspector's bottom dock is drag-resized and the
        // owner will make it small. The plot must give up its space and the band
        // editor must NOT crush.
        { "short", 420, 250 },
        // Wider than the mockup ever drew. The pane can be dragged past 800 and
        // the module also floats into its own window.
        { "very-wide", 1200, Natural },
        // The state no mockup draws and the one this repo cares most about: an
        // engine with no parametric EQ. Every control disabled, the reason
        // PRINTED. It is captured so that state gets reviewed too — a disabled
        // surface nobody has looked at is how "it ships visibly disabled"
        // becomes "it ships looking broken".
        { "no-engine", 420, Natural },
        // Nothing playing. The analyser falls back to the MARKED preview
        // spectrum, which is the state the module spends most of its life in —
        // and the one that used to draw an empty well.
        { "silent", 420, Natural },
        // Master power off. The curve goes grey, the glow goes, and the
        // BYPASSED tag appears. This is what the owner's screenshot would have
        // looked like if the section really had been bypassed.
        { "bypassed", 420, Natural },
        // A high pass selected, i.e. a band with NO gain — the row that shows
        // the slope instead of a slider, and the handle whose vertical axis is
        // now Q.
        { "gainless-band", 420, Natural }
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task TheEqModule_laysOutAtEveryDockSize_andIsCaptured(string id, double width, double height) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            _ = App.Services
                ?? throw new InvalidOperationException("the headless app session did not boot.");

            var vm = BuildSeededViewModel(id);
            var view = new EqModuleView { DataContext = vm, Width = width };

            // The real host is a vertically SCROLLING inspector, so the module is
            // measured against infinite height and takes its own. Reproducing
            // that is the whole point — a plain fixed-height window hands the
            // plot the leftover space and shows a size the product never renders,
            // which is how a previous verification in this program proved
            // nothing. A constrained case sets an explicit Height instead.
            if (height > Natural) view.Height = height;
            else view.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

            var pageHeight = height > Natural ? height : PageHeight;
            var host = new Border
            {
                Background = new SolidColorBrush(PageGround),
                Padding = new Thickness(Inset),
                Child = view
            };

            // The module carries NO StyleInclude of its own — `MixerInspectorView`
            // includes `InspectorStyles.axaml` once for everything it hosts, which
            // is the same arrangement the seven inspector tabs have. Swept bare,
            // every class here would match no selector and the whole capture would
            // be of an unstyled tree; the UI sweep DERIVES that grammar, and this
            // harness supplies it explicitly for the same reason.
            var window = new Window
            {
                Width = width + 2 * Inset,
                Height = pageHeight + 2 * Inset,
                Background = new SolidColorBrush(PageGround),
                Content = host
            };
            window.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });

            window.Show();
            try
            {
                host.Measure(new Size(window.Width, window.Height));
                host.Arrange(new Rect(0, 0, window.Width, window.Height));
                window.UpdateLayout();

                AssertLaysOut(id, view, width, height);
                AssertPlotIsTheMockupsSize(id, view, natural: height <= Natural);
                AssertPlotDrewTheRightSpectrum(id, view);
                AssertEverySliderCanBeResetToItsDefault(id, view);
                if (height <= Natural) AssertTheWholeModuleFits(id, view);
                Capture(window, id, width, view.Bounds.Height);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     What must be true at every one of those sizes. Deliberately not "the
    ///     view measured non-zero" alone: a `UserControl` given an explicit Width
    ///     arranges to it whether or not anything inside it survived, which is
    ///     precisely the weak check the sweep's own harness warns about.
    /// </summary>
    private static void AssertLaysOut(string id, EqModuleView view, double width, double height)
    {
        Assert.True(view.Bounds is { Width: > 1, Height: > 1 },
            $"'{id}' laid out {view.Bounds} — it rendered nothing, which is what a failed " +
            "DynamicResource, a missing ControlTheme and a Space* token in a Padding all look like");

        // The PLOT is the flexible part and the one that can be starved to
        // nothing without any of its ancestors noticing.
        var plot = view.GetVisualDescendants().OfType<EqPlotControl>().FirstOrDefault();
        Assert.True(plot is { Bounds: { Width: > 1, Height: > 1 } },
            $"'{id}' has no laid-out plot — the curve area collapsed");
        Assert.True(plot!.Bounds.Height >= 56,
            $"'{id}' gave the plot only {plot.Bounds.Height:0.#} px — the module's MinHeight and the " +
            "plot's own floor are what stop a short dock crushing the curve into a line");

        // The six chips must be one ROW of six, not two rows of three. A
        // UniformGrid that wrapped is the first thing that stops this being the
        // drawing, and it measures perfectly.
        var chips = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("EqChip"))
            .ToList();
        Assert.Equal(EqLimits.BandCount, chips.Count);
        var chipTop = chips[0].Bounds.Y;
        Assert.All(chips, chip => Assert.Equal(chipTop, chip.Bounds.Y, 1));
        Assert.All(chips, chip => Assert.True(chip.Bounds.Width > 8,
            $"'{id}' squeezed a band chip to {chip.Bounds.Width:0.#} px"));

        // NOTHING may be drawn on top of anything else. This is the assertion
        // the short case exists for: at 420 x 250 the module used to draw its
        // plot straight through the header and the chip row, and every bounds
        // check still passed because each control had measured perfectly.
        var plotBottom = (plot.TranslatePoint(new Point(0, plot.Bounds.Height), view)
                          ?? new Point(0, 0)).Y;
        var firstChipTop = (chips[0].TranslatePoint(new Point(0, 0), view) ?? new Point(0, 0)).Y;
        Assert.True(firstChipTop >= plotBottom - 1,
            $"'{id}' drew the band chips at y={firstChipTop:0.#} under a plot ending at " +
            $"y={plotBottom:0.#} — the module overlapped itself instead of scrolling");

        // Every param row's value rail must still be on screen. A slider that
        // pushed its readout off the right edge is the classic variable-width
        // failure and no bounds check on the ROW would see it.
        foreach (var value in view.GetVisualDescendants().OfType<TextBlock>()
                     .Where(t => t.Classes.Contains("EqParamValue")))
        {
            var origin = value.TranslatePoint(new Point(0, 0), view) ?? new Point(0, 0);
            Assert.True(origin.X + value.Bounds.Width <= width + 1,
                $"'{id}' pushed a value readout to {origin.X + value.Bounds.Width:0.#} px in a " +
                $"{width:0} px module");
        }

        _ = height;
    }

    /// <summary>
    ///     The plot gets the mockup's 104 px well, and it gets it from ITS OWN
    ///     desired height rather than from the host.
    ///     <para>
    ///         This is the assertion the owner's "so squished together in height
    ///         that it isn't even readable" needed and nothing had: the module
    ///         hangs the plot in a Grid <c>*</c> row inside a vertically
    ///         scrolling inspector, which measures with INFINITE height, so the
    ///         star row is worth exactly the child's desired size — and with none
    ///         of its own the row collapsed to the well's <c>MinHeight</c> and the
    ///         curve got 58 px. Every bounds check in this file passed while it
    ///         did.
    ///     </para>
    /// </summary>
    private static void AssertPlotIsTheMockupsSize(string id, EqModuleView view, bool natural)
    {
        var plot = view.GetVisualDescendants().OfType<EqPlotControl>().Single();

        // The ask is the PLOT's, not the well's. A MinHeight on the Border would
        // produce the same drawn height and would stop a constrained dock ever
        // shrinking the curve.
        Assert.Equal(EqPlotControl.DefaultPreferredHeight, plot.DesiredSize.Height, 1);

        Assert.True(plot.Bounds.Height >= EqPlotControl.DefaultPreferredHeight - 0.5,
            $"'{id}' gave the plot {plot.Bounds.Height:0.#} px against the mockup's " +
            $"{EqPlotControl.DefaultPreferredHeight:0} — a star row measured against infinity is worth " +
            "its child's DESIRED size, so the plot has to ask for one");

        // In the host the product actually has — unconstrained — it must be the
        // mockup's size EXACTLY. `>=` alone would pass a plot that had grown to
        // fill a window, which is what every fixed-height capture shows and the
        // real inspector never does.
        if (natural)
            Assert.Equal(EqPlotControl.DefaultPreferredHeight, plot.Bounds.Height, 1);
    }

    /// <summary>
    ///     Which analyser the plot drew, and that it is the honest one.
    ///     <para>
    ///         The preview spectrum exists because the owner asked for one ("we
    ///         still want a preview spectrogram … when silence a preview
    ///         spectrogram"), and the rule it ships under is that it must never
    ///         read as measured audio — so it is only ever drawn when the frame
    ///         really is silent, and it is drawn with the tag.
    ///     </para>
    /// </summary>
    private static void AssertPlotDrewTheRightSpectrum(string id, EqModuleView view)
    {
        var plot = view.GetVisualDescendants().OfType<EqPlotControl>().Single();
        var expected = id is "silent" or "no-engine" ? EqSpectrumKind.Preview : EqSpectrumKind.Live;

        Assert.Equal(expected, plot.SpectrumKind);
        Assert.False(string.IsNullOrWhiteSpace(plot.PreviewLabel),
            $"'{id}' has no preview tag, so a made-up spectrum would be indistinguishable from a measured one");
    }

    /// <summary>
    ///     Every slider in the module declares what a double-click resets it to.
    ///     <para>
    ///         The owner reported this one as flatly missing ("you also can't
    ///         double click sliders to reset them to default value") and it was:
    ///         the mixer's seven faders had the gesture, the EQ's four never did.
    ///         Asserted over the LAID-OUT tree rather than as a source lint so
    ///         the binding really resolved — an
    ///         <c>ResetOnDoubleClick.DefaultValue="{Binding …}"</c> pointing at
    ///         nothing leaves NaN behind and reads exactly like no declaration.
    ///     </para>
    /// </summary>
    private static void AssertEverySliderCanBeResetToItsDefault(string id, EqModuleView view)
    {
        var sliders = view.GetVisualDescendants().OfType<Slider>().ToList();
        Assert.Equal(4, sliders.Count); // freq, gain (or the slope row's absence), Q, trim

        foreach (var slider in sliders)
        {
            Assert.True(ResetOnDoubleClick.IsArmed(slider),
                $"'{id}' has a slider between {slider.Minimum:0.##} and {slider.Maximum:0.##} with no " +
                "reset default — double-clicking it does nothing");
        }
    }

    /// <summary>
    ///     At its natural height the module shows ALL of itself, footer included.
    ///     <para>
    ///         The footer carries the routing segment and the output trim, and it
    ///         is the row a taller plot pushes off the bottom. Worth an assertion
    ///         because the failure is silent: the ScrollViewer simply scrolls, so
    ///         every other check here passes over a module whose last row nobody
    ///         can see without dragging.
    ///     </para>
    /// </summary>
    private static void AssertTheWholeModuleFits(string id, EqModuleView view)
    {
        var trim = view.GetVisualDescendants().OfType<Slider>().Last();
        var bottom = (trim.TranslatePoint(new Point(0, trim.Bounds.Height), view) ?? new Point(0, 0)).Y;

        Assert.True(bottom > 0 && bottom <= view.Bounds.Height + 1,
            $"'{id}' put the trim slider's bottom edge at y={bottom:0.#} in a {view.Bounds.Height:0.#} px " +
            "module — the footer scrolled out of the capture");
    }

    /// <summary>
    ///     A module with something to draw.
    ///     <para>
    ///         A fresh section has every band OFF, which is correct (it is what
    ///         the engine boots at) and completely uninformative as a picture: a
    ///         flat line and six grey handles. The capture loads a real preset and
    ///         feeds the analyser a synthetic spectrum, so the review compares
    ///         against a mockup that is also drawing a live EQ.
    ///     </para>
    ///     <para>
    ///         The spectrum is put on the VIEW MODEL directly rather than through
    ///         the engine: a headless run hears nothing, so the analyser would
    ///         otherwise render empty and its whole styling would never be looked
    ///         at. It is clearly synthetic and it is only ever a picture.
    ///     </para>
    /// </summary>
    private static EqModuleViewModel BuildSeededViewModel(string id)
    {
        // A FAKE service rather than the app's own, deliberately. The headless
        // session opens no audio device, so the real service reports no
        // parametric EQ and every capture would be of the disabled state — which
        // is a real state worth reviewing (see the `no-engine` case) but is not
        // the thing being compared to the mockup.
        var audio = new FakeAudioPlaybackService { SupportsParametricEq = id != "no-engine" };
        // The `silent` case leaves the fake engine's analyser reporting nothing,
        // which is what a stopped transport really does — and is what the plot's
        // marked preview spectrum exists for.
        if (id == "silent") audio.EqAnalyserHasSignal = false;

        var vm = new EqModuleViewModel(audio, LocalizationService.Instance)
        {
            SelectedPresetKey = "FindTheGuitar",
            SelectedIndex = id == "gainless-band" ? 0 : 3,
            TrimDb = 4.0
        };
        if (id == "bypassed") vm.IsSectionEnabled = false;

        // The analyser frame comes from the fake ENGINE, through the same
        // PollSpectrum path the product uses — not written onto the view model
        // here. Setting it directly looked right and was a lie: attaching the
        // view starts the poll timer, which overwrote it on the first tick, and
        // the first four captures were of an empty analyser for that reason.
        vm.PollSpectrum();
        return vm;
    }

    // ── capture ─────────────────────────────────────────────────────────────

    private static void Capture(Window window, string id, double width, double height)
    {
        if (!UiSweepRunner.CaptureEnabled) return;

        Directory.CreateDirectory(ArtifactDirectory);
        try
        {
            var frame = HeadlessWindowExtensions.CaptureRenderedFrame(window);
            if (frame is null) return;

            frame.Save(Path.Combine(ArtifactDirectory, id + ".png"), new PngBitmapEncoderOptions());
            AppendToSheet(id, width, height);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            // A capture is a review aid, never the assertion.
        }
    }

    private static void AppendToSheet(string id, double width, double height)
    {
        var path = Path.Combine(ArtifactDirectory, "index.html");
        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "<!doctype html><meta charset=\"utf-8\"><title>EQ module vs EQ and Filters.dc.html</title>" +
                "<style>body{margin:0;padding:26px 30px;background:#08070e;color:#f4f0fb;" +
                "font-family:'Space Grotesk',system-ui,sans-serif}" +
                ".g{display:flex;flex-wrap:wrap;gap:22px;align-items:flex-start}" +
                ".c{display:flex;flex-direction:column;gap:9px}" +
                ".t{font-family:'IBM Plex Mono',monospace;font-size:9.5px;letter-spacing:.2em;color:#8b81a3}" +
                "img{display:block}</style>" +
                "<h1 style=\"font-size:20px;margin:0 0 18px\">Rendered EQ module &mdash; compare against " +
                "<code>EQ and Filters.dc.html</code></h1><div class=\"g\">\n");
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<div class=\"c\"><div class=\"t\">{id.ToUpperInvariant()} &middot; {width:0} × {height:0}</div>");
        sb.Append(CultureInfo.InvariantCulture, $"<img src=\"{id}.png\" alt=\"{id}\"></div>\n");
        File.AppendAllText(path, sb.ToString());
    }
}
