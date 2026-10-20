using Avalonia.Threading;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The whole chart-editor shell as a picture at the window sizes #130 and #134 were reported
///     at — 1280x800 (a 1920 laptop at 150 %) and 1920x1080 — with a long warning on the status
///     rail and a running task, over the UI sweep's seeded song. The before/after evidence for
///     the narrow-width fixes; the geometry is asserted by
///     <see cref="StatusRailNarrowWidthUiPerfSmokeTests" /> and
///     <see cref="InspectorNarrowWidthUiPerfSmokeTests" />, on the views alone.
///     <para>
///         A CAPTURE harness: it does its work only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (Skia
///         drawing, set before the session starts), so the ordinary headless run does not pay
///         for a <c>MainWindow</c> per case. PNGs go to <see cref="NarrowWidthLayout.ArtifactDirectory" />,
///         suffixed with <c>DOTCHARTER_HEADLESS_CULTURE</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class NarrowWidthShellCaptureUiPerfSmokeTests
{
    /// <summary>One <c>MainWindow</c>, resized between shots: a second shell window in the same
    /// process never started (the first one's teardown ends the run), so the sizes share one.</summary>
    [Fact]
    public async Task TheEditorShell_IsCaptured_AtBothSizes()
    {
        if (!UiSweepRunner.CaptureEnabled) return;

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var state = services.GetRequiredService<ProjectState>();
            var statusBar = services.GetRequiredService<IStatusBarService>();

            foreach (var dialog in vm.Dialogs.AllDialogs)
            {
                if (dialog.IsOpen)
                    dialog.RequestClose();
            }

            vm.CommandPalette.Close();
            vm.IsGameplayPresentation = false;
            vm.SelectedMainTabIndex = 0;
            NarrowWidthLayout.SeedSong(state);

            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                foreach (var (width, height) in new[] { (1920, 1080), (1280, 800) })
                {
                    window.Width = width;
                    window.Height = height;
                    statusBar.CompleteTask("narrow-width-capture");
                    state.SetMessage(
                        "3D preview unavailable: the compositor has no GPU interop on this device, so the highway panel stays blank until restart.",
                        StatusSeverity.Warning);
                    NarrowWidthLayout.Settle(window);
                    NarrowWidthLayout.Capture(window, $"shell-{width}x{height}");

                    statusBar.BeginTask("narrow-width-capture",
                        "Separating stems: The Extraordinarily Long Band Name Appreciation Society Orchestra – Symphony No. 4 in D Minor",
                        indeterminate: false, cancel: () => { });
                    NarrowWidthLayout.Settle(window);
                    NarrowWidthLayout.Capture(window, $"shell-task-{width}x{height}");
                    Assert.True(window.Bounds.Width > 1 && window.Bounds.Height > 1, $"the shell laid out as {window.Bounds}");
                }
            }
            finally
            {
                statusBar.CompleteTask("narrow-width-capture");
                window.Close();
            }
        }, CancellationToken.None);
    }
}
