namespace DotCharter.Services.State;

/// <summary>
///     Collects the post-execute passes a composite's children would each have
///     run, so the composite runs each DISTINCT one exactly once at the end.
///     <para>
///         <b>Why.</b> Every <see cref="CollectionActionBase{T}" /> that carries an
///         <c>onPostExecute</c> runs it inside its own Execute and Undo — and that
///         delegate is the whole-level spacing enforcer (<c>ChartSpacingEnforcer
///         .EnforceLevel</c>: fill an n-entry array, sort it, sweep it, then the
///         hand-shape pass — measured 47 ms on a 20,000-sound level). A
///         <see cref="CompositeAction" /> just looped its children, so one
///         drag-drop (up to six effective children) paid the pass six times and
///         Ctrl+Z six more, a plain Delete three or four: ~280 ms for a gesture
///         whose budget is 150. The pass is idempotent — it only ever shortens —
///         so passes 2..k re-derived an answer the level already satisfied.
///     </para>
///     <para>
///         Thread-static and nesting-aware like <see cref="SpacingCollateralRecorder" />:
///         <see cref="BeginIfNone" /> opens a scope only when none is open, so a
///         composite inside a composite defers into the OUTER one and the gesture
///         still pays once. While a scope is open, <c>ExecutePostPass</c> /
///         <c>UndoPostPass</c> hand their delegate and their <c>owned</c> set here
///         instead of running; the composite then runs the passes under ONE
///         collateral recorder whose exclusion set is the union of every child's,
///         and holds that collateral itself (its children hold none). On undo
///         NO pass runs: inverses + that record restore the exact pre-edit chart,
///         and a pass there could only re-shorten it (<c>CompositeAction.Undo</c>).
///         Pinned by <c>SpacingPostPassCoalescingTests</c>.
///     </para>
/// </summary>
internal sealed class DeferredPostPassScope : IDisposable
{
    [ThreadStatic] private static DeferredPostPassScope? _current;

    private readonly List<object> _owned = [];
    private readonly List<Action> _passes = [];

    private DeferredPostPassScope() { }

    /// <summary>The open scope on this thread, if any — the deferral target.</summary>
    public static DeferredPostPassScope? Current => _current;

    /// <summary>How many distinct passes have been deferred — the test seam.</summary>
    public int PassCount => _passes.Count;

    /// <summary>
    ///     Opens a scope unless one is already open, in which case the caller is
    ///     nested and gets <c>null</c>: its children defer into the outer scope and
    ///     the outermost composite runs the passes.
    /// </summary>
    public static DeferredPostPassScope? BeginIfNone()
    {
        if (_current != null) return null;
        return _current = new DeferredPostPassScope();
    }

    /// <summary>
    ///     Records a pass to run at the end of the scope. Delegate equality
    ///     (target + method) is what dedupes it, so the same enforcer handed to
    ///     six children counts once; a genuinely different pass counts too.
    /// </summary>
    public void Defer(Action pass, IEnumerable<object> owned)
    {
        if (!_passes.Contains(pass)) _passes.Add(pass);
        _owned.AddRange(owned);
    }

    /// <summary>Runs every deferred pass once under one collateral recorder and
    /// returns what it changed outside the union of the children's owned sets;
    /// <c>null</c> when nothing was deferred.</summary>
    public SpacingCollateral? RunOnceRecordingCollateral()
    {
        if (_passes.Count == 0) return null;
        using var recorder = SpacingCollateralRecorder.Begin();
        for (var i = 0; i < _passes.Count; i++) _passes[i]();
        return recorder.Build(_owned);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
