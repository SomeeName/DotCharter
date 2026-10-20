namespace DotCharter.Services.Editing.Targets;

/// <summary>
///     The one place a keyboard editing verb asks "what am I acting on".
///     Wraps the pure <see cref="EditTargetResolver" /> with the ambient state
///     the ladder needs (the selection, the active level, where the pen is).
///     <para>
///         <b>This is the seam Stage 5's inspector should build its own target
///         on.</b> The inspector currently re-derives "selected chords, else
///         selected hand shapes, else the shape enclosing the first selected
///         note" in four copy-pasted blocks inside
///         <c>ChordTemplateEditorViewModel</c>; <see cref="Resolve" /> answers
///         exactly that question once, and <see cref="EditTarget.Source" />
///         tells the caller which facet to show. A read-only consumer such as
///         the inspector's display must use <see cref="Resolve" /> — the
///         promoting overloads write to the selection and belong to verbs.
///     </para>
/// </summary>
public interface IEditTargetService
{
    /// <summary>
    ///     Installs "where is the pen right now" — the bottom rung of the
    ///     ladder, for callers that have no grid divisor of their own. Same
    ///     idiom as <c>IEditingScope.SetResolver</c>: the snapped playhead
    ///     depends on the grid divisor, which lives in the UI's transport view
    ///     model, so the composition root supplies it rather than the service
    ///     layer reaching upward. Returning <c>null</c> (the default, when
    ///     nothing is installed) simply removes that rung.
    /// </summary>
    void SetPenPositionProvider(Func<FractionalPosition?> provider);

    /// <summary>
    ///     Resolves selection → hand shape → pen. Read-only: nothing is
    ///     selected, seeked or edited.
    ///     <para>
    ///         The two IMPLIED rungs (hand shape, pen) are gated on
    ///         <c>AppConfig.UseFastChartingInput</c>, so with the setting off
    ///         this is byte-for-byte the pre-v0.35 "act on the selection, else
    ///         do nothing".
    ///     </para>
    /// </summary>
    EditTarget Resolve();

    /// <summary>
    ///     <see cref="Resolve" />, then makes an implied target VISIBLE by
    ///     promoting it to a real selection — EOF's implied note selection
    ///     drawn on screen rather than left as invisible modal state.
    /// </summary>
    EditTarget ResolveAndPromote();

    /// <summary>
    ///     <see cref="ResolveAndPromote" /> with the pen rung taken at an
    ///     EXPLICIT position instead of the installed provider. The pen is
    ///     handed a grid divisor per keystroke and already snaps its own
    ///     position (grid, with a nearer sound winning), so it passes that
    ///     straight in rather than round-tripping through a global the
    ///     composition root has to have wired.
    /// </summary>
    EditTarget ResolveAndPromoteAt(FractionalPosition penPosition);
}
