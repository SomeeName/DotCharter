using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The arrangement dialog's A440 check row (#821), built the way the app builds it: the measured
///     value beside the authored one, Measure, and the Use button while a correction is offered, each
///     laid out with a size.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ArrangementReferencePitchUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 1300;

    private static readonly string[] RowParts = ["ReferencePitchRow", "ReferencePitchStatus", "ReferencePitchMeasure", "ReferencePitchApply"];

    [Fact]
    public async Task TheRow_ShowsTheMeasurement_AndBothVerbs()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeArrangementSettingsScreen.Default();
            WithOpenDialog(screen, view =>
            {
                var controls = view.GetVisualDescendants().OfType<Control>().ToList();
                foreach (var name in RowParts)
                {
                    var control = controls.Single(c => c.Name == name);
                    Assert.True(control.IsEffectivelyVisible, $"{name} is hidden");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out {control.Bounds}");
                }

                var status = (TextBlock)controls.Single(c => c.Name == "ReferencePitchStatus");
                Assert.Equal(screen.ReferencePitch.StatusText, status.Text);
                var apply = (Button)controls.Single(c => c.Name == "ReferencePitchApply");
                Assert.Equal(screen.ReferencePitch.ApplyLabel, apply.Content);
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>Opt-in review capture: <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> plus a folder in
    ///     <c>DOTCHARTER_REFERENCE_PITCH_CAPTURE_DIR</c>; taken before the assertions, so a run without the row captures too.</summary>
    private static void Capture(Window window)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        var directory = Environment.GetEnvironmentVariable("DOTCHARTER_REFERENCE_PITCH_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var name = Environment.GetEnvironmentVariable("DOTCHARTER_REFERENCE_PITCH_CAPTURE_NAME") ?? "arrangement-settings";
        frame.Save(Path.Combine(directory, $"{name}-{HeadlessSessionFixture.SweptCulture}.png"), new PngBitmapEncoderOptions());
    }

    private static void WithOpenDialog(IArrangementSettingsScreen screen, Action<Control> assert)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped(_ => screen);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
            provider.GetRequiredService<DialogsSlot>());
        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(host);
        var window = new Window { Width = HostWidth, Height = HostHeight, Content = container };
        window.Show();
        try
        {
            _ = ((IDialogs)host).ShowAsync<IArrangementSettingsScreen, bool>();
            Dispatcher.UIThread.RunJobs();
            container.Measure(new Size(HostWidth, HostHeight));
            container.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            var view = Assert.Single(host.OpenViews);
            Capture(window);
            assert(view);
            host.CloseTop();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }
}
