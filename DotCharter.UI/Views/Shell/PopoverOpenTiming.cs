using System.Diagnostics;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Measures what a popover open actually COSTS on the user's box — the
///     wall time from <c>FlyoutBase.OnOpening</c> to <c>OnOpened</c>, which
///     spans the popup HWND + GPU-surface creation Avalonia pays per open
///     while <c>Win32PlatformOptions.OverlayPopups</c> is off (the app's
///     default; see <c>Program.cs</c>) — and hands it to the app log.
///     <para>
///         <b>Why (owner report).</b> "A short freeze, ~200 ms,
///         opening Tempo / Mix / Tools / Tracks — in the release build and from
///         Rider alike." Every prewarm this shell has is about the FIRST open
///         (styles, templates, layout of a closed popup's content) and is
///         measured headlessly, where no OS window exists. A cost paid on every
///         open, unchanged by ReadyToRun, is not that; it is the per-open
///         top-level, and nothing measured it on a real desktop. This does, so
///         the next report can carry a number and a machine.
///     </para>
///     <para>
///         Pure and static on purpose: a <c>Flyout</c> is markup-constructed and
///         has no DI; <see cref="Sink" /> is wired once by <c>App</c> to the
///         logger. Without a sink a record is a no-op.
///     </para>
/// </summary>
public static class PopoverOpenTiming
{
    /// <summary>Receives (popover name, milliseconds) per completed open.</summary>
    public static Action<string, double>? Sink { get; set; }

    /// <summary>Ticks-to-ms on the Stopwatch axis; exposed for the flyout and tests.</summary>
    public static double MsSince(long startTicks) =>
        (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

    public static void Record(string name, double milliseconds)
    {
        var sink = Sink;
        if (sink is null) return;
        try { sink(name, milliseconds); } catch { /* a diagnostic must never take an open down */ }
    }
}
