namespace DotCharter.Services.Editing.Pen;

/// <summary>What one pen keystroke resolves to. The pen decides; the driver
/// (<c>ChartPenService</c>) is the only thing that touches the chart.</summary>
public enum PenVerb
{
    /// <summary>Nothing to commit.</summary>
    None,

    /// <summary>Toggle a string into or out of the sound at the pen: create a
    /// note, add the string to the chord already there, remove it, and delete the
    /// sound when the last string goes.</summary>
    ToggleString,

    /// <summary>Write the typed fret onto the MASKED strings of the target.</summary>
    SetFret,

    /// <summary>No mask: move the whole shape so its LOWEST fret lands on the
    /// typed number (rscharter's <c>moveTemplateFrets</c>).</summary>
    TransposeShape,

    /// <summary>Undo the open typing run — <c>Esc</c>.</summary>
    RevertTyping
}

/// <param name="Verb">What to do.</param>
/// <param name="StringIndex">The string, for <see cref="PenVerb.ToggleString" />.</param>
/// <param name="Fret">The accumulated fret, for the two fret verbs.</param>
/// <param name="ContinuesTypingRun">
///     True when this digit EXTENDS a value the pen already committed
///     (<c>1</c> then <c>2</c>): the driver must undo that node before
///     committing the new value, so the whole run stays ONE step on the undo
///     path. See <see cref="ChartPen" /> for why.
/// </param>
public readonly record struct PenDecision(PenVerb Verb, int StringIndex, int Fret, bool ContinuesTypingRun)
{
    public static PenDecision Nothing => new(PenVerb.None, -1, -1, false);
}

/// <summary>
///     The keyboard pen's state machine — EOF's <c>eof_pen_note</c>
///     (<c>editor.c:6320-6336</c>) as a pure object: what you are about to
///     commit is decided here and committed by the driver. Stage 2 of the
///     editing rework
///     (charting-workflow comparison
///     §8), behind <c>AppConfig.UseFastChartingInput</c>.
///     <para>
///         <b>Two states.</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Idle</b> — no run open. <c>1..9</c> ⇒
///             <see cref="PenVerb.ToggleString" />. A digit ⇒ open a run and
///             commit the fret. <c>Esc</c> ⇒ <see cref="PenVerb.None" />, so it
///             falls through to the shell's Escape chain (which clears the
///             selection) instead of being swallowed.
///         </item>
///         <item>
///             <b>Typing</b> — a run is open. A digit inside the window EXTENDS
///             the value (<c>1</c>,<c>2</c> ⇒ fret 12) and replaces the run's
///             node. <c>1..9</c> ends the run and toggles a string. <c>Esc</c> ⇒
///             <see cref="PenVerb.RevertTyping" />. A seek, a selection change or
///             any non-pen edit ends it through <see cref="EndTypingRun" />.
///         </item>
///     </list>
///     <para>
///         <b>Undo granularity — decided deliberately: one typing RUN is one
///         node, one string toggle is one node.</b> A run stays a single step
///         because a continuation digit reports
///         <see cref="PenDecision.ContinuesTypingRun" /> and the driver then
///         undoes the run's own node before committing the new value — so
///         <c>Ctrl+Z</c> after typing "12" lands on the fret you had BEFORE
///         typing, never on an intermediate "fret 1". The abandoned value
///         survives as a sibling branch in the undo TREE, which is exactly what
///         that tree is for (<c>HistoryService</c>: "undoing and then making a
///         new edit keeps the redo tail"). String toggles are
///         deliberately NOT coalesced: each is a discrete structural act
///         (create / add / remove / delete) that a charter expects to undo on its
///         own, and EOF records one undo state per lane keypress for the same
///         reason.
///     </para>
///     <para>
///         <b>ONE window governs both.</b> The same 2 s that decides whether a
///         digit extends the value also decides whether it replaces the run's
///         undo node. Type <c>1</c>, pause, type <c>2</c> and you made two
///         decisions: fret 2 replaces fret 1 in the chart and BOTH are on the
///         undo path.
///     </para>
///     <para>
///         <b>The run is anchored to a history node id.</b> The pen compares the
///         node it settled on against the CURRENT node before treating a digit as
///         a continuation, so a run something else has already built on top of —
///         an undo, a pointer edit, a paste — can never be reopened and can never
///         undo somebody else's work.
///     </para>
///     <para>
///         Pure by construction: no clock, no services, no chart. Every input is
///         a parameter — the same reason <c>GlobalChordGate</c> and
///         <c>ActivationKeyUpLatch</c> were split out of <c>MainWindow</c>.
///     </para>
/// </summary>
public sealed class ChartPen
{
    private FretDigitAccumulator _digits = FretDigitAccumulator.Empty;

    /// <summary>The history node the open run is anchored to: the node it
    /// committed, else the node it started from.</summary>
    private int _runAnchorNodeId;

    /// <summary>The node the open run committed, if it committed one. A refused
    /// transpose or an unchanged fret produces no node.</summary>
    private int? _runNodeId;

    private bool _runOpen;

