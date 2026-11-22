using System.Diagnostics;

namespace DotCharter.Services.Diagnostics;

/// <summary>Named synchronous UI work, reported without affecting its result or exception.</summary>
public static class UiWorkTiming
{
    public static Action<string, double>? Sink { get; set; }

    public static void Measure(string name, Action action)
    {
        var sink = Sink;
        if (sink is null)
        {
            action();
            return;
        }
        var start = Stopwatch.GetTimestamp();
        try { action(); }
        finally { Record(sink, name, start); }
    }

    public static T Measure<T>(string name, Func<T> action)
    {
        var sink = Sink;
        if (sink is null) return action();
        var start = Stopwatch.GetTimestamp();
        try { return action(); }
        finally { Record(sink, name, start); }
    }

    private static void Record(Action<string, double> sink, string name, long start)
    {
        try { sink(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
        catch { /* a diagnostic must never replace the measured work's exception */ }
    }
}
