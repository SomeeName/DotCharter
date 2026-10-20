namespace DotCharter.Services.State;

/// <summary>
///     An action that can absorb the next action of the SAME continuous gesture,
///     so a wheel run, a pointer drag or a held key collapses into ONE undo node
///     instead of one node per tick.
///     <para>
///         <b>This is not an optimization — it is what makes Ctrl+Z mean
///         anything.</b> Measured over one real 32-minute charting session
///         223 of the 345 history nodes were single gestures:
///         one hand-shape resize alone pushed <b>111 consecutive
///         <c>MutateHandShapesAction</c> nodes</b> (15:43:47–15:44:08), and three
///         sustain adjustments pushed 39, 37 and 22. Undoing that resize meant
///         pressing Ctrl+Z 111 times. It also silently evicted real work:
///         <see cref="HistoryService.DefaultMaxNodes" /> is 500, so a two-hour
///         session spends its whole budget on wheel ticks and prunes the edits
///         the charter actually wants back.
///     </para>
///     <para>
///         <b>The merge is driven by <see cref="MergeVerb" />, never by type.</b>
///         Adjusting a sustain and re-fretting a chord are both a
///         <c>MutateChordsAction</c> over the same chord, and folding those two
///         together would make one Ctrl+Z revert an edit the user made
///         deliberately and separately. Two actions merge only when their verbs
///         are equal AND the absorbing action agrees it holds the same targets.
///     </para>
/// </summary>
public interface IMergeableAction : IChartAction
{
    /// <summary>
    ///     Names the gesture — "sustain", "handshape-edge", "anchor-drag".
    ///     Two actions may only merge when these are equal, so it must identify
    ///     the VERB and not the action type.
    ///     <para>
    ///         <b>Empty means "never merge", and that is the default.</b> A
    ///         generic action type (<c>MutateChordsAction</c> serves a dozen
    ///         different verbs) implements this interface but leaves the verb
    ///         empty unless its CALL SITE opted in, so merging is something a
    ///         gesture asks for rather than something every mutation inherits.
    ///     </para>
    /// </summary>
    string MergeVerb { get; }

    /// <summary>
    ///     Whether <paramref name="next" /> is the same gesture over the same
    ///     targets, and can therefore be folded in by <see cref="Absorb" />.
    ///     <para>
    ///         <b>PURE — it must not change anything.</b> The check is separate
    ///         from the fold precisely so a composite can ask all of its children
    ///         before any of them commits: a merge that gave up half way would
    ///         leave one child describing the gesture's start and its sibling the
    ///         gesture's end, and redoing that node would put the chart in a state
    ///         it was never in.
    ///     </para>
    ///     <para>
    ///         Answering false is always SAFE — it costs one undo node, never
    ///         correctness.
    ///     </para>
    /// </summary>
    bool CanAbsorb(IChartAction next);

    /// <summary>
    ///     Folds <paramref name="next" /> into this action so that Undo restores
    ///     the state from before THIS action and Execute reproduces the state
    ///     after <paramref name="next" />.
    ///     <para>
    ///         <paramref name="next" /> has ALREADY been applied to the chart by
    ///         the time this is called — the history service merges after the
    ///         fact, so absorbing must not touch the chart, only this action's own
    ///         record of it.
    ///     </para>
    ///     <para>
    ///         Only ever called when <see cref="CanAbsorb" /> just returned true
    ///         for the same argument.
    ///     </para>
    /// </summary>
    void Absorb(IChartAction next);
}
