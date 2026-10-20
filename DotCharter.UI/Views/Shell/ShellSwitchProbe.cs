using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.Presentation.Contracts.Shell;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Perf-autopilot probe (<c>DOTCHARTER_AUTOPILOT_SWITCHES=1</c>): walks the
///     shell through every top-level switch a user makes — Home, Chart, Library, Play,
///     Settings, the rig — on the REAL window, several rounds, and logs per switch the
///     time from the call to the second animation frame after it (the frame that laid
///     out and drew the new surface has been committed by then) and to the first
///     <c>Background</c> tick (the UI thread is free again). The calls are the ones the
///     activity rail and Home's buttons make (<see cref="MainWindowViewModel.Go" />,
///     <c>ToneRig.IsPedalboardOpen</c>), so a measured switch is a clicked one.
/// </summary>
internal static class ShellSwitchProbe
{
    private const int Rounds = 3;

    private static readonly (string Name, Action<MainWindowViewModel> Do)[] Steps =
    [
        ("Chart->Home", vm => vm.Go(ShellDestination.Home)),
        ("Home->Chart", vm => vm.Go(ShellDestination.Chart)),
        ("Chart->Library", vm => vm.Go(ShellDestination.Library)),
        ("Library->Play", vm => vm.Go(ShellDestination.Play)),
        ("Play->Home", vm => vm.Go(ShellDestination.Home)),
        ("Home->Library", vm => vm.Go(ShellDestination.Library)),
        ("Library->Settings", vm => vm.Go(ShellDestination.Settings)),
        ("Settings->Chart", vm => vm.Go(ShellDestination.Chart)),
        ("rig open", vm => vm.ToneRig.IsPedalboardOpen = true),
        ("rig close", vm => vm.ToneRig.IsPedalboardOpen = false),
        ("Chart->Play", vm => vm.Go(ShellDestination.Play)),
        ("Play->Chart", vm => vm.Go(ShellDestination.Chart)),
    ];

    /// <summary>
    ///     Not <c>early</c>: wait for the startup prewarm's last drain first, so a first entry is
    ///     the switch's own cost. <c>early</c> (<c>DOTCHARTER_AUTOPILOT_SWITCHES=early</c>): start
    ///     at once and note each step as input the way a click does, i.e. a user clicking while
    ///     the drain is still running.
    /// </summary>
    public static async Task RunAsync(Window window, MainWindowViewModel vm, IAppLogger? logger, bool early = false)
    {
        var topLevel = TopLevel.GetTopLevel(window);
        if (topLevel is null) return;

        // A first entry timed while the startup drain is still realizing surfaces measures the
        // collision, not the switch: wait for the last drain, bounded, then let it settle.
        if (!early)
        {
            if (window is MainWindow shell)
                await Task.WhenAny(shell.HiddenSurfacesDrained, Task.Delay(TimeSpan.FromSeconds(30)));
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        logger?.Info(AppLogCategory.Rendering, $"[SwitchProbe] start ({(early ? "early: clicks during the drain" : "after the drain")}).");

        var samples = Steps.ToDictionary(s => s.Name, _ => new List<Sample>());
        long due = 0;
        for (var round = 0; round < Rounds; round++)
        {
            foreach (var (name, step) in Steps)
            {
                if (early) PrewarmPacer.NoteInput();
                var gcBefore = GC.GetTotalPauseDuration();
                var start = Stopwatch.GetTimestamp();
                // How late the "click" itself ran: whatever held the UI thread when it was due (a
                // drain step, say) is latency the user feels and the frame time below cannot see.
                var queued = due == 0 ? 0 : Math.Max(0, (start - due) * 1000.0 / Stopwatch.Frequency);
                step(vm);
                var call = PopoverOpenTiming.MsSince(start);
                // The layout pass the next render tick would run anyway, run here so its share is
                // split from the call's: call = the switch's own handlers, layout = measure/arrange
                // of what it showed, frame - layout - call = render, idle - frame = posted work.
                window.UpdateLayout();
                var layout = PopoverOpenTiming.MsSince(start) - call;
                await NextFrameAsync(topLevel);
                await NextFrameAsync(topLevel);
                var frame = PopoverOpenTiming.MsSince(start);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                // GC pause inside the step: a collection that lands on a switch is not the switch's own cost.
                var gc = (GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds;
                samples[name].Add(new Sample(queued, call, layout, frame, PopoverOpenTiming.MsSince(start), gc));
                due = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 400 / 1000;
                await Task.Delay(400);
            }
        }

        foreach (var (name, list) in samples)
        {
            var rounds = string.Join(" | ", list.Select(s => string.Create(CultureInfo.InvariantCulture,
                $"frame {s.Frame:F1} idle {s.Idle:F1} (queued {s.Queued:F0} call {s.Call:F1} layout {s.Layout:F1} gc {s.Gc:F1})")));
            logger?.Info(AppLogCategory.Rendering, $"[SwitchProbe] {name}: {rounds} ms");
        }
    }

    private readonly record struct Sample(double Queued, double Call, double Layout, double Frame, double Idle, double Gc);

    private static Task NextFrameAsync(TopLevel topLevel)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        topLevel.RequestAnimationFrame(_ => done.TrySetResult());
        return done.Task;
    }
}
