namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The rig's undo / redo, as whole-rig SNAPSHOTS (Ctrl+Z / Ctrl+Y). Every
///     entry is a complete state the rig can be put back into, so one mechanism
///     undoes a knob turn, a bypass, a unit added, removed or moved, and a preset
///     loaded — nothing has to know how to reverse itself.
///     <para>
///         Edits that arrive in a BURST (a knob being dragged, a unit added with its
///         params set) are one step: a new entry is only recorded when the last
///         edit was longer ago than <see cref="BurstGapMs" />. Pure (the clock is
///         passed in), so every rule is pinned in the default gate.
///     </para>
/// </summary>
public sealed class RigHistory<T> where T : class
{
    /// <summary>Edits closer together than this are one undo step.</summary>
    public const long BurstGapMs = 600;

    /// <summary>The deepest the undo stack goes.</summary>
    public const int MaxDepth = 100;

    private readonly Func<T, T, bool> _same;
    private readonly LinkedList<T> _undo = new();
    private readonly Stack<T> _redo = new();
    private T? _settled;
    private long _lastEditMs = long.MinValue;

    /// <param name="same">True when two states sound the same (no step is recorded
    /// for an edit that changed nothing).</param>
    public RigHistory(Func<T, T, bool> same) => _same = same;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoDepth => _undo.Count;

    /// <summary>Starts over from <paramref name="state" /> — a new board (a seat or
    /// profile switch): the old steps belong to a rig that is not on screen.</summary>
    public void Reset(T state)
    {
        _undo.Clear();
        _redo.Clear();
        _settled = state;
        _lastEditMs = long.MinValue;
    }

    /// <summary>
    ///     An edit happened; <paramref name="now" /> is the rig AFTER it. The state
    ///     before the burst it opened becomes an undo step, and redo is spent — a
    ///     new edit forks history.
    /// </summary>
    public void Edited(T now, long nowMs)
    {
        if (_settled is null)
        {
            _settled = now;
            _lastEditMs = nowMs;
            return;
        }

        // a clock that went BACKWARDS is a different clock, never the same burst
        var newBurst = _lastEditMs == long.MinValue || nowMs < _lastEditMs || nowMs - _lastEditMs > BurstGapMs;
        if (newBurst)
        {
            // a note that changed nothing opens no burst: were it to, the real
            // edit landing inside its window would be folded into it and lost
            if (_same(_settled, now)) return;
            _undo.AddLast(_settled);
            if (_undo.Count > MaxDepth) _undo.RemoveFirst();
            _redo.Clear();
        }

        _settled = now;
        _lastEditMs = nowMs;
    }

    /// <summary>
    ///     A DISCRETE gesture happened (a scene switch, a scene renamed or
    ///     stored); <paramref name="now" /> is the rig after it. Always its own
    ///     step, never folded into a knob burst that was still open, and the next
    ///     edit opens a new step rather than joining this one.
    /// </summary>
    public void Committed(T now)
    {
        if (_settled is not null && !_same(_settled, now))
        {
            _undo.AddLast(_settled);
            if (_undo.Count > MaxDepth) _undo.RemoveFirst();
            _redo.Clear();
        }

        _settled = now;
        _lastEditMs = long.MinValue;
    }

    /// <summary>The state to go back to, or null; <paramref name="current" /> goes
    /// onto the redo stack.</summary>
    public T? Undo(T current)
    {
        if (_undo.Last is not { } last) return null;
        _undo.RemoveLast();
        _redo.Push(current);
        _settled = last.Value;
        _lastEditMs = long.MinValue;
        return last.Value;
    }

    /// <summary>The state to go forward to, or null; <paramref name="current" /> goes
    /// back onto the undo stack.</summary>
    public T? Redo(T current)
    {
        if (_redo.Count == 0) return null;
        var next = _redo.Pop();
        _undo.AddLast(current);
        _settled = next;
        _lastEditMs = long.MinValue;
        return next;
    }
}
