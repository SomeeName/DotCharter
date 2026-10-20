namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
/// Tracks in-flight stem-separation runs so the application shutdown path
/// can wait for the underlying native code (demucs.cpp / ONNX) to finish
/// instead of being killed mid-stride, which causes process exit code -1
/// on Windows.
/// </summary>
/// <remarks>
/// The native demucs.cpp loop is uncancellable from the managed side. The
/// next best thing is to delay the application's service-shutdown step until the
/// loop returns (or a sensible timeout elapses). Each call to
/// <see cref="BeginRun"/> returns a disposable that automatically registers
/// completion. <see cref="WaitForCompletionAsync"/> is best-effort; it
/// will fall through after the timeout instead of hanging the UI thread
/// forever.
/// </remarks>
public sealed class InFlightStemSeparationTracker
{
    private int _inFlight;
    private readonly object _gate = new();
    private TaskCompletionSource _idleTcs = NewTcs();

    public int InFlight => Volatile.Read(ref _inFlight);

    public IDisposable BeginRun()
    {
        lock (_gate)
        {
            _inFlight++;
            if (_idleTcs.Task.IsCompleted) _idleTcs = NewTcs();
        }
        return new Scope(this);
    }

    private void EndRun()
    {
        lock (_gate)
        {
            _inFlight--;
            if (_inFlight <= 0)
            {
                _inFlight = 0;
                _idleTcs.TrySetResult();
            }
        }
    }

    public Task WaitForCompletionAsync(TimeSpan timeout)
    {
        Task task;
        lock (_gate) { if (_inFlight == 0) return Task.CompletedTask; task = _idleTcs.Task; }
        return Task.WhenAny(task, Task.Delay(timeout));
    }

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Scope : IDisposable
    {
        private readonly InFlightStemSeparationTracker _owner;
        private int _disposed;
        public Scope(InFlightStemSeparationTracker owner) => _owner = owner;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.EndRun(); }
    }
}
