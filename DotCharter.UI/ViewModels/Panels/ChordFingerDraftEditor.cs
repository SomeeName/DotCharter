using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     The buffered finger draft of the chord-template panel: fills it from the per-string rows,
///     the SetFinger keybinds and the chord box's hover-typing, and commits it as ONE history
///     node. Owned by <see cref="ChordTemplateEditorViewModel" />, which keeps the bindable
///     properties; this class holds the rules.
/// </summary>
internal sealed class ChordFingerDraftEditor(IChordFingerSurface surface)
{
    private readonly IChordFingerSurface _surface = surface;

    /// <summary>
    ///     The strings the loaded shape frets ABOVE the capo — the only strings a
    ///     fingering can be about. Empty (not null) when nothing is loaded, so an
    ///     "every string" edit onto no shape touches nothing rather than
    ///     everything.
    /// </summary>
    private HashSet<int> FrettedStringsOfLoadedShape()
    {
        var result = new HashSet<int>();
        if (_surface.Template == null) return result;
        var capo = _surface.Capo;
        foreach (var (stringIndex, fret) in _surface.Template.Frets)
        {
            if (fret > capo)
                result.Add(stringIndex);
        }

        return result;
    }

    internal void UpdateDraftFingersFromInputs()
    {
        if (_surface.Template == null) return;
        // Re-clone to force binding refresh on MiniFretboardControl (which
        // reads Template.Fingers eagerly and won't observe dict mutations).
        var draft = _surface.Template.Clone();
        foreach (var input in _surface.StringInputs)
        {
            var fingerId = FingerTextCodec.Parse(input.FingerText);
            if (fingerId >= 0) draft.Fingers[input.StringIndex] = fingerId;
            else draft.Fingers.Remove(input.StringIndex);
        }
        _surface.DraftTemplate = draft;
    }


    /// <summary>
    /// Bulk-set the fingering value (-1 = unspecified / 0 = thumb / 1..4 = fingers)
    /// on the named strings of the currently-loaded chord template, then commit
    /// just like the user clicked "Save fingerings". Used by the
    /// global SetFinger keybinds (Inspector → ChordTemplateEditor → here).
    /// If <paramref name="stringIndices"/> is empty the change is applied to
    /// every string in the template.
    /// </summary>
    internal bool SetFingerForStrings(int fingerId, IReadOnlyCollection<int>? stringIndices)
    {
        if (!SetFingerDraftForStrings(fingerId, stringIndices)) return false;
        return CommitPendingFingerEdits();
    }

    /// <summary>
    ///     Fills the finger DRAFT without committing — the buffered half of
    ///     <see cref="SetFingerForStrings" />, shared so the chord box's
    ///     hover-typing and the keybinds cannot drift apart. Returns whether
    ///     anything actually changed.
    /// </summary>
    internal bool SetFingerDraftForStrings(int fingerId, IReadOnlyCollection<int>? stringIndices)
    {
        if (_surface.Template == null) return false;

        // null means EVERY string; an EMPTY collection means NONE. They are not
        // the same value, and the caller produces an empty list for two
        // opposite answers - the string-edit mask being empty ("all strings",
        // the documented convention) and the mask being SET but intersecting
        // none of this chord's strings ("no strings"). Treating them alike
        // would invert the second and write the finger to every string of the chord.
        if (stringIndices is { Count: 0 }) return false;
        // "EVERY STRING" means every string the loaded SHAPE actually frets, not
        // every row on the panel. A 6-row panel over a three-string grip wrote
        // the finger onto the three strings the chord does not play, and onto
        // any OPEN one — the first is data no export can spell (RS-XML would
        // emit `fingerN` beside `fret="-1"`) and, because the L06/L07 rule walks
        // `Frets`, is invisible to the inspector that would otherwise report it;
        // the second IS L06. A caller that NAMES a string still gets exactly the
        // string it asked for.
        //
        // …but only when SETTING one. CLEARING ("no finger", fingerId < 0) has to
        // reach every row, because the strings that need repairing are exactly
        // the ones this narrowing excludes: a finger an older build left standing
        // on an open string is an L06 the panel reports and the keybind must be
        // able to take off. Setting is guarded twice over — here, and per row
        // below — so nothing writes onto a string the shape does not fret.
        var targetSet = stringIndices != null
            ? [.. stringIndices]
            : fingerId >= 0
                ? FrettedStringsOfLoadedShape()
                : null;
        if (targetSet is { Count: 0 }) return false;

        var changed = false;
        foreach (var input in _surface.StringInputs)
        {
            if (targetSet != null && !targetSet.Contains(input.StringIndex)) continue;
            // A finger only goes on a string the grip actually FRETS. A finger on
            // an open string is inspector rule L06 (FingerSetForOpenString) and
            // "open" is fret <= capo, not fret 0; a finger on a string the shape
            // does not play at all has nothing to hold down. ToggleNote,
            // SetHoveredFret and ClearString — the three shape-authoring gestures
            // — each strip it for exactly this reason, and so does
            // ChordTemplateFingerSetter.SetSuggestedFingers; the FINGER-authoring
            // gestures did not, so SetFinger1..4 (the keybinds, with no mask =
            // "every string of the chord") and the chord box's hover-typing minted
            // the very finding the panel's own Issues tab reports. CLEARING
            // (fingerId < 0) is always allowed — that is how an existing one is
            // repaired.
            if (fingerId > 0 && !IsFretted(input.StringIndex)) continue;
            var newText = fingerId < 0 ? string.Empty : FingerTextCodec.Format(fingerId);
            if (input.FingerText == newText) continue;
            input.FingerText = newText;
            changed = true;
        }

        if (!changed) return false;

        _surface.HasPendingFingerEdits = true;
        UpdateDraftFingersFromInputs();
        return true;
    }