    /// <summary>
    ///     True while a run has a committed node behind it. <c>Esc</c> also asks
    ///     whether that run is still inside the accumulator window — see
    ///     <see cref="PressEscape" /> — so this is the structural half only, and
    ///     that is why nothing OUTSIDE this type gates on it: a caller that drew
    ///     a "revert available" affordance from it would show one for a run whose
    ///     window has expired and that <see cref="PressEscape" /> will refuse.
    ///     <c>IChartPenService</c> carried exactly such a property
    ///     (<c>HasPendingEntry</c>) for four releases, resolved by nothing, its
    ///     doc claiming the shell's Escape chain asked it — the chain asks the
    ///     VERB (<c>RevertPendingEntry</c>), which answers and acts in one step.
    ///     This stays as the state machine's own observable state, which is what
    ///     <c>ChartPenTests</c> reads it as.
    /// </summary>
    public bool HasOpenTypingRun => _runOpen && _runNodeId.HasValue;

    /// <summary>The fret the accumulator currently holds (<c>-1</c> = none).
    /// For the status line and tests, not for editing decisions.</summary>
    public int PendingFret => _digits.PendingFret;

    /// <summary>
    ///     <c>1..9</c> — the note-entry verb. Ends any typing run first: a
    ///     structural change is a new gesture, and letting digits keep
    ///     accumulating across it would land the next fret on the wrong target.
    /// </summary>
    public PenDecision PressString(int stringIndex)
    {
        EndTypingRun();
        return new PenDecision(PenVerb.ToggleString, stringIndex, -1, false);
    }

    /// <summary>
    ///     A typed digit. <paramref name="currentHistoryNodeId" /> is
    ///     <c>IHistoryService.CurrentNodeId</c> at the keypress and
    ///     <paramref name="maskIsEmpty" /> is
    ///     <c>ProjectState.StringEditMask.IsEmpty</c> — both parameters so this
    ///     type stays free of the chart.
    ///     <para>
    ///         The driver MUST call <see cref="Settle" /> afterwards.
    ///     </para>
    /// </summary>
    public PenDecision PressDigit(int digit, long nowTicks, bool maskIsEmpty, int currentHistoryNodeId)
    {
        if (_runOpen && (currentHistoryNodeId != _runAnchorNodeId || !WithinWindow(nowTicks)))
            EndTypingRun();

        var continues = _runOpen && _runNodeId.HasValue;

        _digits = _digits.Push(digit, nowTicks, FretDigitAccumulator.PenWindowMs,
            FretDigitAccumulator.MaxTypedFret, out var fret);
        _runOpen = true;

        return new PenDecision(
            maskIsEmpty ? PenVerb.TransposeShape : PenVerb.SetFret,
            -1,
            fret,
            continues);
    }

    /// <summary>
    ///     <c>Esc</c>. Reverts an in-progress entry and reports whether it did —
    ///     the shell's Escape chain falls through to "clear the selection" when
    ///     it did not, so a pen with nothing pending never steals the key.
    ///     <para>
    ///         <b>"In progress" is bounded by the SAME window that decides
    ///         whether a digit extends the value</b> — the "ONE window governs
    ///         both" rule above, which this method did not honour. Nothing
    ///         outside the pen ever calls <see cref="EndTypingRun" />: a seek and
    ///         a selection change push no history node, so the run stayed open
    ///         and <c>_runNodeId</c> stayed current INDEFINITELY. Type a fret,
    ///         seek away, and the next <c>Esc</c> — overwhelmingly "clear the
    ///         selection" — silently undid that fret instead. Past the window the
    ///         run is over and <c>Esc</c> behaves exactly as it always has.
    ///     </para>
    /// </summary>
    public PenDecision PressEscape(long nowTicks, int currentHistoryNodeId)
    {
        var canRevert = _runOpen && _runNodeId == currentHistoryNodeId && WithinWindow(nowTicks);
        EndTypingRun();
        return canRevert ? new PenDecision(PenVerb.RevertTyping, -1, -1, false) : PenDecision.Nothing;
    }

    /// <summary>
    ///     The driver reports where the history ended up after committing a fret
    ///     verb. <paramref name="committed" /> is whether a node was actually
    ///     produced — a refused transpose or an unchanged fret produces none, and
    ///     the next digit must then not try to undo one.
    /// </summary>
    public void Settle(int currentHistoryNodeId, bool committed)
    {
        _runAnchorNodeId = currentHistoryNodeId;
        _runNodeId = committed ? currentHistoryNodeId : null;
    }

    /// <summary>Ends the run: no further digit extends the value and <c>Esc</c>
    /// has nothing to revert. Called for every seek, selection change and
    /// non-pen edit.</summary>
    public void EndTypingRun()
    {
        _runOpen = false;
        _runNodeId = null;
        _digits = _digits.Reset();
    }

    private bool WithinWindow(long nowTicks)
    {
        return _digits.IsAccumulating &&
               nowTicks - _digits.LastDigitTicks <= FretDigitAccumulator.PenWindowMs;
    }
}
