using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Amp &amp; FX rig's FOOTER strip and the module editor above it, both
///     reported by the owner on 2026-09-12: "the footer jumps around while the
///     tuner reads" and "the pedal's options view renders behind the footer
///     strip".
///     <para>
///         The first is real and is fixed by fixed-width columns — see
///         <see cref="TheFooter_DoesNotMove_WhenTheTunerFindsANote" />, which
///         renders the strip in both tuner states and compares X.
///     </para>
///     <para>
///         The second is NOT a z-order fault and this class says which: the
///         editor and the strip live in different rows of the screen's root grid
///         and never overlap, so
///         <see cref="TheModuleEditor_NeverPaintsIntoTheFooter" /> asserts
///         NON-INTERSECTION rather than a hit-test winner. What the owner saw is
///         the editor being CUT at the row boundary — which is the strip's top
///         edge — by its own auto-hiding scroller, so
///         <see cref="TheModuleEditor_KeepsItsScrollbarUp_WhenItIsTallerThanTheDock" />
///         pins the affordance that says the content continues.
///     </para>
///     <para>
///         <c>*UiPerfSmokeTests</c> + <c>[Collection("HeadlessUI")]</c>: needs
///         the real Avalonia session, so it runs in the headless suite's own
///         <c>dotnet test</c>, not the default gate.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigFooterStabilityUiPerfSmokeTests(ITestOutputHelper output)
{
    private const double WindowWidth = 1920;
    private const double WindowHeight = 1080;

    /// <summary>The region of <paramref name="v" /> that actually reaches the
    /// screen: its own box intersected with every clipping ancestor's, in
    /// <paramref name="root" /> coordinates. Comparing raw <c>Bounds</c> would
    /// report an overflow that a ScrollViewer never draws.</summary>
    private static Rect PaintedRect(Visual v, Visual root)
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

    /// <summary>What the CPU readout's engine reader answers; null = no engine.</summary>
    private static AudioCallbackLoad? CpuLoad { get; set; } = new AudioCallbackLoad(0.14, 0.31);

    /// <summary>The rig screen over a real chain, attached and laid out.</summary>
    private static void WithRig(Action<AmpFxRigView, AmpFxRigViewModel, PedalboardViewModel, Window> body,
        IEnumerable<string>? gear = null, double width = WindowWidth, double height = WindowHeight, bool cpu = true)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var chain = new FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
        foreach (var id in gear ?? ["overdrive", "amp", "cab"])
            rig.AddGear(catalogue.Single(m => m.Id == id));
        rig.SelectNodeAt(0);

        // the side rack is the DETAILS column; these cases measure it, so it is open
        // every footer cluster the app can show, the drop-tune pedal included,
        // so the one-line budget is measured with all of them
        var dropSemis = 0;
        var model = new AmpFxRigViewModel(rig)
        {
            IsDetailsOpen = true,
            DropTune = new RigDropTuneViewModel(() => dropSemis, v => dropSemis = v),
            // …and the CPU readout, reading whatever the case sets
            CallbackLoadProvider = cpu ? () => CpuLoad : null
        };
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

    // ══ 1. the footer holds still ════════════════════════════════════════════

    /// <summary>
    ///     The tuner's two states print a 24 px string ("–" with a wrapped
    ///     status hint beside it) and a 29 px one ("F#4" with "-49 ¢"), and the
    ///     footer laid them out with everything auto-sized, so every note the
    ///     detector found and lost slid the reference box, the tempo box, the
    ///     jack picker and both meters sideways. Every readout is in a fixed
    ///     column now; this renders both states and compares X.
    /// </summary>
    [Fact]
    public async Task TheFooter_DoesNotMove_WhenTheTunerFindsANote()
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, _, rig, window) =>
        {
            var footer = view.FindControl<Border>("RigFooter")!;
            var watched = new[]
            {
                "RigTunerMeter", "RigTunerReadout", "RigTunerReference",
                "RigTempoBox", "RigTempoTap", "RigInputChannelPicker",
                "RigInputChannelCombo", "RigInputMeter", "RigOutputStrip", "RigOutputMeter", "RigCpuStrip"
            };

            Dictionary<string, Rect> Snapshot()
            {
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                return watched.ToDictionary(n => n,
                    n => PaintedRect(view.FindControl<Control>(n)!, view));
            }

            // NO READING: the dash and the longest status hint the tuner has.
            var tuner = rig.RigTuner;
            tuner.HasReading = false;
            tuner.NoteText = "–";
            tuner.CentsText = string.Empty;
            tuner.StatusHint = "Audio engine not running - configure an input device in Settings first";
            var quiet = Snapshot();
            var quietHeight = footer.Bounds.Height;

            // A READING: the widest note name and the widest cent deviation.
            tuner.HasReading = true;
            tuner.NoteText = "A#10";
            tuner.CentsText = "-50 ¢";
            tuner.StatusHint = string.Empty;
            var reading = Snapshot();

            output.WriteLine($"footer height quiet={quietHeight:F0} reading={footer.Bounds.Height:F0}");
            foreach (var name in watched)
                output.WriteLine($"  {name,-22} quiet={quiet[name]} reading={reading[name]}");

            foreach (var name in watched)
            {
                Assert.True(Math.Abs(quiet[name].X - reading[name].X) < 0.5,
                    $"'{name}' moved from x={quiet[name].X:F1} to x={reading[name].X:F1} when the tuner " +
                    "found a note — a readout beside it is still auto-sized");
                Assert.True(Math.Abs(quiet[name].Width - reading[name].Width) < 0.5,
                    $"'{name}' changed width {quiet[name].Width:F1} -> {reading[name].Width:F1}");
            }

            // …and the strip stays ONE line. `FireRowDesc` wraps, so before the
            // fix the longest status hint took the footer to three lines and 80
            // px, which came straight out of the module editor's row.
            Assert.Equal(quietHeight, footer.Bounds.Height, 1);
            Assert.True(footer.Bounds.Height <= 56,
                $"the footer is {footer.Bounds.Height:F0} px tall — a readout in it is wrapping");
        }), CancellationToken.None);
    }

    /// <summary>
    ///     A TUNING picked in the footer: the picker and one chip per string lay
    ///     out with real size, and PICKING one moves nothing — the owner saw the
    ///     whole footer shift between Chromatic, a six-string and a bass tuning,
    ///     because the note / strings cell took whatever width it held. It also
    ///     checks the picker's name, the chips and the note sit on the strip's
    ///     vertical centre. With the capture env vars set it writes a PNG a state.
    /// </summary>
    [Fact]
    public async Task ATuning_ShowsItsStrings_AndPickingOneMovesNothing()
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, _, rig, window) =>
        {
            var footer = view.FindControl<Border>("RigFooter")!;
            var strip = view.FindControl<Grid>("RigTunerStrip")!;
            var picker = view.FindControl<ComboBox>("RigTunerTuning")!;
            var strings = view.FindControl<ItemsControl>("RigTunerStrings")!;
            var note = view.FindControl<Control>("RigTunerNote")!;
            var tuner = rig.RigTuner;
            tuner.HasReading = true;
            tuner.NoteText = "D2";
            var watched = new[] { "RigTunerMeter", "RigTunerReadout", "RigTunerReference", "RigTempoBox", "RigInputChannelCombo", "RigDropTuneCombo" };

            (Dictionary<string, Rect> At, double Height) Pick(string? name, bool bass = false)
            {
                tuner.SelectedTuning = name is null
                    ? tuner.Tunings[0]
                    : tuner.Tunings.Single(c => c.Tuning is { } t && t.IsBass == bass && t.Name == name);
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                CaptureFooter(window, footer, $"footer-tuning-{(name ?? "chromatic").Replace(' ', '-')}{(bass ? "-bass" : "")}.png");
                return (watched.ToDictionary(n => n, n => PaintedRect(view.FindControl<Control>(n)!, view)), footer.Bounds.Height);
            }

            var chromatic = Pick(null);
            Assert.True(picker.Bounds.Width > 60 && picker.Bounds.Height > 10, $"the tuning picker measured {picker.Bounds}");
            Assert.False(strings.IsEffectivelyVisible, "chromatic shows no string chips");
            Assert.True(note.IsEffectivelyVisible, "chromatic names the note");
            AssertCentred(note, strip, "the note");
            AssertCentred(PickerText(picker), strip, "the picker's name");
            // the needle's TRACK, not its box: the meter draws a TrackHeight-tall
            // well from its own top, so a box stretched over the row painted it
            // high (owner: "the thing that shows if the string is in tune")
            var meter = view.FindControl<FireTunerMeter>("RigTunerMeter")!;
            var track = PaintedRect(meter, strip);
            var trackMiddle = track.Y + Math.Min(track.Height, meter.TrackHeight) / 2;
            Assert.True(Math.Abs(trackMiddle - strip.Bounds.Height / 2) <= 1.0,
                $"the tuner meter's track is centred at y={trackMiddle:F1}, the strip at y={strip.Bounds.Height / 2:F1}");

            // every tuning's name fits the closed picker, untrimmed
            foreach (var choice in tuner.Tunings)
            {
                tuner.SelectedTuning = choice;
                for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
                var shown = (TextBlock)PickerText(picker);
                var needed = new TextBlock { Text = shown.Text, FontFamily = shown.FontFamily, FontSize = shown.FontSize, FontWeight = shown.FontWeight };
                needed.Measure(Size.Infinity);
                Assert.True(needed.DesiredSize.Width <= shown.Bounds.Width + 0.5,
                    $"'{shown.Text}' needs {needed.DesiredSize.Width:F1} px and the picker gives it {shown.Bounds.Width:F1} - it is trimmed");
            }

            var dropD = Pick("Drop D");
            var chips = strings.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("FireTunerString")).ToList();
            Assert.Equal(6, chips.Count);
            Assert.False(note.IsEffectivelyVisible, "with a tuning the lit chip is the note");
            Assert.All(chips, c => Assert.True(c.Bounds.Width >= 16 && c.Bounds.Height >= 18, $"a string chip measured {c.Bounds}"));
            Assert.All(chips, c => AssertCentred(c, strip, "a string chip"));
            AssertCentred(PickerText(picker), strip, "the picker's name");

            tuner.Strings[0].IsCurrent = true;
            tuner.Strings[1].IsTuned = true;
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Assert.True(chips[0].Classes.Contains("current") && chips[1].Classes.Contains("tuned"));
            CaptureFooter(window, footer, "footer-tuning-Drop-D-lit.png");

            var dadgad = Pick("DADGAD");
            var bass = Pick("Drop D", bass: true);
            Assert.Equal(4, strings.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("FireTunerString")));

            foreach (var (label, state) in new[] { ("Drop D", dropD), ("DADGAD", dadgad), ("bass Drop D", bass) })
            {
                foreach (var name in watched)
                {
                    Assert.True(Math.Abs(chromatic.At[name].X - state.At[name].X) < 0.5,
                        $"'{name}' moved from x={chromatic.At[name].X:F1} to x={state.At[name].X:F1} when {label} was picked");
                }

                Assert.True(state.Height <= 56, $"the footer is {state.Height:F0} px tall with {label} picked - it wrapped");
            }
        }), CancellationToken.None);
    }

    /// <summary>
    ///     The DROP TUNE pedal sits beside the input jack: it lays out, picking −12
    ///     writes −12 to the host's value and marks the pedal engaged, and neither
    ///     "0" nor "−12" moves anything or wraps the footer.
    /// </summary>
    [Fact]
    public async Task TheDropTunePedal_WritesTheHostsValue_AndHoldsStill()
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, _, window) =>
        {
            var footer = view.FindControl<Border>("RigFooter")!;
            var combo = view.FindControl<ComboBox>("RigDropTuneCombo")!;
            Assert.True(combo.IsEffectivelyVisible && combo.Bounds.Width > 40 && combo.Bounds.Height > 10,
                $"the drop-tune picker measured {combo.Bounds}");
            var output = PaintedRect(view.FindControl<Control>("RigOutputMeter")!, view);

            var pedal = model.DropTune!;
            combo.SelectedItem = pedal.Steps.Single(s => s.Semitones == -12);
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Assert.Equal(-12, pedal.Semitones);
            Assert.True(pedal.IsEngaged);
            Assert.Equal("−12", ((RigDropTuneStep)combo.SelectedItem!).Label);
            // …and the box SHOWS it: at 64 px it drew a lone dash (owner's capture)
            var shown = (TextBlock)PickerText(combo);
            var needed = new TextBlock { Text = shown.Text, FontFamily = shown.FontFamily, FontSize = shown.FontSize, FontWeight = shown.FontWeight };
            needed.Measure(Size.Infinity);
            Assert.True(needed.DesiredSize.Width <= shown.Bounds.Width + 0.5,
                $"'{shown.Text}' needs {needed.DesiredSize.Width:F1} px and the drop picker gives it {shown.Bounds.Width:F1} - it is trimmed");
            Assert.Equal(output.X, PaintedRect(view.FindControl<Control>("RigOutputMeter")!, view).X, 1);
            Assert.True(footer.Bounds.Height <= 56, $"the footer is {footer.Bounds.Height:F0} px tall with the drop pedal - it wrapped.");
            CaptureFooter(window, footer, "footer-droptune-minus12.png");
        }), CancellationToken.None);
    }

    /// <summary>
    ///     The CPU readout (JuceSharp #29, ABI v46): it lays out with real size, prints
    ///     the engine's measured average once the meter clock polls, warns past 80 %
    ///     peak, prints a dash with no engine — and none of those, nor the widest
    ///     values its cell can hold ("98%", "99+"), moves a neighbour or wraps the footer.
    /// </summary>
    [Fact]
    public async Task TheCpuReadout_PrintsTheMeasuredLoad_AndHoldsStill()
    {
        try
        {
            await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, _, window) =>
            {
                var footer = view.FindControl<Border>("RigFooter")!;
                var strip = view.FindControl<Grid>("RigCpuStrip")!;
                var value = view.FindControl<TextBlock>("RigCpuValue")!;
                var output = PaintedRect(view.FindControl<Control>("RigOutputMeter")!, view);

                (Rect At, double Height) Show(AudioCallbackLoad? load, string capture)
                {
                    CpuLoad = load;
                    model.CpuLoad!.Refresh();
                    for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                    CaptureFooter(window, footer, capture);
                    return (PaintedRect(strip, view), footer.Bounds.Height);
                }

                var normal = Show(new AudioCallbackLoad(0.14, 0.31), "footer-cpu-14.png");
                Assert.True(strip.IsEffectivelyVisible, "the CPU readout is hidden with an engine reader");
                Assert.True(normal.At.Width > 30 && normal.At.Height > 8, $"the CPU readout measured {normal.At}");
                Assert.True(value.Bounds.Width > 1 && value.Bounds.Height > 1, $"the CPU value measured {value.Bounds}");
                Assert.Equal("14%", value.Text);
                Assert.DoesNotContain("Warn", value.Classes);

                var high = Show(new AudioCallbackLoad(0.62, 0.93), "footer-cpu-high.png");
                Assert.Equal("62%", value.Text);
                Assert.Contains("Warn", value.Classes);

                var widest = Show(new AudioCallbackLoad(0.98, 0.98), "footer-cpu-widest.png");
                Assert.Equal("98%", value.Text);
                var needed = new TextBlock { Text = value.Text, FontFamily = value.FontFamily, FontSize = value.FontSize, FontWeight = value.FontWeight };
                needed.Measure(Size.Infinity);
                Assert.True(needed.DesiredSize.Width <= value.Bounds.Width + 0.5,
                    $"'{value.Text}' needs {needed.DesiredSize.Width:F1} px and the CPU cell gives it {value.Bounds.Width:F1} - it is trimmed");

                var over = Show(new AudioCallbackLoad(1.4, 2.0), "footer-cpu-over.png");
                Assert.Equal("99+", value.Text);
                needed.Text = value.Text;
                needed.Measure(Size.Infinity);
                Assert.True(needed.DesiredSize.Width <= value.Bounds.Width + 0.5,
                    $"'{value.Text}' needs {needed.DesiredSize.Width:F1} px and the CPU cell gives it {value.Bounds.Width:F1} - it is trimmed");

                var none = Show(null, "footer-cpu-no-engine.png");
                Assert.Equal("–", value.Text);

                foreach (var (label, state) in new[] { ("amber", high), ("98%", widest), ("99+", over), ("no engine", none) })
                {
                    Assert.Equal(normal.At.X, state.At.X, 1);
                    Assert.Equal(normal.At.Width, state.At.Width, 1);
                    Assert.True(state.Height <= 56, $"the footer is {state.Height:F0} px tall showing {label} - it wrapped");
                }

                Assert.Equal(output.X, PaintedRect(view.FindControl<Control>("RigOutputMeter")!, view).X, 1);
            }), CancellationToken.None);
        }
        finally
        {
            CpuLoad = new AudioCallbackLoad(0.14, 0.31);
        }
    }

    /// <summary>A host with no engine reader (a test, a rig built offline) shows no
    /// CPU readout at all rather than a number nothing computes.</summary>
    [Fact]
    public async Task WithoutAnEngineReader_TheCpuReadoutIsNotDrawn()
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, _, _, window) =>
        {
            var footer = view.FindControl<Border>("RigFooter")!;
            Assert.True(footer.Bounds.Height > 1, "the footer did not lay out");
            Assert.False(view.FindControl<Grid>("RigCpuStrip")!.IsEffectivelyVisible);
            CaptureFooter(window, footer, "footer-no-cpu-reader.png");
        }, cpu: false), CancellationToken.None);
    }

    /// <summary>The TextBlock the closed picker shows its name in.</summary>
    private static Control PickerText(ComboBox picker) =>
        picker.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 } text && text == picker.SelectedItem?.ToString());

    /// <summary>The control's painted vertical centre sits on the strip's, within a pixel.</summary>
    private static void AssertCentred(Visual control, Visual strip, string what)
    {
        var box = PaintedRect(control, strip);
        var middle = strip.Bounds.Height / 2;
        Assert.True(Math.Abs(box.Center.Y - middle) <= 1.0,
            $"{what} is centred at y={box.Center.Y:F1}, the strip at y={middle:F1} ({box})");
    }

    private static void CaptureFooter(Window window, Control footer, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>
    ///     The reserved widths are big enough for the widest value each cell can
    ///     print — a fixed column that TRIMS is a second bug, not a fix. Measures
    ///     the real faces rather than trusting the numbers in the markup comment.
    /// </summary>
    [Fact]
    public async Task TheFooterCells_AreWideEnoughForTheWidestValueTheyCanPrint()
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, _, rig, window) =>
        {
            var note = view.FindControl<TextBlock>("RigTunerNote")!;
            var cents = view.FindControl<TextBlock>("RigTunerCents")!;
            var tuner = rig.RigTuner;
            tuner.HasReading = true;

            // Every note name RigTunerViewModel can print: NoteNameOf(midi) is
            // "<name><octave>", and the sharps over a two-digit octave are the
            // widest of them.
            foreach (var text in new[] { "–", "C0", "F#4", "G#-1", "A#10" })
            {
                tuner.NoteText = text;
                for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
                var needed = new TextBlock
                {
                    Text = text, TextWrapping = TextWrapping.NoWrap,
                    FontFamily = note.FontFamily, FontSize = note.FontSize, FontWeight = note.FontWeight
                };
                needed.Measure(Size.Infinity);
                output.WriteLine($"note '{text}' needs {needed.DesiredSize.Width:F1} in {note.Bounds.Width:F1}");
                Assert.True(needed.DesiredSize.Width <= note.Bounds.Width + 0.5,
                    $"the note cell is {note.Bounds.Width:F1} px and '{text}' needs " +
                    $"{needed.DesiredSize.Width:F1} — it will be clipped");
            }

            // The cents readout is clamped to ±RigTunerMath.MaxDisplayCents and
            // always carries a sign, so "-50 ¢" is its widest face.
            tuner.CentsText = "-50 ¢";
            for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
            output.WriteLine($"cents '-50 ¢' -> {cents.Bounds.Width:F1} in a " +
                             $"{view.FindControl<Panel>("RigTunerReadout")!.Bounds.Width:F1} px cell");
            Assert.True(cents.Bounds.Width > 1, "the cents readout measured to nothing");
            Assert.True(cents.Bounds.Width <= view.FindControl<Panel>("RigTunerReadout")!.Bounds.Width + 0.5,
                "the cents readout does not fit the cell it shares with the status hint");
        }), CancellationToken.None);
    }

    // ══ 2. the editor and the strip ══════════════════════════════════════════

    /// <summary>
    ///     ASSERTS NON-INTERSECTION, deliberately, and that is the finding: the
    ///     module editor (<c>RigDockBody</c>) is inside row 1 of the screen's
    ///     root grid and <c>RigFooter</c> is row 2, so no point is ever inside
    ///     both and there is no hit-test winner to assert. Swept over the whole
    ///     catalogue because the editor's height is per-FAMILY: a family view
    ///     without a root scroller would overflow this Border into row 2, where
    ///     the footer — declared later in the same Grid — paints over it, and
    ///     <c>ClipToBounds</c> on the dock body is what makes that impossible.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1600, 900)]
    public async Task TheModuleEditor_NeverPaintsIntoTheFooter(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, rig, window) =>
        {
            var body = view.FindControl<Border>("RigDockBody")!;
            var footer = view.FindControl<Border>("RigFooter")!;
            Assert.True(body.ClipToBounds,
                "RigDockBody must clip, or a family view with no root scroller overflows into the footer's row");

            for (var i = 0; i < rig.Nodes.Count; i++)
            {
                rig.SelectNodeAt(i);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                Assert.True(model.IsDockBodyVisible, "selecting a unit must open the editor");

                var footerRect = PaintedRect(footer, view);
                var editorRect = PaintedRect(body, view);
                Assert.True(editorRect.Height > 1, $"the editor for {rig.Nodes[i].DisplayName} did not lay out");
                Assert.True(editorRect.Intersect(footerRect).Height <= 0.5,
                    $"the editor for {rig.Nodes[i].DisplayName} paints at {editorRect} inside the footer's " +
                    $"{footerRect} — it is drawn into the strip");

                var into = body.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsVisible)
                    .Select(c => (c, r: PaintedRect(c, view)))
                    .Where(t => t.r is { Width: > 0, Height: > 0 } && t.r.Bottom > footerRect.Top + 0.5)
                    .ToList();
                Assert.True(into.Count == 0,
                    $"{into.Count} editor visuals reach into the footer, first " +
                    $"{(into.Count > 0 ? into[0].c.GetType().Name + " at " + into[0].r : "-")}");
            }

            output.WriteLine($"{width}x{height}: {rig.Nodes.Count} editors, none reaching " +
                             $"{PaintedRect(footer, view).Top:F0}");
        }, gear: ["overdrive", "namamp", "amp", "cab", "reverb", "delay", "compressor", "graphiceq", "wah"],
            width: width, height: height), CancellationToken.None);
    }

    /// <summary>
    ///     WHY the owner read the cut as "behind the footer": the editor is
    ///     routinely taller than the dock row, and Avalonia auto-hides a
    ///     scrollbar, so the last row of knobs was sliced at the strip's top edge
    ///     with nothing saying the content continued. Every family view's root
    ///     scroller is <c>RigEditorScroll</c> and rests VISIBLE now — it has to be
    ///     set per view, because <c>ScrollViewer.AllowAutoHide</c> does not
    ///     inherit down the tree.
    ///     <para>
    ///         <b>The anti-vacuity counter reads the RACK, not the face, since
    ///         lane E (2026-09-13).</b> Every family FACE is scaled into the dock
    ///         row by <c>Controls/Fire/RigScaleToFit</c> now, so none of them is
    ///         taller than its viewport and counting those would assert zero for
    ///         ever. <c>RigEditorRackScroll</c> — the shared side rack,
    ///         854 px of panels, deliberately NOT scaled because shrinking a
    ///         column of combos and toggles past reading size is worse than the
    ///         scrollbar — still overflows, which is what keeps the rest-visible
    ///         property a thing this case can exercise. Both scrollers' auto-hide
    ///         is still asserted for every module.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheModuleEditor_KeepsItsScrollbarUp_WhenItIsTallerThanTheDock()
    {
        // The FOLD half is about the family faces, which a module without a
        // faceplate opens; one with a face is edited on the board and its dock is
        // the inspector (RigDockUnit.EditorFamilyFor), which scrolls by design.
        DotCharter.Tone.UI.Controls.Dsp.RigFaceplates.SuppressedForTests = true;
        try
        {
            await HeadlessSessionFixture.Dispatch(() => WithRig((view, _, rig, window) =>
            {
                var body = view.FindControl<Border>("RigDockBody")!;
                var taller = 0;
                var foldedFaces = 0;
                var scrolledFaces = 0;

                for (var i = 0; i < rig.Nodes.Count; i++)
                {
                    rig.SelectNodeAt(i);
                    for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                    // the controller / MIDI panels start folded; open them, which is
                    // what makes a rack taller than the dock
                    if (view.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Name == "RigEditorControllers") is { IsExpanded: false } folded)
                    {
                        folded.IsExpanded = true;
                        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                    }

                    // Named, because a family view whose root scroller is anonymous is
                    // a family view nothing here can check — five of the nine were,
                    // and all five were the ones still on auto-hide. RigModuleEditorView
                    // keeps every family it has built ATTACHED and hidden, so there is
                    // more than one of them in the tree; the live one is the one that
                    // is effectively visible.
                    var named = body.GetVisualDescendants().OfType<ScrollViewer>()
                        .Where(s => s.Name is "RigEditorScroll" or "RigEditorRackScroll").ToList();
                    Assert.NotEmpty(named);
                    foreach (var s in named)
                    {
                        Assert.False(ScrollViewer.GetAllowAutoHide(s),
                            $"'{s.Name}' for {rig.Nodes[i].DisplayName} still auto-hides — its cut edge " +
                            "reads as content drawn under the footer");
                    }

                    var scroller = Assert.Single(named,
                        s => s.Name == "RigEditorScroll" && s.IsEffectivelyVisible);
                    // A face folds into the dock row when it CAN. Below
                    // `RigArtwork.MinEditorScale` it stops shrinking and scrolls
                    // instead, by design — that floor exists so labels never go
                    // past reading size — and the Graphite type ladder pushed the
                    // densest face (NAM) over that line. What must hold either way
                    // is the pair above: the scrollbar does not auto-hide, so a cut
                    // edge reads as scrollable rather than as content drawn under
                    // the footer, which is the fault the owner reported.
                    if (scroller.Extent.Height > scroller.Viewport.Height + 0.5)
                    {
                        scrolledFaces++;
                        output.WriteLine($"{rig.Nodes[i].DisplayName}: FACE extent {scroller.Extent.Height:F0} " +
                                         $"in a {scroller.Viewport.Height:F0} px viewport — at the readability floor");
                    }
                    else
                    {
                        foldedFaces++;
                    }

                    var rack = Assert.Single(named,
                        s => s.Name == "RigEditorRackScroll" && s.IsEffectivelyVisible);
                    if (rack.Extent.Height > rack.Viewport.Height + 0.5)
                    {
                        taller++;
                        output.WriteLine($"{rig.Nodes[i].DisplayName}: rack extent {rack.Extent.Height:F0} " +
                                         $"in a {rack.Viewport.Height:F0} px viewport (face " +
                                         $"{scroller.Extent.Height:F0}/{scroller.Viewport.Height:F0})");
                    }
                }

                Assert.True(foldedFaces > 0,
                    $"not one face folded into the dock row ({scrolledFaces} scrolled) — scale-to-fit "
                    + "is doing nothing at all, which is a different fault from a dense face reaching "
                    + "the readability floor");

                Assert.True(taller > 0,
                    "no editor RACK was taller than the dock — this case is not exercising the cut it "
                    + "exists for. The faces are scaled to fit since lane E; the rack is not, and if it "
                    + "ever is, this counter has to move again rather than be deleted.");
            }, gear: ["overdrive", "namamp", "amp", "cab", "reverb"], height: 900), CancellationToken.None);
        }
        finally
        {
            DotCharter.Tone.UI.Controls.Dsp.RigFaceplates.SuppressedForTests = false;
        }
    }
}
