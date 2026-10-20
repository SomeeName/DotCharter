namespace DotCharter.UI.ViewModels.Dialogs;

/// <summary>
///     The one-at-a-time queue behind <see cref="MessageDialogViewModel.ShowAsync" />,
///     with a way OUT of a dialog nobody can dismiss.
///     <para>
///         A plain <c>SemaphoreSlim</c> held from "wait" to "the user dismissed"
///         had a failure mode this repo already knows: a dialog view model that
///         opens while NOTHING renders (the missing <c>RegisterLazy</c> line in
///         <c>DialogContainerView</c>). Its pending task is never completed, the
///         semaphore is never released, and every later <c>ShowAsync</c> in the
///         process blocks forever with no log — including the save-failure
///         dialog, the one message that must never be silent.
///     </para>
///     <para>
///         So a caller waits for its turn for at most <see cref="StuckTimeout" />;
///         past that it DISPLACES the holder — completes the stuck dialog's task
///         with <c>false</c> (a cancel, the safe answer for every confirmation
///         in the app) — and takes the turn without waiting further. Turns are
///         identified by token so a displaced holder that eventually wakes
///         cannot release a turn it does not own. Pure: no dispatcher, no
///         clock of its own beyond the timeout.
///     </para>
/// </summary>
internal sealed class DialogTurnstile(TimeSpan stuckTimeout)
{
    /// <summary>A dialog the user has genuinely been reading for this long is
    /// rare; a dialog that CANNOT be dismissed is stuck forever. Generous, so
    /// the first case is almost never displaced, bounded, so the second is.</summary>
    public static readonly TimeSpan DefaultStuckTimeout = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly object _gate = new();
    private object? _holder;
    private int _displacements;

    /// <summary>How long a caller waits before displacing the holder.</summary>
    public TimeSpan StuckTimeout { get; } = stuckTimeout;

    /// <summary>How many times a stuck holder has been displaced. Diagnostic.</summary>
    public int Displacements => Volatile.Read(ref _displacements);

    /// <summary>
    ///     Waits for the turn. Returns the token <see cref="Exit" /> needs, and
    ///     whether the turn was TAKEN from a stuck holder — in which case
    ///     <paramref name="displaceHolder" /> has already run.
    /// </summary>
    public async Task<(object Token, bool Displaced)> EnterAsync(Action displaceHolder,
        CancellationToken cancellationToken = default)
    {
        var token = new object();
        if (await _queue.WaitAsync(StuckTimeout, cancellationToken).ConfigureAwait(false))
        {
            lock (_gate) _holder = token;
            return (token, false);
        }

        // The holder is stuck. Take its turn: the semaphore count stays where
        // it is, and the identity check in Exit keeps the books straight — the
        // stuck holder's eventual Exit is a no-op, ours releases.
        lock (_gate) _holder = token;
        Interlocked.Increment(ref _displacements);
        displaceHolder();
        return (token, true);
    }

    /// <summary>Gives the turn back — only if <paramref name="token" /> still
    /// owns it. A displaced holder's Exit must not free the thief's turn.</summary>
    public void Exit(object token)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_holder, token)) return;
            _holder = null;
        }

        _queue.Release();
    }
}
