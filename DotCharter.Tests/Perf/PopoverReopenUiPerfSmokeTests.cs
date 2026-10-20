using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #176: a toolbar popover's REPEAT open. On the real window (perf autopilot,
///     <c>DOTCHARTER_AUTOPILOT_POPOVERS=1</c>, 2026-10-05) the synchronous
///     <c>ShowAt</c> of an already-opened popover took 25-90 ms while the app's own
///     Opening → Opened log line read 2-5 ms: Avalonia's close had taken the popup
///     out of the logical tree, and the next <c>ShowAt</c> put it back — re-styling
///     the whole popover — BEFORE Opening. <c>PrewarmableFlyout.OnClosed</c> now
///     re-parents and re-realizes the closed popover at <c>Background</c>.
/// </summary>
/// <remarks>
///     Measured here (headless, Debug, <c>r2r=no</c>), LOGGED, never asserted:
///     repeat opens 140-215 ms before the fix, 4-7 ms after. What is asserted is the
///     state the fix leaves behind: between opens the popup is back under its target.
/// </remarks>
[Collection("HeadlessUI")]
public sealed class PopoverReopenUiPerfSmokeTests
{
    private static readonly Size Slot = new(1600, 1000);

    [Fact]
    public Task Every_repeat_open_finds_the_popup_still_under_its_target() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var toolbar = new ToolbarView();
            var window = new Window { Width = Slot.Width, Height = Slot.Height, Content = toolbar };
            window.Show();
            try
            {
                toolbar.Measure(Slot);
                toolbar.Arrange(new Rect(Slot));
                var popovers = MenuFlyoutPrewarmer.FlyoutsUnder(toolbar);
                Assert.NotEmpty(popovers);

                var report = new List<string>();
                foreach (var p in popovers)
                {
                    MenuFlyoutPrewarmer.Realize(p, Slot);
                    var times = new List<double>();
                    for (var i = 0; i < 4; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        p.Flyout.ShowAt(p.Target);
                        times.Add(sw.Elapsed.TotalMilliseconds);
                        Assert.True(p.Flyout.IsOpen, $"{p.Target.Name} did not open");
                        p.Flyout.Hide();
                        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

                        Assert.True(ReferenceEquals(p.Target, ((ILogical)p.Flyout.Popup!).LogicalParent),
                            $"{p.Target.Name}: after a close the popup is not back under its target, so the " +
                            "next open re-styles the whole popover inside the click (#176)");
                    }

                    report.Add($"{p.Target.Name}: {string.Join(" / ", times.Select(t => t.ToString("0.0")))} ms");
                }

                foreach (var line in report) Debug.WriteLine($"[popover-reopen] {line}");
                TestContext.Current.SendDiagnosticMessage(string.Join("\n", report));
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
}
