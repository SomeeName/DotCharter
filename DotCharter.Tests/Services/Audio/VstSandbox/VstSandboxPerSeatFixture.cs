using System.Reflection;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.Audio.Vst.Sandbox;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

internal sealed class VstSandboxPerSeatFixture : IDisposable
{
    private readonly string _plugin = Path.GetTempFileName();
    public JuceAudioPlaybackService Service { get; }
    public IJuceAudioEngine Engine { get; } = DispatchProxy.Create<IJuceAudioEngine, SandboxEngineProxy>();
    public SandboxEngineProxy Bridge => (SandboxEngineProxy)Engine;
    public List<SandboxChild> Children { get; } = [];
    public List<string> Traffic { get; } = [];
    public List<(Guid Id, bool Loaded)> Notifications { get; } = [];
    public bool RejectChainLoads { get; set; }
    public FakeAppLogger Logger { get; } = new();
    public Action<SandboxChild>? ConfigureChild { get; set; }
    public Func<TimeSpan, Task> RestartDelay { get; set; } = _ => Task.CompletedTask;
    public SandboxStopWait StopWait { get; } = new();

    /// <summary>
    ///     The seats' health monitor never ticks on its own here: the fake bridge's heartbeat is a constant, which
    ///     the real 3 s timer reads as a WEDGED child on its second tick, killing a healthy seat and counting a crash
    ///     whenever a starved run spans two ticks. Tests that exercise the monitor call <c>HealthTick</c> themselves.
    /// </summary>
    private static readonly TimeSpan HealthTimerOff = TimeSpan.FromDays(1);

    public VstSandboxPerSeatFixture()
    {
        Service = new JuceAudioPlaybackService(FakeLocalization.English(), () => Engine, () =>
        {
            var child = new SandboxChild(Children.Count, Traffic) { RejectChainLoad = RejectChainLoads };
            ConfigureChild?.Invoke(child);
            Children.Add(child);
            return child;
        }, delay => RestartDelay(delay), StopWait, Logger, HealthTimerOff);
        Service.GuitarVstSlotLoadChanged += (_, e) => Notifications.Add(e);
    }

    public VstChainEntry Entry(int player, Guid? id = null, bool bypass = false) => new()
    { Id = id ?? Guid.NewGuid(), Player = player, PluginPath = _plugin, Bypass = bypass };

    public Task Chain(params VstChainEntry[] entries) => Call("ApplySandboxGuitarChainCoreAsync", (object)entries);
    public Task Instrument(bool load) => Call("LoadInstrumentVstSandboxCoreAsync", load ? _plugin : null, false);
    public Task Call(string method, params object?[] args) => (Task)Invoke(Service, method, args)!;
    public VstSandboxRecovery Recovery(int player) => Field<VstSandboxSeat[]>(Service, "_sandboxSeats")[player].Recovery;
    public VstSandboxSeat Seat(int player) => Field<VstSandboxSeat[]>(Service, "_sandboxSeats")[player];

    public static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    public static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    public void Dispose()
    {
        Service.Dispose();
        File.Delete(_plugin);
    }
}

internal sealed class SandboxChild(int number, List<string> traffic) : IVstSandboxHost
{
    private bool _quitRequested;
    private bool _exitRaised;
    private bool _started;
    public int Number => number;
    public string ShmName => $"child-{number}-shm";
    public string RequestEventName => $"child-{number}-req";
    public string ResponseEventName => $"child-{number}-resp";
    public bool IsRunning { get; private set; }
    public int? ChildProcessId => null;
    public List<VstSandboxRequest> Requests { get; } = [];
    public int Stops { get; private set; }
    public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<VstSandboxRequest>? NextRequest { get; set; }
    public event Action<bool>? Exited;
    public event Action<string>? EditorClosedByUser;
    public event Action<string>? ChildLog;
    public TaskCompletionSource? UnloadReply { get; set; }
    public bool RejectUnload { get; set; }
    public bool ThrowOnUnload { get; set; }
    public bool RejectChainLoad { get; set; }
    public HashSet<string> RejectLoadSlots { get; } = [];
    public Func<VstSandboxRequest, Task>? BeforeReply { get; set; }
    public bool RejectGetState { get; set; }
    public Exception? StartFailure { get; set; }
    public TaskCompletionSource? StopReply { get; set; }
    public Dictionary<string, string> States { get; } = [];
    public int Kills { get; private set; }
    public int DisposeCalls { get; private set; }
    public List<bool> ExitRequests { get; } = [];

    public Task StartAsync(int sampleRate, int blockSize, TimeSpan? connectTimeout = null)
    {
        if (StartFailure != null) return Task.FromException(StartFailure);
        _started = true;
        IsRunning = true;
        ChildLog?.Invoke("started");
        return Task.CompletedTask;
    }

