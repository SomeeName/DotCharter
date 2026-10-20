using System.Diagnostics;
using Avalonia.Threading;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// What showing a surface COSTS on the user's box: an Inspector tab
///     switch, a hosted dialog open. Measured from the gesture to the first
///     <see cref="DispatcherPriority.Background" /> tick after it — the layout and
///     render of the newly shown subtree run at higher priorities, so that tick is
///     the first moment the UI is free again. The sibling of
///     <see cref="PopoverOpenTiming" />, for the same reason: the owner reported a
///     short stutter on the Phrases / Song tabs and the Tone designer that no
///     headless fixture of realistic size had measured, so the next report carries
///     a number and a machine. Without a <see cref="Sink" /> nothing is recorded.
/// </summary>
public static class SurfaceShowTiming
{
    /// <summary>Receives (surface name, milliseconds) per completed show.</summary>
    public static Action<string, double>? Sink { get; set; }

    /// <summary>Starts timing a show that begins now.</summary>
    public static void Begin(string name) => BeginAt(name, Stopwatch.GetTimestamp());

    /// <summary>Starts timing a show that began at <paramref name="startTicks" />
    /// (a <see cref="Stopwatch.GetTimestamp" /> reading).</summary>
    public static void BeginAt(string name, long startTicks)
    {
        if (Sink is null) return;
        Dispatcher.UIThread.Post(() => Record(name, PopoverOpenTiming.MsSince(startTicks)), DispatcherPriority.Background);
    }

    public static void Record(string name, double milliseconds)
    {
        var sink = Sink;
        if (sink is null) return;
        try { sink(name, milliseconds); } catch { /* a diagnostic must never take a show down */ }
    }
}
