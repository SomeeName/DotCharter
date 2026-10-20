using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Onboarding;
using DotCharter.Presentation.Fakes.Onboarding;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Infrastructure.Graphics;
using DotCharter.Tone.UI.Platform;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Onboarding;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The first-run wizard's check of this computer (#288), built for real and laid out:
///     every fake state on its own, and the real view model on the wizard's first page
///     inside the production dialog host. A capability that cannot run must LOOK off —
///     its name dimmed, its tag the danger tag, its reason measured — and every row must
///     stay inside the panel. That last part is width-shaped, so this class is in the
///     release's de-DE / ja-JP layout filter.
/// </summary>
[Collection("HeadlessUI")]
public sealed class OnboardingSystemCheckUiPerfSmokeTests
{
    /// <summary>Narrower than the wizard's card body, so a reason that does not wrap overflows here first.</summary>
    private const double PanelWidth = 480;

    public static TheoryData<string> Variants => [.. FakeSystemCheckScreen.Variants.Select(v => v.Name)];

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task EveryVariant_BuildsAndLaysOut_AndEveryRowFitsThePanel(string variant)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeSystemCheckScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new SystemCheckView { DataContext = screen, Width = PanelWidth };
            var window = new Window { Width = 1280, Height = 900, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                AssertPanel(view, screen, variant);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    public static TheoryData<string> WizardStates => ["AllReady", "EngineMissing", "AbiMismatch", "NoVulkan", "BothOff"];

    /// <summary>
    ///     The product order, once per state the first page can show: the wizard is on screen
    ///     BEFORE the check answers, so the rows must change in place. The check reports through
    ///     the real <see cref="AvaloniaUiDispatcher" />, which is how the app marshals it — an
    ///     inline one would raise the change on the pool, where Avalonia's bindings never hear it.
    ///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set it also
    ///     writes the page to PNG for review.
    /// </summary>
    [Theory]
    [MemberData(nameof(WizardStates))]
    public async Task OnTheWizardsFirstPage_EachStateShows_DisabledWithItsReasonOrQuietlyReady(string state)
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var (engine, gpu) = state switch
            {
                "EngineMissing" => (new NativeEngineCheck(NativeEngineState.Missing), new GpuRenderCheck(GpuRenderState.Ready)),
                "AbiMismatch" => (new NativeEngineCheck(NativeEngineState.AbiMismatch, 42, 41), new GpuRenderCheck(GpuRenderState.Ready)),
                "NoVulkan" => (new NativeEngineCheck(NativeEngineState.Ready), new GpuRenderCheck(GpuRenderState.NoVulkan)),
                "BothOff" => (new NativeEngineCheck(NativeEngineState.AbiMismatch, 42, 41), new GpuRenderCheck(GpuRenderState.NoInteropDevice)),
                _ => (new NativeEngineCheck(NativeEngineState.Ready), new GpuRenderCheck(GpuRenderState.Ready))
            };
            var rig = new OnboardingWizardRig(engineProbe: new FakeNativeEngineProbe(engine),
                gpuProbe: new FakeGpuRenderProbe(gpu), systemCheckUi: new AvaloniaUiDispatcher());
            var wizard = new OnboardingWizardView { DataContext = rig.Vm };
            var host = new DialogContainerView();
            host.DialogPanel.Children.Clear();
            host.DialogPanel.Children.Add(wizard);
            var window = new Window { Width = 1280, Height = 900, Content = host, Background = AppBackground() };
            window.Show();
            try
            {
                rig.Vm.Open();
                var run = rig.SystemCheck.Run;
                // The answer is posted to THIS thread, so it cannot have landed yet.
                Assert.True(rig.SystemCheck.IsBusy, "the page must open on the checking state");
                await run.WaitAsync(TimeSpan.FromSeconds(60));

                host.Measure(new Size(1280, 900));
                host.Arrange(new Rect(0, 0, 1280, 900));
                wizard.UpdateLayout();

                var panel = wizard.GetLogicalDescendants().OfType<SystemCheckView>().Single(v => v.Name == "OnboardingSystemCheck");
                Assert.True(panel.IsEffectivelyVisible, "the check must show on the Welcome page");
                Assert.Same(rig.SystemCheck, panel.DataContext);
                Assert.False(rig.SystemCheck.IsBusy);
                Assert.Equal(state != "AllReady", rig.SystemCheck.HasProblem);
                Assert.Equal(engine.State != NativeEngineState.Ready, rig.SystemCheck.Capabilities[0].IsUnavailable);
                Assert.Equal(gpu.State != GpuRenderState.Ready, rig.SystemCheck.Capabilities[1].IsUnavailable);
                AssertPanel(panel, rig.SystemCheck, state);
                Capture(window, $"onboarding-system-check-{state}");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static IBrush AppBackground()
    {
        var app = Application.Current ?? throw new InvalidOperationException("no Application — the headless session did not boot");
        Assert.True(app.TryFindResource("AppBackground", app.ActualThemeVariant, out var brush) && brush is IBrush,
            "AppBackground did not resolve");
        return (IBrush)brush!;
    }

    private static void Capture(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"{name}-{HeadlessSessionFixture.SweptCulture}.png"), new PngBitmapEncoderOptions());
    }

