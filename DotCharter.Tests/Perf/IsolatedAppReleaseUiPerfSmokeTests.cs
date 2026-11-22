using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.UI;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
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

    /// <summary>
    ///     The NEXT app's start raises the process-wide <c>LocalizationService.PropertyChanged</c>
    ///     (<c>LoadCommunityLanguages</c>), and the released app's <see cref="InspectorIssuesViewModel" />
    ///     still hears it through its weak language subscription while it is not yet collected. With
    ///     its host gone (the container disposed the inspector) it rebuilds its severity groups, so the
    ///     released, hidden <see cref="IssuesInspectorView" /> sees a list move and asks its
    ///     <see cref="HiddenSurfaceRewarmer" /> for a step. A detached surface must schedule nothing:
    ///     a queued step holds the view, its window and the whole app on the dispatcher.
    ///     <para>
    ///         Built here on purpose: the Issues tab realized with findings and then hidden, and the
    ///         view model held until the next app has started (a full collection between the two
    ///         dispatches would hide the path).
    ///     </para>
    /// </summary>
    [Fact]
    public async Task AReleasedApp_WhoseHiddenIssuesTabHearsTheNextAppsLanguageLoad_IsCollectable()
    {
        var ct = TestContext.Current.CancellationToken;
        WeakReference? app = null, window = null, issuesView = null, issuesVm = null;
        InspectorIssuesViewModel? heldUntilNextApp = null;

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app has no services");
            var shell = services.GetRequiredService<MainWindowViewModel>();
            shell.SelectedMainTabIndex = 0;
            shell.IsInspectorVisible = true;
            NarrowWidthLayout.SeedSong(services.GetRequiredService<ProjectState>());
            var win = new MainWindow { DataContext = shell };
            win.Show();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

            var inspector = shell.Inspector;
            inspector.SelectedTabIndex = InspectorViewModel.IssuesTabIndex;
            NarrowWidthLayout.Settle(win);
            HeadlessIssuePass.Settle(inspector.Issues);
            ArrangementIssue[] findings =
            [
                new(ArrangementIssueType.BendStrengthTooHigh, "bend", 4000),
                new(ArrangementIssueType.BendStrengthTooHigh, "bend", 9000)
            ];
            inspector.Issues.Items.ResetWith(findings);
            inspector.Issues.IssueGroups.ResetWith(IssueGrouping.Build(findings));
            NarrowWidthLayout.Settle(win);
            var view = win.GetVisualDescendants().OfType<IssuesInspectorView>().Single();
            Assert.True(view.GetVisualDescendants().OfType<Button>().Count(b => b.Name == "IssueRow") > 0, // virtualized: a card paged away realizes one row
                "the Issues tab realized no finding rows, so nothing of it could move after the release");

            // Hidden again: a rewarmer only schedules for a hidden surface.
            inspector.SelectedTabIndex = 0;
            NarrowWidthLayout.Settle(win);
            Assert.False(view.IsVisible, "the Issues tab is still showing, so its rewarmer would never schedule");

            app = new WeakReference(Application.Current);
            window = new WeakReference(win);
            issuesView = new WeakReference(view);
            issuesVm = new WeakReference(inspector.Issues);
            heldUntilNextApp = inspector.Issues;
        }, ct);

        await HeadlessSessionFixture.Dispatch(() =>
        {
            // This dispatch's app has started, so the language load has reached the old view model.
            heldUntilNextApp = null;
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(issuesView!.IsAlive,
                "the released app's IssuesInspectorView is still reachable — a rewarm step queued for a detached surface holds it.");
            Assert.False(window!.IsAlive, "the released MainWindow is still reachable.");
            Assert.False(issuesVm!.IsAlive, "the released app's InspectorIssuesViewModel is still reachable.");
            Assert.False(app!.IsAlive, "the released App is still reachable.");
        }, ct);
    }
}
