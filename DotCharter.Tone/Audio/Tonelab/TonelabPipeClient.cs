using System.IO.Pipes;
using System.Text;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>How long the client waits. <c>apply-file</c> rebuilds the game's chain, so it gets longer than the rest.</summary>
public sealed record TonelabTimeouts(TimeSpan Connect, TimeSpan Command, TimeSpan ApplyFile)
{
    /// <summary>
    ///     Sized for a HIDDEN game: its main loop runs at 10 frames/s and serves the pipe from it, so a
    ///     connect or a reply can take a few hundred ms. Sub-second limits give up on a live game that
    ///     does answer, and an abandoned exchange costs a reconnect.
    /// </summary>
    public static TonelabTimeouts Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
}

/// <summary>
///     The client end of <c>\\.\pipe\tonelab-&lt;pid&gt;</c>: one command line out, one
///     reply line back, one command in flight at a time. Every call is async and
///     cancellable and does its I/O on the pipe's overlapped handle, so no caller ever
///     blocks a thread on the game — call it from a worker, never the UI or an audio thread.
///     <para>
///         A timeout or an I/O fault DROPS the connection: a reply that arrives late would
///         otherwise be read as the answer to the next command. The next call connects again,
///         which is also how the client finds a game that restarted under the same pid name.
///     </para>
/// </summary>
public sealed class TonelabPipeClient(string pipeName, TonelabTimeouts? timeouts = null) : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly TonelabTimeouts _timeouts = timeouts ?? TonelabTimeouts.Default;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private bool _disposed;

    public string PipeName { get; } = pipeName;

    public bool IsConnected => _pipe is { IsConnected: true };

    public async Task<TonelabStatus> StatusAsync(CancellationToken ct = default)
    {
        var (line, failure) = await SendAsync(TonelabProtocol.Status(), _timeouts.Command, ct).ConfigureAwait(false);
        return failure is not null ? TonelabStatus.Failed(failure) : TonelabProtocol.ParseStatus(line);
    }

    /// <summary><c>ok applied &lt;toneKey&gt;</c>; the key is <see cref="TonelabResult.Detail" />.</summary>
    public Task<TonelabResult> ApplyFileAsync(string path, CancellationToken ct = default) =>
        RunAsync(TonelabProtocol.ApplyFile(path), _timeouts.ApplyFile, ct, "applied");

    public Task<TonelabResult> KnobAsync(string gearSlot, string knobKey, float value, CancellationToken ct = default) =>
        RunAsync(TonelabProtocol.Knob(gearSlot, knobKey, value), _timeouts.Command, ct);

    public Task<TonelabResult> RestoreAsync(CancellationToken ct = default) =>
        RunAsync(TonelabProtocol.Restore(), _timeouts.Command, ct, "restored");

    /// <summary>
    ///     <c>tone dump &lt;path&gt;</c>: the game writes its active tone there as JSON and answers
    ///     <c>ok dumped &lt;n&gt; live gears</c> (<see cref="TonelabResult.Detail" /> is the rest after <c>dumped</c>).
    /// </summary>
    public Task<TonelabResult> DumpAsync(string path, CancellationToken ct = default) =>
        RunAsync(TonelabProtocol.Dump(path), _timeouts.ApplyFile, ct, "dumped");

    public Task<TonelabResult> PerfAsync(bool whileHidden, int framesPerSecond, CancellationToken ct = default) =>
        RunAsync(TonelabProtocol.Perf(whileHidden, framesPerSecond), _timeouts.Command, ct);

    public Task<TonelabResult> MixMuteAsync(bool on, CancellationToken ct = default) =>
        RunAsync(TonelabProtocol.MixMute(on), _timeouts.Command, ct);

    public Task<TonelabResult> WindowAsync(bool show, CancellationToken ct = default) =>
        RunAsync(TonelabProtocol.Window(show), _timeouts.Command, ct);

    public Task<TonelabResult> QuitAsync(CancellationToken ct = default) =>
        RunAsync(TonelabProtocol.Quit(), _timeouts.Command, ct);

    public void Dispose()
    {
        _disposed = true;
        Drop();
    }

    private async Task<TonelabResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct, params string[] okWords)
    {
        var (line, failure) = await SendAsync(command, timeout, ct).ConfigureAwait(false);
        return failure ?? TonelabProtocol.Parse(line, okWords);
    }

    /// <summary>The reply line, or the failure that stands for it. Cancellation by <paramref name="ct" /> throws.</summary>
    private async Task<(string? Line, TonelabResult? Failure)> SendAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureConnectedAsync(ct).ConfigureAwait(false)) return (null, TonelabResult.NotConnected("no pipe " + PipeName));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                var bytes = Utf8.GetBytes(command + "\n");
                await _pipe!.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                await _pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                var line = await _reader!.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null)
                {
                    Drop();
                    return (null, TonelabResult.NotConnected("the game closed the pipe"));
                }

                return (line, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Drop();
                return (null, TonelabResult.Timeout(command));
            }
            catch (OperationCanceledException)
            {
                Drop();
                throw;
            }
            catch (IOException ex)
            {
                Drop();
                return (null, TonelabResult.NotConnected(ex.Message));
            }
            catch (ObjectDisposedException)
            {
                Drop();
                return (null, TonelabResult.NotConnected("the pipe was closed"));
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return true;
        Drop();
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(_timeouts.Connect, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, Utf8, false, 1024, leaveOpen: true);
        return true;
    }

    private void Drop()
    {
        _reader?.Dispose();
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;
    }
}
