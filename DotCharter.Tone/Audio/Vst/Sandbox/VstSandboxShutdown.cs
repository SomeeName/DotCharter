namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>A single wait over all child stop tasks, with an injectable shared bound.</summary>
internal interface IVstSandboxStopWait
{
    bool WaitForDispose(Task stops, TimeSpan bound);
    Task<bool> WaitAsync(Task stops, TimeSpan bound);
}

internal sealed class VstSandboxStopWait : IVstSandboxStopWait
{
    public bool WaitForDispose(Task stops, TimeSpan bound) => VstSandboxCalls.WaitForDispose(stops, bound);

    public async Task<bool> WaitAsync(Task stops, TimeSpan bound)
    {
        try { await stops.WaitAsync(bound).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
        catch { return true; }
    }
}

/// <summary>Requests every child's quit before waiting once, then kills unfinished stops.</summary>
internal static class VstSandboxShutdown
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed record Stop(IVstSandboxHost Host, Task Completion);

    public static async Task StopAsync(IEnumerable<VstSandboxSeat> seats, bool disarmBridge, IVstSandboxStopWait wait)
    {
        var stops = Begin(seats, disarmBridge);
        if (stops.Count == 0) return;
        var all = Task.WhenAll(stops.Select(stop => stop.Completion));
        try { await wait.WaitAsync(all, Bound).ConfigureAwait(false); }
        finally { VstSandboxCalls.ObserveLate(all); Finish(stops); }
    }

    public static void Dispose(IEnumerable<VstSandboxSeat> seats, IVstSandboxStopWait wait)
    {
        var stops = Begin(seats, disarmBridge: true);
        if (stops.Count == 0) return;
        var all = Task.WhenAll(stops.Select(stop => stop.Completion));
        try { wait.WaitForDispose(all, Bound); }
        finally { VstSandboxCalls.ObserveLate(all); Finish(stops); }
    }

    private static List<Stop> Begin(IEnumerable<VstSandboxSeat> seats, bool disarmBridge)
    {
        var stops = new List<Stop>();
        foreach (var seat in seats)
        {
            seat.Recovery.CancelPendingRestart();
            var host = seat.TakeHostForShutdown(disarmBridge);
            if (host == null) continue;
            Task completion;
            try { completion = host.StopAsync(GracePeriod); }
            catch (Exception ex) { completion = Task.FromException(ex); }
            stops.Add(new Stop(host, completion));
        }
        return stops;
    }

    private static void Finish(IEnumerable<Stop> stops)
    {
        foreach (var stop in stops)
        {
            if (!stop.Completion.IsCompletedSuccessfully)
            {
                VstSandboxCalls.ObserveLate(stop.Completion);
                try { stop.Host.Kill(); } catch { /* ignored */ }
            }
            try { stop.Host.Dispose(); } catch { /* ignored */ }
        }
    }
}
