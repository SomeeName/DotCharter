using System.Runtime.InteropServices;

namespace DotCharter.Tone.UI.Platform;

/// <summary>
///     Raises the Windows multimedia timer resolution to 1 ms for the process
///     lifetime. The 3D preview render loop (<c>Preview3DRenderDriver</c>) paces
///     both its fps cap and its idle park with <see cref="System.Threading.Thread.Sleep(int)" />
///     of 1–4 ms — but on Windows a <c>Sleep</c> is quantised to the current
///     system timer resolution, which DEFAULTS to ~15.6 ms. Measured on the
///     owner's machine: <c>Sleep(1)</c> = 15.5 ms by default, 1.5 ms after
///     <c>timeBeginPeriod(1)</c>. Without the raise the render loop can only
///     pace at ~64 fps (one 15.6 ms quantum per frame) whenever a frame finishes
///     faster than the cap interval, which is exactly what happens on a
///     high-refresh monitor — the 280 fps target is unreachable and every
///     Sleep-based pace/park injects a 15 ms hitch.
///     <para>
///         JUCE raises the resolution too, but only while an audio device /
///         <c>HighResolutionTimer</c> is active — so a paused editor, a scrub,
///         or an idle device leaves the render loop on the coarse default. This
///         makes the guarantee explicit and process-wide instead of an implicit
///         dependency on the audio subsystem's state.
///     </para>
///     <para>
///         <c>timeBeginPeriod</c> is reference-counted by the OS, so this nests
///         harmlessly with JUCE's own calls. The cost is a higher global timer
///         interrupt rate (~1 kHz) — the standard, expected trade-off for any
///         interactive high-refresh desktop app (every game does it), negligible
///         on a desktop rig. Windows-only; a no-op elsewhere.
///     </para>
/// </summary>
internal static class WindowsTimerResolution
{
    private const uint TargetPeriodMs = 1;
    private static bool _raised;

    /// <summary>Raise the timer resolution to 1 ms. Idempotent; safe to call
    /// before Avalonia is initialised.</summary>
    public static void Raise()
    {
        if (_raised || !OperatingSystem.IsWindows()) return;
        try
        {
            if (timeBeginPeriod(TargetPeriodMs) == 0 /* TIMERR_NOERROR */)
                _raised = true;
        }
        catch (DllNotFoundException)
        {
            // winmm unavailable (should never happen on Windows) — leave the
            // default resolution; the render loop still works, just coarsely.
        }
    }

    /// <summary>Release the raise on shutdown. Best-effort — the OS drops the
    /// period on process exit regardless, which covers the hard-terminate path
    /// (<c>App.TerminateNow</c>) where this never runs.</summary>
    public static void Release()
    {
        if (!_raised) return;
        _raised = false;
        try { timeEndPeriod(TargetPeriodMs); }
        catch (DllNotFoundException) { }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
