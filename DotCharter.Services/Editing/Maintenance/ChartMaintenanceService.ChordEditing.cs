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
        var templates = arrangement.ChordTemplates;
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
            // one) was silently re-fretted to the previous shape. Fixed twice before
            // in exactly this form: GlobalEditingCoordinator's orphan sweep and
            // RefretService.ApplyChordGroup.
            HandShape? containing = null;
            foreach (var hs in handShapes)
            {
                if (!hs.Covers(chord.Position)) continue;
                if (containing == null || hs.Position > containing.Position)
                    containing = hs;
            }
            if (containing == null || containing.TemplateId < 0) continue;

            var template = templates.FirstOrDefault(t => t.Id == containing.TemplateId);
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
        if (!_historyService.Do(action)) return;

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

        // Reuse the open phrase at the playhead if one exists; otherwise create
        // a new phrase wrapping the placeholder lyric.
        LyricPhrase? targetPhrase = null;
        foreach (var phrase in track.Phrases)
        {
            if (phrase.Position <= startPos && phrase.EndPosition >= startPos)
            {
                targetPhrase = phrase;
                break;
            }
        }

        var newLyric = new LyricEvent
        {
            Text = "+",
            IsWordPart = false,
            Position = startPos,
            EndPosition = endPos
        };

        if (targetPhrase == null)
        {
            var newPhrase = new LyricPhrase
            {
                Position = startPos,
                EndPosition = endPos
            };
            newPhrase.Lyrics.Add(newLyric);
            _historyService.Do(new AddElementsAction<LyricPhrase>(track.Phrases, new[] { newPhrase }, p => p.Position));
        }
        else
        {
            // ONE gesture is ONE undo node, so the phrase-end stretch the insert
            // forces travels in the SAME node as the lyric. The stretch must not be a
            // bare assignment after the add: Ctrl+Z would take the placeholder back out and
            // leave the phrase permanently longer, and because nothing on screen shows
            // a phrase end moving by a fraction of a beat, repeating the gesture would creep
            // the phrase forward invisibly, one insert at a time.
            var actions = new List<IChartAction>
            {
                new AddElementsAction<LyricEvent>(targetPhrase.Lyrics, new[] { newLyric }, l => l.Position)
            };
            // Extend the phrase end if the new lyric goes past it.
            if (targetPhrase.EndPosition < endPos)
                actions.Add(new ResizeLyricPhraseAction(targetPhrase, targetPhrase.EndPosition, endPos));

            var composite = actions.Count == 1 ? actions[0] : new CompositeAction(actions);
            _historyService.Do(composite);
        }

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
