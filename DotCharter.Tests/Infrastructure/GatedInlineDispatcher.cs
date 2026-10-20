using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     An <see cref="IUiDispatcher" /> that runs dispatched work INLINE but under a
///     gate, plus <see cref="Snapshot" /> so a test can read a bound collection with
///     the same exclusion the view model writes it with.
/// </summary>
/// <remarks>
///     <b>"Inline" does not mean "on the test's thread", and that is the whole
///     reason this type exists.</b> <see cref="StemMixerViewModel" />'s rebuild
///     awaits a worker probe with <c>ConfigureAwait(false)</c> and an engine load,
///     so every dispatched step after that runs on a POOL thread — inline, but not
///     here. A plain inline fake therefore lets the view model <c>Clear()</c> and
///     refill the bound <c>Tracks</c> collection while the polling test thread
///     enumerates it: <c>InvalidOperationException: Collection was modified</c>, or
///     worse, a read that lands mid-fill and silently sees the wrong strips.
///     <para>
///         That second shape is the one that made two mixer classes flake without
///         ever throwing. <c>BuildStrips</c> adds the stems and only THEN inserts
///         the master "Original" strip at index 0, so a settle condition of
///         <c>Tracks.Count &gt; 1</c> goes true one line before the master exists —
///         and the very next statement, <c>Single(t =&gt; t.IsMasterMix)</c>, throws
///         "sequence contains no matching element" for a two-stem project.
///         <b>Settle on the strip you are about to read, never on a count.</b>
///     </para>
///     <para>
///         The gate is a real mutual exclusion, not a memory barrier: it is held
///         across the whole delegate, so a snapshot taken while a rebuild is
///         applying waits for that apply to finish rather than interleaving with it.
///     </para>
/// </remarks>
internal sealed class GatedInlineDispatcher : IUiDispatcher
{
    private readonly Lock _gate = new();

    public bool CheckAccess() => true;
    public void Post(Action action) => Run(action);
    public void PostNormal(Action action) => Run(action);

    public Task InvokeAsync(Action action)
    {
        Run(action);
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        lock (_gate) return Task.FromResult(func());
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

    /// <summary>Reads a mixer's bound collection under the same gate its writes
    /// take.</summary>
    public IReadOnlyList<StemTrackViewModel> Snapshot(StemMixerViewModel vm)
    {
        lock (_gate) return vm.Tracks.ToArray();
    }

    /// <summary>Reads anything else the view model touches from a dispatched step —
    /// a domain flag a strip writes through, a derived property — under that same
    /// gate.</summary>
    public T Read<T>(Func<T> read)
    {
        lock (_gate) return read();
    }

    private void Run(Action action)
    {
        lock (_gate) action();
    }
}
