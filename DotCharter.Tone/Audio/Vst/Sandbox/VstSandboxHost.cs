using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// Owns ONE instance of the sandboxed VST host process
/// (<c>juce_vst_host.exe</c>): spawn, pipe handshake, serialized
/// request/response traffic, unsolicited event dispatch, and teardown
/// (graceful <c>quit</c> with a kill fallback). Restart orchestration and
/// chain replay live in the caller — a host instance is single-use; after
/// <see cref="Exited"/> fires, create a fresh one.
/// </summary>
public sealed class VstSandboxHost : IDisposable, IVstSandboxChannel
{
    public const string ExecutableName = "juce_vst_host.exe";

    private static int _instanceCounter;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<VstSandboxMessage>> _pending = [];
    private readonly object _pendingGate = new();
    private readonly CancellationTokenSource _cts = new();

    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private long _nextId;
    private volatile bool _quitRequested;
    private volatile bool _disposed;

    public string ShmName { get; }
    public string RequestEventName { get; }
    public string ResponseEventName { get; }
    public string PipeName { get; }

    public int? ChildProcessId { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Fired once when the child process exits for any reason.
    /// <c>true</c> = the exit was requested (quit/kill by us).</summary>
    public event Action<bool>? Exited;
    /// <summary>The user closed a plugin editor window in the child.
    /// Slot id, or "instrument".</summary>
    public event Action<string>? EditorClosedByUser;
    /// <summary>Child stderr log line (already prefixed by the child).</summary>
    public event Action<string>? ChildLog;

    public VstSandboxHost()
    {
        var id = $"dotcharter-vst-{Environment.ProcessId}-{Interlocked.Increment(ref _instanceCounter)}";
        ShmName = id + "-shm";
        RequestEventName = id + "-req";
        ResponseEventName = id + "-resp";
        PipeName = id + "-pipe";
    }

    /// <summary>Locate the host executable next to the deployed native DLL.
    /// Returns null when it is not present (native build too old).</summary>
    public static string? FindExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            System.IO.Path.Combine(baseDir, ExecutableName),
            System.IO.Path.Combine(baseDir, "runtimes", "win-x64", "native", ExecutableName),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Spawn the child and complete the pipe handshake. Throws on failure
    /// (missing exe, pipe timeout, immediate child death).
    /// </summary>
    public async Task StartAsync(int sampleRate, int blockSize, TimeSpan? connectTimeout = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VstSandboxHost));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The VST sandbox is Windows-only.");

