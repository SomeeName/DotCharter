using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Perf-autopilot probe (<c>DOTCHARTER_AUTOPILOT_POPOVERS=1</c>): opens
///     every popover on the editor toolbar several times in the REAL window and logs,
///     per open, three intervals from the call: the synchronous <c>ShowAt</c>, the
///     second animation frame after it (the frame that laid out and rendered the
///     popover has been committed by then) and the first <c>Background</c> tick (the
///     UI thread is free again). The app's own <c>Popover … opened in</c> line stops
///     at <c>Opened</c>, which with overlay popups is BEFORE the popover's layout and
///     render, so it cannot see the lag the owner reported.
/// </summary>
internal static class PopoverLatencyProbe
{
    private const int OpensPerPopover = 5;

    public static async Task RunAsync(Control toolbar, string phase, IAppLogger? logger)
    {
        var topLevel = TopLevel.GetTopLevel(toolbar);
        if (topLevel is null)
        {
            logger?.Info(AppLogCategory.Rendering, "[PopoverProbe] the toolbar is not in a window.");
            return;
        }

        var popovers = MenuFlyoutPrewarmer.FlyoutsUnder(toolbar);
        logger?.Info(AppLogCategory.Rendering, $"[PopoverProbe] {phase}: {popovers.Count} popovers.");
        foreach (var popover in popovers)
        {
            var name = popover.Target.Name ?? popover.Flyout.Content?.GetType().Name ?? "?";
            var samples = new List<(double Sync, double Frame, double Idle)>();
            for (var i = 0; i < OpensPerPopover; i++)
            {
                var start = Stopwatch.GetTimestamp();
                popover.Flyout.ShowAt(popover.Target);
                var sync = PopoverOpenTiming.MsSince(start);
                await NextFrameAsync(topLevel);
                await NextFrameAsync(topLevel);
                var frame = PopoverOpenTiming.MsSince(start);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var idle = PopoverOpenTiming.MsSince(start);
                samples.Add((sync, frame, idle));

                await Task.Delay(250);
                popover.Flyout.Hide();
                await Task.Delay(250);
            }

            var opens = string.Join(" | ", samples.Select(s => string.Create(CultureInfo.InvariantCulture,
                $"sync {s.Sync:F1} frame {s.Frame:F1} idle {s.Idle:F1}")));
            logger?.Info(AppLogCategory.Rendering, $"[PopoverProbe] {phase} {name}: {opens} ms");
        }
    }

    private static Task NextFrameAsync(TopLevel topLevel)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        topLevel.RequestAnimationFrame(_ => done.TrySetResult());
        return done.Task;
    }
}
