using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The editor toolbar's play-the-chart toggle (#574), built against the app's OWN
///     shell view model: it shows the state the Mixer's "Play the chart" checkbox
///     writes (<c>Transport.MidiEnabled</c>), and in the headless session — where no
///     audio engine is open — it is DISABLED and its tooltip says why, with the live
///     keybind in it. The command half (the toolbar flipping the state) is the default
///     gate's <c>UI/ChartSynthToolbarToggleTests</c>.
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_TOOLBAR_CAPTURE_DIR</c>
///         set it also writes the toolbar as PNGs: without the toggle (what shipped before
///         #574), with it as the headless session really shows it (disabled), and lit.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChartSynthToolbarToggleUiPerfSmokeTests
{
    private static readonly Size Slot = new(1600, 80);

    [Fact]
    public Task TheToggle_MirrorsTheOneSwitch_AndIsDisabledWithItsReason() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = (App.Services ?? throw new InvalidOperationException("the headless app session did not boot"))
                .GetRequiredService<MainWindowViewModel>();
            var transport = shell.Transport;
            var wasOn = transport.MidiEnabled;

            var view = new ToolbarView { DataContext = shell };
            var window = new Window { Width = Slot.Width, Height = Slot.Height, Content = view };
            window.Show();
            try
            {
                view.Measure(Slot);
                view.Arrange(new Rect(Slot));

                var name = LocalizationService.Instance["Toolbar_ToggleChartSynth"];
                var toggle = view.GetVisualDescendants().OfType<ToggleButton>()
                    .Single(b => AutomationProperties.GetName(b) == name);
                Assert.True(toggle.Bounds is { Width: > 1, Height: > 1 }, $"the toggle laid out as {toggle.Bounds}");

                // The headless session opens no audio engine: nothing could sound.
                Assert.False(transport.CanPlayChart, "the headless session has an engine up — this case lost its premise");
                Assert.False(toggle.IsEffectivelyEnabled, "the toggle looks live while the chart cannot sound");
                var chord = shell.ProjectState.Keybinds.ToggleChartSynth;
                var reason = string.Format(CultureInfo.CurrentCulture,
                    LocalizationService.Instance["Toolbar_ToggleChartSynth_Unavailable"], chord);
                Assert.Equal(reason, ToolTip.GetTip(toggle));
                Assert.Contains(chord.ToString(), reason, StringComparison.Ordinal);

                Capture(window, "toolbar-574-after-disabled.png");

                // The Mixer's checkbox writes the property; the toolbar shows it.
                transport.MidiEnabled = true;
                Dispatch();
                Assert.True(toggle.IsChecked, "the toolbar did not light when the switch went on");
                transport.MidiEnabled = false;
                Dispatch();
                Assert.False(toggle.IsChecked, "the toolbar stayed lit after the switch went off");

                if (CaptureDir() is not null)
                {
                    // The capture's "on" frame: the same button, its availability lifted by hand
                    // (the headless session has no engine to do it). The theme's 0.15 s
                    // brush transitions never advance on the headless clock, so they go
                    // first or the frame shows the fade's first step, not the lit state.
                    toggle.Transitions = null;
                    toggle.Command = null;
                    toggle.IsEnabled = true;
                    toggle.IsChecked = true;
                    Dispatch();
                    Capture(window, "toolbar-574-after-on.png");
                    toggle.IsVisible = false;
                    Dispatch();
                    Capture(window, "toolbar-574-before.png");
                }
            }
            finally
            {
                transport.MidiEnabled = wasOn;
                window.Close();
            }
        }, CancellationToken.None);

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static string? CaptureDir()
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_TOOLBAR_CAPTURE_DIR");
        return UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir) ? dir : null;
    }

    private static void Capture(Window window, string file)
    {
        if (CaptureDir() is not { } dir) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
