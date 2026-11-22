// ChartMaintenanceService: chord editing - hand-shape templates, a vocal at the playhead, and the forwards to ChordConversionCommands.
using DotCharter.Services.State;
namespace DotCharter.Services.Editing.Maintenance;

// Chord-editing surface: hand-shape template application, vocal insertion at the playhead,
// and the forwards to ChordConversionCommands (notes to chords and back, flags, fingering).
public sealed partial class ChartMaintenanceService
{
    /// <summary>
    /// For each selected chord, finds the hand-shape that contains it and
    /// re-points the chord at that hand-shape's chord-template (rebuilding
    /// the chord's notes from the template fret pattern). Mirrors RsCharter's
    /// <c>SET_HAND_SHAPE_TEMPLATE_ON_CHORDS</c> action — useful when the user
    /// has reshaped a hand-shape and wants the chords inside it to follow.
    /// </summary>
    public void ApplyHandShapeTemplateToSelectedChords()
    {
        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        if (arrangement == null || level == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoActiveLevel");
            return;
        }

        var selectedChords = _chartEditor.SelectedChords.ToList();
        if (selectedChords.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_ApplyHandShapeTemplate_NoChords");
            return;
        }

        var handShapes = level.HandShapes;
        var targets = new List<Chord>();
        var newStates = new List<Chord>();

        foreach (var chord in selectedChords)
        {
            // Find the hand-shape that contains this chord — latest start wins when
            // shapes overlap. Containment is the ONE shared span rule
            // HandShape.Covers: start inclusive, end EXCLUSIVE. This open-coded a
            // CLOSED span, and shapes ABUT by construction (every tab importer emits
            // A=[0,4) B=[4,8)), so a chord sitting exactly ON a shape's end counted
            // as inside it — and because Chord.ApplyTemplate rewrites ChordNotes from
            // the template's frets, a chord that belongs to NO shape (or to the next
            // one) was silently re-fretted to the previous shape.
            HandShape? containing = null;
            foreach (var hs in handShapes)
            {
                if (!hs.Covers(chord.Position)) continue;
                if (containing == null || hs.Position > containing.Position)
                    containing = hs;
            }
            if (containing == null || containing.TemplateId < 0) continue;

            var template = arrangement.FindChordTemplate(containing.TemplateId);
            if (template == null) continue;
            if (chord.TemplateId == template.Id) continue;

            var newChord = chord.Clone();
            newChord.ApplyTemplate(template);
            targets.Add(chord);
            newStates.Add(newChord);
        }

        if (targets.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_ApplyHandShapeTemplate_NoMatches");
            return;
        }

        var action = new SwitchTemplateAction(targets, newStates,
            [], [], _chartEditor);
        if (!_historyService.Do(ChordShapes.StrandedShapes.Wrap(arrangement, action))) return;

        _chartEditor.RequestRedraw();
        _projectState.MainDisplayMessage = _localization.Plural("Status_ApplyHandShapeTemplate_DoneOne", "Status_ApplyHandShapeTemplate_Done", targets.Count);
        _logger.Info(AppLogCategory.Editing,
            $"Applied hand-shape template to {targets.Count} chord(s) on arrangement '{arrangement.Name}'.");
    }

    /// <summary>
    /// Inserts a placeholder lyric ("+") at the playhead in the active vocal track.
    /// Mirrors RsCharter's <c>INSERT_VOCAL</c> action — used for quick-blocking out
    /// vocal positions before dropping in the actual lyric text.
    /// </summary>
    public void InsertVocalAtPlayhead()
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }
        var beats = project.Beats;
        if (beats.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_TempoMapOperationNoChange");
            return;
        }

        var track = _chartEditor.ActiveVocalTrack ?? project.VocalTracks.FirstOrDefault();
        if (track == null)
        {
            _projectState.MainDisplayMessage = L("Status_InsertVocal_NoTrack");
            return;
        }

        var tapMs = _projectState.CurrentTimeMs;
        var startPos = TimelineMath.PositionFromMs(tapMs, beats);
        var endPos = TimelineMath.PositionFromMs(tapMs + 250.0, beats);

        _historyService.Do(VocalInsertion.Create(track, startPos, endPos));

        _projectState.MainDisplayMessage = L("Status_VocalInserted", $"{tapMs:0} ms");
        _chartEditor.RequestRedraw();
        _logger.Info(AppLogCategory.Editing, $"Inserted placeholder lyric at {tapMs:F0} ms in track '{track.Name}'.");
    }

    public void ToggleSelectedChordSplit() => ToggleChordFlag(c => c.SplitIntoNotes = !c.SplitIntoNotes, "split-into-notes");

    public void ToggleSelectedChordOnlyBox() => ToggleChordFlag(c => c.ForceNoNotes = !c.ForceNoNotes, "force-no-notes (only-box)");

    public void ToggleSelectedChordPassOtherNotes() => ToggleChordFlag(c => c.PassOtherNotes = !c.PassOtherNotes, "pass-other-notes");

    public void ToggleTwoFretOctaveFingering() => _chordConversion.ToggleTwoFretOctaveFingering();

    public void TogglePowerChordFingering() => _chordConversion.TogglePowerChordFingering();

    public void ConvertMutedStrumsToChords() => _chordConversion.ConvertMutedStrumsToChords();

    public void ConvertSelectedNotesToPowerChord(bool big) => _chordConversion.ConvertSelectedNotesToPowerChord(big);

    public void MergeSelectedNotesIntoChords() => _chordConversion.MergeSelectedNotesIntoChords();

    public void SplitSelectedChordsIntoNotes() => _chordConversion.SplitSelectedChordsIntoNotes();

    private void ToggleChordFlag(Action<Chord> mutate, string label) => _chordConversion.ToggleChordFlag(mutate, label);

    // the ghost-gem verbs live in their own collaborator (GhostGemCommands).
    public void ToggleGhostGems() =>
        ChordShapes.GhostGemCommands.Toggle(_projectState, _chartEditor, _historyService, _localization, _logger);

    public void MakeTappedHarmonic() =>
        ChordShapes.GhostGemCommands.TappedHarmonic(_projectState, _chartEditor, _historyService, _localization, _logger,
            _configService.LoadAppConfig().TappedHarmonicFretOffset);
}