    /// <summary>
    ///     Does the loaded shape hold this string down? Absent from
    ///     <c>Frets</c> means the shape does not play it; a fret at or below the
    ///     capo means it sounds OPEN. Either way there is no finger on it — the
    ///     same predicate <c>TemplateAndOverlapRule</c> uses to report L06.
    /// </summary>
    private bool IsFretted(int stringIndex) =>
        _surface.Template != null && _surface.Template.Frets.TryGetValue(stringIndex, out var fret) && fret > _surface.Capo;

    /// <summary>
    ///     Snaps an authored fret onto the frets the neck actually HAS —
    ///     <c>{0} ∪ [capo, 24]</c>, the same legal set every other fret-writing
    ///     verb lands in (<see cref="SoundingPitch.SnapToNeck" />).
    ///     <para>
    ///         The chord box's POINTER path could never produce an illegal fret
    ///         (<c>ChordBoxGeometry</c> floors its window at <c>capo + 1</c> and
    ///         its open rail returns the capo), so this reads like dead defence
    ///         until you notice that the two KEYBOARD paths bypass that geometry
    ///         entirely: hover-typing a digit builds a <c>ChordBoxFretEdit</c>
    ///         straight from the accumulator, and the per-row fret box parses
    ///         whatever was typed. Typing <c>3</c> on a capo-5 chart therefore
    ///         wrote fret 3 into a SHARED template — inspector rule <c>L01</c>
    ///         (<c>NoteFretBelowCapo</c>) on every chord bound to it, from a
    ///         gesture the charter cannot see is illegal. The fret box also took
    ///         any int at all: <c>99</c> and <c>-4</c> both landed verbatim on a
    ///         24-fret neck.
    ///     </para>
    ///     <para>
    ///         Direction is always <c>0</c> (absolute): every caller here is a
    ///         typed or clicked ABSOLUTE fret, so a sub-capo value lands ON the
    ///         capo — the answer <c>ArrangementAutoFixService.FixCapoFrets</c>
    ///         gives. Only a fret-DOWN run may fall through to open, and the
    ///         panel has no such verb.
    ///     </para>
    /// </summary>
    internal int SnapFretToNeck(int fret) => SoundingPitch.SnapToNeck(fret, Math.Max(0, _surface.Capo));

    internal bool CommitPendingFingerEdits()
    {
        if (_surface.Template == null || !_surface.CanSaveFingering) return false;

        var newState = _surface.Template.Clone();

        foreach (var input in _surface.StringInputs)
        {
            var fingerId = FingerTextCodec.Parse(input.FingerText);
            // A finger only exists on a string the shape DECLARES. The per-row
            // finger strip is bound straight to `FingerText`, so it reaches this
            // without passing SetFingerDraftForStrings — and a finger on a
            // string with no fret is unexportable junk the L06/L07 rule cannot
            // see, because that rule walks `Frets`.
            if (fingerId >= 0 && newState.Frets.ContainsKey(input.StringIndex))
                newState.Fingers[input.StringIndex] = fingerId;
            else newState.Fingers.Remove(input.StringIndex);
        }

        // ONE undo node for the gesture, the "same grip" templates included
        // when that rung is lit (editing-invariants.md).
        if (!_surface.ApplyTemplateEdit(newState, ChordTemplateEditKind.Fingering)) return false;

        _surface.HasPendingFingerEdits = false;
        _surface.DraftTemplate = _surface.Template?.Clone();
        return true;
    }

}
