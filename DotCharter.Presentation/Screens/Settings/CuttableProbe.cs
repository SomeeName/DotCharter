using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     One device read on the pool whose result lands on the UI thread. The worker holds the reader's arguments and a
///     landing, never the owner; <c>Drop</c> / <c>Cancel</c> cut the landing, so a late result reaches nothing.
///     The <c>read</c> delegate must be <c>static</c>: a closure over the owner is what this type keeps out.
/// </summary>
internal sealed class CuttableProbe<TResult>(IUiDispatcher ui)
{
    private Landing? _current;
    private CancellationTokenSource _cancel = new();

    /// <summary>Reads on the pool, lands on the UI thread; a read still running is dropped.</summary>
    public Task Start<TArgs>(TArgs args, Func<TArgs, TResult> read, Action<TResult> land)
    {
        _current?.Cut();
        var landing = _current = new Landing(land);
        var token = _cancel.Token;
        return Task.Run(() => Run(ui, args, read, landing), token);
    }

    /// <summary>Drops the read running now: it finishes, but its result lands nowhere.</summary>
    public void Drop()
    {
        _current?.Cut();
        _current = null;
    }

    /// <summary>Ends every read with its owner: a queued one never runs, a running one lands nowhere.</summary>
    public void Cancel()
    {
        Drop();
        _cancel.Cancel();
        _cancel.Dispose();
        _cancel = new CancellationTokenSource();
    }

    private static void Run<TArgs>(IUiDispatcher ui, TArgs args, Func<TArgs, TResult> read, Landing landing)
    {
        if (landing.IsCut) return;
        try
        {
            var found = read(args);
            if (landing.IsCut) return;
            ui.PostNormal(() => landing.Deliver(found));
        }
        catch
        {
            // A failed read lands nothing.
        }
    }

    /// <summary>The one reference from the worker back to its owner, dropped by <see cref="Cut" />.</summary>
    private sealed class Landing(Action<TResult> land)
    {
        private volatile Action<TResult>? _land = land;

        public bool IsCut => _land is null;

        public void Cut() => _land = null;

        public void Deliver(TResult found) => _land?.Invoke(found);
    }
}
