using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.UI;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     A dispatch that booted a whole <see cref="MainWindow" /> must leave NOTHING
///     of it reachable once <see cref="HeadlessSessionFixture.Dispatch(Action, CancellationToken)" />
///     has released it.
///     <para>
///         Measured 2026-10-04 with a probe and a heap dump: every such dispatch
///         left ~250 MB of live heap behind (303 → 555 → 804 → 1,072 → 1,338 MB
///         across <c>EscapeKeyRoutingUiPerfSmokeTests</c>' five cases). The root
///         was the PROCESS-WIDE <c>LocalizationService.Instance.PropertyChanged</c>:
///         the shell's Settings <see cref="ScreenHost" /> owns a DI scope whose
///         <c>SettingsShellViewModel</c> / <c>KeybindsSettingsViewModel</c> subscribe
///         to it and unsubscribe only in <c>Dispose</c>, and nothing disposed the
///         host — so each released app (window, container, every singleton)
///         stayed alive for the rest of the run. <c>MainWindow.ReleaseForIsolatedTeardown</c>
///         now disposes the window's screen hosts.
///     </para>
///     <para>
///         The assertion is reachability, not a byte count: weak references to
///         the app, the window and the settings host must all be dead after a
///         full collection in the NEXT dispatch (which boots its own app, so
///         Avalonia's last-app statics no longer point at the old one).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class IsolatedAppReleaseUiPerfSmokeTests
{
    [Fact]
    public async Task AReleasedMainWindowApp_IsCollectable_SettingsHostAndAll()
    {
        var ct = TestContext.Current.CancellationToken;
        WeakReference? app = null, window = null, host = null, shell = null;

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app has no services");
            var win = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            win.Show();
            win.UpdateLayout();

            // Open Settings so the shell's settings ScreenHost (and its scope) exists.
            services.GetRequiredService<SettingsSession>().IsOpen = true;
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            win.UpdateLayout();
            var settings = win.FindControl<ContentControl>("SettingsHost")?.Content as ScreenHost;
            Assert.NotNull(settings); // otherwise this test would prove nothing about the host
            var shellVm = settings!.ViewOf(typeof(ISettingsShellScreen))?.DataContext;
            Assert.NotNull(shellVm);

            app = new WeakReference(Application.Current);
            window = new WeakReference(win);
            host = new WeakReference(settings);
            shell = new WeakReference(shellVm);
        }, ct);

        await HeadlessSessionFixture.Dispatch(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(shell!.IsAlive,
                "the released app's SettingsShellViewModel is still reachable — its scope was never disposed " +
                "(MainWindow.ReleaseForIsolatedTeardown -> DisposeScreenHosts), so LocalizationService pins it.");
            Assert.False(host!.IsAlive, "the released window's settings ScreenHost is still reachable.");
            Assert.False(window!.IsAlive, "the released MainWindow is still reachable.");
            Assert.False(app!.IsAlive, "the released App is still reachable — every dispatch that builds a MainWindow leaks it.");
        }, ct);
    }
}