    private static void AssertPanel(Control view, ISystemCheckScreen screen, string label)
    {
        var panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SystemCheckPanel");
        Assert.True(panel.Bounds is { Width: > 1, Height: > 1 }, $"{label}: the panel laid out as {panel.Bounds}");
        Assert.True(screen.HasProblem == panel.Classes.Contains("Problem"),
            $"{label}: HasProblem={screen.HasProblem} but the panel's classes are [{string.Join(" ", panel.Classes)}] (DataContext {panel.DataContext?.GetType().Name}, busy {screen.IsBusy})");

        var rows = view.GetVisualDescendants().OfType<ContentPresenter>()
            .Where(p => p.DataContext is ISystemCapability && p.Child is Grid).ToList();
        Assert.Equal(screen.Capabilities.Count, rows.Count);
        foreach (var row in rows)
        {
            var capability = (ISystemCapability)row.DataContext!;
            var grid = (Grid)row.Child!;
            Assert.True(grid.Bounds is { Width: > 1, Height: > 1 }, $"{label}: the {capability.Name} row laid out as {grid.Bounds}");

            var tag = grid.Children.OfType<Border>().Single();
            Assert.True(tag.Bounds is { Width: > 1, Height: > 1 }, $"{label}: the {capability.Name} tag measured nothing");
            Assert.Equal(capability.IsUnavailable, tag.Classes.Contains("Danger"));
            Assert.Equal(capability.IsAvailable, tag.Classes.Contains("Success"));
            var tagRight = tag.TranslatePoint(new Point(tag.Bounds.Width, 0), panel)!.Value.X;
            Assert.True(tagRight <= panel.Bounds.Width + 0.5,
                $"{label}: the {capability.Name} tag ends at {tagRight:0.#} in a {panel.Bounds.Width:0.#} px panel");

            var texts = grid.GetVisualDescendants().OfType<TextBlock>().ToList();
            var name = texts.Single(t => t.Classes.Contains("SystemCheckName"));
            Assert.Equal(capability.IsUnavailable, name.Classes.Contains("Off"));
            var reason = texts.SingleOrDefault(t => t.IsEffectivelyVisible && t.Text == capability.Reason && capability.HasReason);
            if (!capability.HasReason) continue;
            Assert.NotNull(reason);
            Assert.True(reason.Bounds is { Width: > 1, Height: > 1 }, $"{label}: the {capability.Name} reason measured {reason.Bounds}");
            var reasonRight = reason.TranslatePoint(new Point(reason.Bounds.Width, 0), panel)!.Value.X;
            Assert.True(reasonRight <= tag.TranslatePoint(default, panel)!.Value.X + 0.5,
                $"{label}: the {capability.Name} reason runs under its tag (ends at {reasonRight:0.#})");
        }
    }
}
