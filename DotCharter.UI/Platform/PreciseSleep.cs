using System.Runtime.InteropServices;
using DotCharter.Tone.UI.Platform;

namespace DotCharter.UI.Platform;

/// <summary>
///     Sub-millisecond sleep for render-loop pacing. <c>Thread.Sleep(1)</c>
///     costs ~1.0–1.6 ms even with the process-wide 1 ms timer-resolution pin
///     (<see cref="WindowsTimerResolution" />) — at a 280 Hz fps cap
///     (3.4 ms interval) that quantization alone loses ~30% of the target
///     rate (measured: the 3D loop paced 200 fps against a 294 fps cap).
///     Windows 10 1803+ high-resolution waitable timers wait with ~0.1–0.5 ms
///     accuracy and no spinning; anything older (or a create failure) falls
///     back to plain <c>Thread.Sleep</c>, which is exactly the old behavior.
///     One timer handle per calling thread (thread-affine by design — the 3D
///     render loop is a long-lived thread; the handle is reclaimed by the OS
///     at thread/process exit).
/// </summary>
internal static class PreciseSleep
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    [ThreadStatic] private static nint _timer;
    [ThreadStatic] private static bool _timerUnavailable;

    /// <summary>Blocks the calling thread for ~<paramref name="milliseconds" />
    /// (sub-ms accuracy on Win10 1803+). Never throws; never spins.</summary>
    public static void Sleep(double milliseconds)
    {
        if (milliseconds <= 0) return;

        if (!_timerUnavailable && _timer == 0)
        {
            _timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_timer == 0) _timerUnavailable = true;
        }

        if (_timerUnavailable)
        {
            Thread.Sleep(Math.Max(1, (int)milliseconds));
            return;
        }

        // Negative due time = relative, in 100 ns units.
        var dueTime = -(long)(milliseconds * 10_000.0);
        if (dueTime >= 0) dueTime = -1;
        if (!SetWaitableTimer(_timer, in dueTime, 0, 0, 0, false))
        {
            Thread.Sleep(Math.Max(1, (int)milliseconds));
            return;
        }

        // Bounded wait: the timer is due in `milliseconds`, so anything past
        // that + a generous margin means the wait itself failed — degrade to
        // returning (the caller's pacing loop re-evaluates).
        WaitForSingleObject(_timer, (uint)(milliseconds + 50));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(nint timer, in long dueTime, int period,
        nint completionRoutine, nint argToCompletionRoutine, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
