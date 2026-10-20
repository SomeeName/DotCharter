using System.Diagnostics;
using Avalonia.Threading;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Paces the three prewarm drains (<see cref="HiddenSubtreePrewarmer" />,
///     both <see cref="MenuFlyoutPrewarmer" /> chains) so a step only lands
///     while the user is NOT touching the app.
///     <para>
///         <b>Why (release build).</b> The window is
///         interactive 1.5 s after launch, and the drains then run for another
///         ~9 s (chrome 0.4 s, menus 0.5 s, popovers 0.5 s, then 79 hidden
///         surfaces over 6.7 s). Each step is one surface realized on the UI
///         thread — tens to a few hundred ms that nothing can interrupt once it
///         has started. <c>DispatcherPriority.Background</c> only means a step
///         does not START while input is queued; the user moving the mouse
///         during those seconds still lands between two of them and reads the
///         app as "laggy right after launch". A loading screen would hide that by
///         taking the 9 s away from the user instead, which is the worse trade.
///     </para>
///     <para>
///         So: a step is posted only once the last pointer/keyboard input is at
///         least <see cref="QuietWindowMs" /> old; otherwise it is re-armed on a
///         one-shot timer for the remainder of that window. The gate is bounded
///         by <see cref="MaxDeferralMs" /> from the step's REQUEST, so a
///         continuous drag cannot starve the drain — the first-switch lag the
///         prewarm exists to remove would otherwise come back for a user who
///         never rests. <see cref="MainWindow" /> notes input from tunnel
///         handlers; the decision itself is the pure
///         <see cref="ShouldRunNow" />.
///     </para>
///     <para>
///         <b>It is asked twice: when the step is posted and again when its turn
/// comes.</b> A step already sitting in the <c>Background</c> queue
///         when the user clicks would otherwise run between the click and the frame
///         that shows its result — the queue is idle until the render tick — so a
///         3 ms switch drew 74-122 ms late. <see cref="TryRun" /> is that second
///         look; a step it refuses is re-armed like any other.
///     </para>
///     <para>
///         The headless harness sets <see cref="QuietWindowMs" /> to 0 — its
///         bounded dispatcher drain runs queued jobs, not timers, and a test that
///         moved the mouse would otherwise find the surfaces cold.
///         <c>DOTCHARTER_NO_PREWARM_IDLE_GATE=1</c> does the same in the real
///         app for an A/B.
///     </para>
/// </summary>
public static class PrewarmPacer
{
    private static long _lastInputTicks;

    /// <summary>How long input must have been quiet before a step runs.
    /// 0 disables the gate (every step posts at once).</summary>
    public static double QuietWindowMs { get; set; } =
        Environment.GetEnvironmentVariable("DOTCHARTER_NO_PREWARM_IDLE_GATE") == "1" ? 0 : 250;

    /// <summary>Longest a step may be held back from its request, whatever the
    /// input is doing.</summary>
    public static double MaxDeferralMs { get; set; } = 8000;

    /// <summary>Stopwatch ticks of the last noted input; 0 = never.</summary>
    public static long LastInputTicks => Volatile.Read(ref _lastInputTicks);

    /// <summary>Called from the window's tunnel input handlers. One volatile
    /// write; safe from any thread.</summary>
    public static void NoteInput() => Volatile.Write(ref _lastInputTicks, Stopwatch.GetTimestamp());

    /// <summary>The whole rule, pure: quiet-window elapsed, or the deferral cap
    /// reached, or the gate disabled, or no input ever.</summary>
    public static bool ShouldRunNow(long lastInputTicks, long requestedTicks, long nowTicks,
        double quietWindowMs, double maxDeferralMs)
    {
        if (quietWindowMs <= 0 || lastInputTicks == 0) return true;
        if (ToMs(nowTicks - requestedTicks) >= maxDeferralMs) return true;
        return ToMs(nowTicks - lastInputTicks) >= quietWindowMs;
    }

    /// <summary>
    ///     Runs <paramref name="step" /> if <see cref="ShouldRunNow" /> says so at
    ///     <paramref name="nowTicks" />, and reports whether it ran. What a posted step
    ///     does when its dispatcher turn comes: a step posted while the input was
    ///     quiet does NOT run if input arrived since — it goes back to waiting.
    /// </summary>
    public static bool TryRun(Action step, long lastInputTicks, long requestedTicks, long nowTicks,
        double quietWindowMs, double maxDeferralMs)
    {
        if (!ShouldRunNow(lastInputTicks, requestedTicks, nowTicks, quietWindowMs, maxDeferralMs)) return false;
        step();
        return true;
    }

    /// <summary>Posts <paramref name="step" /> at <see cref="DispatcherPriority.Background" />
    /// once the input has been quiet — the drop-in for the drains'
    /// <c>Dispatcher.UIThread.Post(Step, Background)</c>.</summary>
    public static void Post(Action step) => Attempt(step, Stopwatch.GetTimestamp());

    private static void Attempt(Action step, long requestedTicks)
    {
        var now = Stopwatch.GetTimestamp();
        var quiet = QuietWindowMs;
        if (ShouldRunNow(LastInputTicks, requestedTicks, now, quiet, MaxDeferralMs))
        {
            // The answer above is the one at POST time. A step then waits in the
            // Background queue, and a click that lands while it waits is handled at
            // Input priority — after which the queue is idle until the render tick,
            // so the waiting step ran BETWEEN the click and the frame that shows it
            // (the Home -> Chart / Chart -> Library / Library -> Play first
            // entries of 74-122 ms were each one such step; the switch itself is
            // 3-6 ms). Ask again when the step's turn comes.
            Dispatcher.UIThread.Post(() =>
            {
                var ran = TryRun(step, LastInputTicks, requestedTicks, Stopwatch.GetTimestamp(), QuietWindowMs,
                    MaxDeferralMs);
                if (!ran) Attempt(step, requestedTicks);
            }, DispatcherPriority.Background);
            return;
        }

        // Re-arm for what is left of the quiet window (never less than a few
        // ms, so a stream of input does not turn this into a spin).
        var remaining = Math.Max(5, quiet - ToMs(now - LastInputTicks));
        DispatcherTimer.RunOnce(() => Attempt(step, requestedTicks), TimeSpan.FromMilliseconds(remaining),
            DispatcherPriority.Background);
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