        var exe = FindExecutable()
            ?? throw new FileNotFoundException($"'{ExecutableName}' not found next to the native runtime.");

        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                                          PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"shm={ShmName} req={RequestEventName} resp={ResponseEventName} " +
                        $"pipe={PipeName} sr={sampleRate} block={blockSize} parent={Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            // Only stderr is redirected here, and StandardOutputEncoding without
            // RedirectStandardOutput makes Process.Start THROW — which stops the
            // sandbox from starting at all.
            StandardErrorEncoding = Encoding.UTF8,
        };
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            FailAllPending(new IOException("VST sandbox process exited."));
            try { Exited?.Invoke(_quitRequested); } catch { /* observer */ }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                try { ChildLog?.Invoke(e.Data); } catch { /* observer */ }
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start the VST sandbox process.");
        _process = process;
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(connectTimeout ?? TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _cts.Token);
        try
        {
            await _pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw new TimeoutException("VST sandbox did not connect its control pipe in time.");
        }

        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));

        // The child sends `hello` immediately after connecting. It arrives on
        // the read loop; we don't strictly need to block on it — the first
        // ping doubles as the handshake proof.
        var pong = await SendAsync(new VstSandboxRequest { Op = "ping" }, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        if (pong is not { Ok: true })
            throw new InvalidOperationException("VST sandbox handshake ping failed.");
    }

    /// <summary>
    /// Serialized request/response. Returns the child's response, or throws
    /// on pipe failure / timeout / child exit.
    /// </summary>
    public async Task<VstSandboxMessage> SendAsync(VstSandboxRequest request, TimeSpan? timeout = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VstSandboxHost));
        var writer = _writer ?? throw new InvalidOperationException("Sandbox pipe is not connected.");

        var id = Interlocked.Increment(ref _nextId);
        var tagged = request with { Id = id };
        var tcs = new TaskCompletionSource<VstSandboxMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate) _pending[id] = tcs;

        await _sendGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(VstSandboxProtocol.Serialize(tagged)).ConfigureAwait(false);
        }
        catch
        {
            lock (_pendingGate) _pending.Remove(id);
            throw;
        }
        finally
        {
            _sendGate.Release();
        }

        var effective = timeout ?? TimeSpan.FromSeconds(15);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(effective, _cts.Token)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            lock (_pendingGate) _pending.Remove(id);
            throw new TimeoutException($"VST sandbox did not answer '{request.Op}' within {effective.TotalSeconds:F0}s.");
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Graceful stop: pipe quit, bounded wait, kill fallback.</summary>
    public async Task StopAsync(TimeSpan? gracePeriod = null)
    {
        _quitRequested = true;
        var process = _process;
        if (process == null) return;

        try
        {
            if (!process.HasExited && _writer != null)
                await SendAsync(new VstSandboxRequest { Op = "quit" }, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch { /* dead pipe / timeout — fall through to kill */ }

        try
        {
            using var cts = new CancellationTokenSource(gracePeriod ?? TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
        }
    }

    /// <summary>Hard kill (used by stop fallback and by health-check escalation).</summary>
    public void Kill()
    {
        _quitRequested = true;
        try
        {
            var p = _process;
            if (p is { HasExited: false }) p.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var pipe = _pipe;
        if (pipe == null) return;
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break; // pipe closed (child gone)
                var msg = VstSandboxProtocol.TryParse(line);
                if (msg == null) continue;

                if (msg.IsResponse)
                {
                    TaskCompletionSource<VstSandboxMessage>? tcs = null;
                    lock (_pendingGate)
                    {
                        if (msg.Id.HasValue && _pending.Remove(msg.Id.Value, out var found)) tcs = found;
                    }
                    tcs?.TrySetResult(msg);
                }
                else if (msg.IsHello)
                {
                    ChildProcessId = msg.Pid.HasValue ? (int)msg.Pid.Value : null;

                    // The child reports the bridge layout it was BUILT against.
                    // Nothing compared it before, so a juce_vst_host.exe left
                    // over from an older build would go on to read and write the
                    // shared block at different offsets than we do — silent
                    // cross-process memory corruption on the audio path. Kill it
                    // instead; Exited reports a requested exit, so the caller
                    // does not spin restarting the same stale executable.
                    if (msg.BridgeVersion is { } childVersion &&
                        childVersion != VstSandboxLayout.BridgeVersion)
                    {
                        try
                        {
                            ChildLog?.Invoke(
                                $"bridge version mismatch: child v{childVersion}, host v{VstSandboxLayout.BridgeVersion}. " +
                                "Killing the sandbox — rebuild juce_vst_host.exe.");
                        }
                        catch { /* observer */ }

                        Kill();
                        break;
                    }
                }
                else if (msg is { IsEvent: true, Event: "editorClosed", Slot: not null })
                {
                    try { EditorClosedByUser?.Invoke(msg.Slot); } catch { /* observer */ }
                }
            }
        }
        catch (OperationCanceledException) { /* teardown */ }
        catch (IOException) { /* pipe broke — Exited handles the rest */ }
        catch (ObjectDisposedException) { /* teardown */ }
        finally
        {
            FailAllPending(new IOException("VST sandbox control pipe closed."));
        }
    }

    private void FailAllPending(Exception reason)
    {
        List<TaskCompletionSource<VstSandboxMessage>> pending;
        lock (_pendingGate)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var tcs in pending) tcs.TrySetException(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _quitRequested = true;
        try { _cts.Cancel(); } catch { /* ignored */ }
        Kill();
        try { _writer?.Dispose(); } catch { /* ignored */ }
        try { _pipe?.Dispose(); } catch { /* ignored */ }
        try { _process?.Dispose(); } catch { /* ignored */ }
        _cts.Dispose();
        _sendGate.Dispose();
        FailAllPending(new ObjectDisposedException(nameof(VstSandboxHost)));
    }
}
