namespace DotCharter.Presentation.Shared;

/// <summary>
///     One run's progress target. A report delivered after <see cref="Close" /> is
///     dropped, and Close waits out a report mid-apply, so a reset made after it
///     is the last word.
/// </summary>
public sealed class ClosableProgress<T> : IProgress<T>
{
    private readonly object _gate = new();
    private Action<T>? _apply;

    public ClosableProgress(Action<T> apply) => _apply = apply;

    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _apply is null;
            }
        }
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            _apply?.Invoke(value);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _apply = null;
        }
    }
}
