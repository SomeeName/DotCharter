using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     Resolves what the "Apply to" ladder reaches for the panel: the selected sounds, the "this
///     chord" sounds, the scope an edit is resolved on and the sounds a template switch moves.
///     Read-only over the selection; owned by <see cref="ChordTemplateEditorViewModel" />.
/// </summary>
internal sealed class ChordApplyScopeController(
    ChartEditorViewModel editor,
    ChordTemplateEditPlanner edits,
    Func<ChordTemplate?> template,
    Func<ChordApplyRung> rung,
    Func<bool> syncsHandShapes)
{
    private readonly ChartEditorViewModel _editor = editor;
    private readonly ChordTemplateEditPlanner _edits = edits;
    private readonly Func<ChordTemplate?> _template = template;
    private readonly Func<ChordApplyRung> _rung = rung;
    private readonly Func<bool> _syncsHandShapes = syncsHandShapes;

    /// <summary>
    ///     The selection this panel edits, with each sound's companion hand shape
    ///     folded in — see <see cref="AppConfig.SyncHandShapeToChordTemplate" />.
    ///     The ladder's "this chord + its hand shape" and its counts state that
    ///     fold, so it is never invisible.
    ///     <para>
    ///         Every entry point below promotes a covering hand shape, not ONLY
    ///         when nothing at all is selected (a bare NOTE). Select
    ///         a chord — which is what selecting a chord in the highway does, and
    ///         the overwhelmingly common gesture — and the bar above it would be
    ///         left out of the target list, so a fingering edit that minted a private
    ///         template would leave that bar on the old one.
    ///     </para>
    /// </summary>
    internal ChordApplySounds SelectedSounds()
    {
        var chords = _editor.Selection.SelectedChords.ToList();
        var handShapes = _editor.Selection.SelectedHandShapes.ToList();

        // A bare note still finds its bar: with no chord and no shape selected,
        // the note IS the only handle the user has on the shape.
        if (chords.Count == 0 && handShapes.Count == 0)
        {
            handShapes.AddRange(SoundCompanions.CoveringHandShapes(
                _editor.Selection.SelectedNotes.Select(n => n.Position),
                _editor.HandShapes, handShapes));
        }

        // A bar whose template is not the shape of the strums under it is edited
        // as those strums: the panel shows what they play, and the edit reaches them.
        if (chords.Count == 0 && handShapes.Count > 0 && StrumOfMismatchedBar(handShapes) is { } strum)
            chords.Add(strum);

        if (chords.Count > 0 && SyncsHandShapes)
        {
            handShapes.AddRange(SoundCompanions.CoveringHandShapes(
                chords.Select(c => c.Position), _editor.HandShapes, handShapes));
        }

        return new ChordApplySounds(chords, handShapes);
    }

    private Chord? StrumOfMismatchedBar(IReadOnlyList<HandShape> handShapes)
    {
        var arr = _editor.ActiveArrangement;
        if (arr == null) return null;
        var first = handShapes[0];
        if (_editor.Selection.SelectedHandShapes.Contains(first) &&
            (first.GhostMask.HasValue || arr.FindChordTemplate(first.TemplateId) is { GhostMask: not 0 }))
        {
            return null;
        }

        foreach (var bar in handShapes)
        {
            var template = arr.FindChordTemplate(bar.TemplateId);
            if (template == null || template.IsArpeggio || bar.GetGhostMask(template) != 0) continue;
            var enclosed = _editor.PlacedChords.Where(c => bar.Covers(c.Position)).ToList();
            if (enclosed.Count == 0 || enclosed.Any(c => ChordTemplateEditPlanner.TemplateMatchesChord(template, c))) continue;
            if (enclosed[0].TemplateId >= 0 && (arr.FindChordTemplate(enclosed[0].TemplateId)?.IsArpeggio ?? false)) continue;
            return enclosed[0];
        }

        return null;
    }

    /// <summary>The ladder's "this chord": the chord the panel shows (the first
    /// selected) with its own bar when the sync is on; else the first hand shape.</summary>
    internal ChordApplySounds ThisSounds()
    {
        var selected = SelectedSounds();
        if (selected.Chords.Count == 0)
            return new ChordApplySounds([], selected.HandShapes.Take(1).ToList());

        var chord = selected.Chords[0];
        var handShapes = SyncsHandShapes
            ? SoundCompanions.CoveringHandShapes([chord.Position], _editor.HandShapes, []).ToList()
            : [];
        return new ChordApplySounds([chord], handShapes);
    }

    private bool SyncsHandShapes => _syncsHandShapes();


    internal ChordApplyScope? CurrentScope() =>
        _template() is { } template ? _edits.ResolveReach(template, ThisSounds(), SelectedSounds()) : null;


    /// <summary>The sounds a template switch moves: the ladder's rung, resolved
    /// for a switch (the "same grip" rung moves every use, never other grips).
    /// Empty with nothing selected.</summary>
    internal ChordApplySounds SwitchTargets()
    {
        var scope = CurrentScope();
        if (scope is not { HasSelection: true }) return ChordApplySounds.None;
        var targets = scope.Resolve(_rung(), ChordTemplateEditKind.Switch);
        return targets.Path == ChordApplyPath.Rebind ? targets.Sounds : ChordApplySounds.None;
    }

}
