using DotCharter.UI.Controls;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     The chord box's pointer and keyboard gestures: toggling a note, hover-typed frets and
///     fingers, clearing a string. Every one lands through the surface's single write path, so
///     nothing here is a second editing route. Owned by <see cref="ChordTemplateEditorViewModel" />.
/// </summary>
internal sealed class ChordBoxEditHandler(
    IChordEditSurface surface,
    ChordFingerDraftEditor fingers,
    IHistoryService historyService,
    Action<ChordTemplate> restoreOrigin)
{
    private readonly IChordEditSurface _surface = surface;
    private readonly ChordFingerDraftEditor _fingers = fingers;
    private readonly IHistoryService _historyService = historyService;

    /// <summary>Puts a shape back on screen (when the arrangement still holds it) after a typing
    /// run's own node was undone.</summary>
    private readonly Action<ChordTemplate> _restoreOrigin = restoreOrigin;

    internal void ToggleNote((int stringIdx, int fret) pos)
    {
        if (_surface.Template == null || _surface.IsUpdatingUi) return;

        var newState = _surface.Template.Clone();
        var fret = _fingers.SnapFretToNeck(pos.fret);

        if (newState.Frets.TryGetValue(pos.stringIdx, out var existingFret) && existingFret == fret)
        {
            newState.Frets.Remove(pos.stringIdx);
            newState.Fingers.Remove(pos.stringIdx);
        }
        else
        {
            newState.Frets[pos.stringIdx] = fret;
            // A finger on an OPEN string is inspector rule L06
            // (FingerSetForOpenString), and "open" is fret <= capo, not fret 0
            // (frets are absolute; Core's capo invariant). Clicking an open
            // string does not stamp finger 1 on it, since the primary shape-authoring
            // gesture would otherwise mint the exact finding the panel's own Issues tab
            // reports — the same "never invent a finding the editor never
            // showed" rule the phrase-anchor fixer is held to.
            // ChordTemplateFingerSetter.SetSuggestedFingers strips open-string
            // fingers for this reason; do not diverge from it.
            if (fret > _surface.Capo) newState.Fingers[pos.stringIdx] = 1;
            else newState.Fingers.Remove(pos.stringIdx);
        }

        _surface.ApplyTemplateEdit(newState);
    }

    // ---- chord-box hover gestures (0.36) ----------------------------------
    //
    // Typing over a hovered string on the chord box. These reuse the existing
    // write paths verbatim — ApplyTemplateEdit for the shape, the buffered
    // finger draft for fingering — so nothing here is a second editing route.

    /// <summary>History node the open fret-typing run produced. A run is only
    /// reopened while it is still the CURRENT state, so a digit can never undo
    /// somebody else's work (the anchoring rule the keyboard pen uses).</summary>
    private int _fretRunNodeId = -1;

    /// <summary>The shape the open fret-typing run started from, so a continuation digit extends
    /// THAT shape rather than the one the previous digit landed on.</summary>
    private ChordTemplate? _fretRunOrigin;

    /// <summary>
    ///     Hover-typed fret. ONE typing run is ONE history node: a continuation
    ///     digit undoes the run's own node before committing the extended value,
    ///     so <c>Ctrl+Z</c> after typing "12" lands on the fret you had BEFORE
    ///     the run, never on an intermediate "fret 1". That is exactly the
    ///     granularity <c>ChartPenService</c> already applies to typed frets,
    ///     and it is what stops the per-keystroke reshape the audit found.
    /// </summary>
    internal void SetHoveredFret(ChordBoxFretEdit edit)
    {
        if (_surface.Template == null || _surface.IsUpdatingUi) return;

        if (edit.ContinuesRun && _fretRunNodeId >= 0 && _historyService.CurrentNodeId == _fretRunNodeId &&
            _historyService.CanUndo)
        {
            _historyService.Undo();
            ReloadFretRunOrigin();
        }
        else
        {
            _fretRunOrigin = _surface.Template;
        }

        var newState = _surface.Template.Clone();
        var fret = _fingers.SnapFretToNeck(edit.Fret);
        newState.Frets[edit.StringIndex] = fret;
        // Typing the string down to an OPEN fret leaves whatever finger was on
        // it standing on an open string — inspector rule L06. Same rule as
        // ToggleNote; the two are the only shape-authoring gestures.
        if (fret <= _surface.Capo) newState.Fingers.Remove(edit.StringIndex);
        var before = _historyService.CurrentNodeId;
        _surface.ApplyTemplateEdit(newState);
        // A REFUSED digit pushed nothing: the current node is somebody else's
        // work, and anchoring the run on it let the next digit undo it.
        _fretRunNodeId = _historyService.CurrentNodeId != before ? _historyService.CurrentNodeId : -1;
    }

    /// <summary>
    ///     Puts the shape the run started from back on screen after the run's own node is undone.
    ///     A first digit that matched an existing shape joined it, and the undo leaves that shape
    ///     loaded while the chord is back on its old one: the ladder then drops the chord from the
    ///     edit (it does not carry the shown shape) and the next digit is refused.
    /// </summary>
    private void ReloadFretRunOrigin()
    {
        if (_fretRunOrigin is { } origin) _restoreOrigin(origin);
    }

    /// <summary>
    ///     Hover-typed finger. Fills the DRAFT only — the buffered path the
    ///     panel has always used — so the user still presses Save to commit and
    ///     propagate. What changes is that the draft is now visible on the chord
    ///     box instead of expressed as a disabled button somewhere else.
    /// </summary>
    internal void SetHoveredFinger(ChordBoxFingerEdit edit)
    {
        if (_surface.Template == null || _surface.IsUpdatingUi) return;
        _fingers.SetFingerDraftForStrings(edit.Finger, new[] { edit.StringIndex });
    }

    /// <summary>Remove a string from the shape (hover + <c>Del</c>, or a
    /// right-click). Its fingering goes with it — a finger on an unused string
    /// is inspector rule L06.</summary>
    internal void ClearString(int stringIdx)
    {
        if (_surface.Template == null || _surface.IsUpdatingUi) return;
        if (!_surface.Template.Frets.ContainsKey(stringIdx)) return;

        var newState = _surface.Template.Clone();
        newState.Frets.Remove(stringIdx);
        newState.Fingers.Remove(stringIdx);
        _fretRunNodeId = -1;
        _surface.ApplyTemplateEdit(newState);
    }

}