    public async Task<VstSandboxMessage> SendAsync(VstSandboxRequest request, TimeSpan? timeout = null)
    {
        Requests.Add(request);
        traffic.Add($"{number}:{request.Op}:{request.Slot}");
        NextRequest?.TrySetResult(request);
        if (BeforeReply != null) await BeforeReply(request).ConfigureAwait(false);
        if (request.Op == "unload" && UnloadReply != null) await UnloadReply.Task;
        if (request.Op == "unload" && ThrowOnUnload) throw new IOException("unload failed");
        if (request.Op == "unload" && RejectUnload) return new VstSandboxMessage { Ok = false, Error = "unload rejected" };
        if (request.Op == "load" && RejectChainLoad) return new VstSandboxMessage { Ok = false, Error = "load rejected" };
        if (request.Op == "load" && request.Slot != null && RejectLoadSlots.Contains(request.Slot))
            return new VstSandboxMessage { Ok = false, Error = "slot load rejected" };
        if (request.Op == "getState" && RejectGetState) return new VstSandboxMessage { Ok = false, Error = "state unavailable" };
        if (request.Op == "setState" && request.Slot != null && request.State != null) States[request.Slot] = request.State;
        if (request.Op == "getState" && request.Slot != null && States.TryGetValue(request.Slot, out var state))
            return new VstSandboxMessage { Ok = true, State = state };
        return new VstSandboxMessage { Ok = true, State = "c3RhdGU=" };
    }

    public void CloseEditor(string slot) => EditorClosedByUser?.Invoke(slot);
    public void Crash()
    {
        IsRunning = false;
        RaiseExited();
    }

    public Task StopAsync(TimeSpan? gracePeriod = null)
    {
        Stops++;
        _quitRequested = true;
        Stopped.TrySetResult();
        if (StopReply != null) return CompleteStopAsync();
        IsRunning = false;
        RaiseExited();
        return Task.CompletedTask;
    }

    private async Task CompleteStopAsync()
    {
        await StopReply!.Task.ConfigureAwait(false);
        IsRunning = false;
        RaiseExited();
    }

    private void RaiseExited()
    {
        if (!_started || _exitRaised) return;
        _exitRaised = true;
        ExitRequests.Add(_quitRequested);
        Exited?.Invoke(_quitRequested);
    }

    public void Kill()
    {
        Kills++;
        _quitRequested = true;
        IsRunning = false;
        RaiseExited();
        StopReply?.TrySetResult();
    }

    public void Dispose()
    {
        DisposeCalls++;
        _quitRequested = true;
        IsRunning = false;
        RaiseExited();
    }
}

internal sealed class SandboxStopWait : IVstSandboxStopWait
{
    private readonly VstSandboxStopWait _default = new();
    public Action<Task, TimeSpan>? BeforeWait { get; set; }
    public bool Timeout { get; set; }
    public List<TimeSpan> Bounds { get; } = [];

    public bool WaitForDispose(Task stops, TimeSpan bound)
    {
        Bounds.Add(bound);
        BeforeWait?.Invoke(stops, bound);
        return !Timeout && _default.WaitForDispose(stops, bound);
    }

    public Task<bool> WaitAsync(Task stops, TimeSpan bound)
    {
        Bounds.Add(bound);
        BeforeWait?.Invoke(stops, bound);
        return Timeout ? Task.FromResult(false) : _default.WaitAsync(stops, bound);
    }
}

public class SandboxEngineProxy : DispatchProxy
{
    public List<(int Player, string? Shm, string? Request, string? Response, bool Enabled)> Arms { get; } = [];
    public List<int> Stats { get; } = [];
    public Func<int, int, bool> ArmResult { get; set; } = (_, _) => true;
    public Dictionary<int, int> ArmCalls { get; } = [];
    public HashSet<int> EnabledSeats { get; } = [];
    public Func<int, uint> Heartbeat { get; set; } = _ => 42u;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        args ??= [];
        switch (targetMethod!.Name)
        {
            case "get_SampleRate": return 48000;
            case "get_BufferFrames": return 256;
            case "SetRemoteBridge":
            {
                var offset = args.Length == 5 ? 1 : 0;
                var player = offset == 1 ? (int)args[0]! : 0;
                var enabled = (bool)args[offset + 3]!;
                Arms.Add((player, (string?)args[offset], (string?)args[offset + 1], (string?)args[offset + 2], enabled));
                if (enabled)
                {
                    var call = ArmCalls.GetValueOrDefault(player) + 1;
                    ArmCalls[player] = call;
                    if (!ArmResult(player, call)) return false;
                    EnabledSeats.Add(player);
                }
                else
                {
                    EnabledSeats.Remove(player);
                }
                return true;
            }
            case "TryGetRemoteBridgeStats":
            {
                var offset = args.Length == 4 ? 1 : 0;
                Stats.Add(offset == 1 ? (int)args[0]! : 0);
                args[offset] = 1u;
                args[offset + 1] = 0u;
                args[offset + 2] = Heartbeat(offset == 1 ? (int)args[0]! : 0);
                return true;
            }
            default: return null;
        }
    }
}
