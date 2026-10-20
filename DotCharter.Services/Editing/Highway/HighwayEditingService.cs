using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.State;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.Services.Editing.Highway;

public class HighwayEditingService : IHighwayEditingService
{
    /// <summary>Neck ceiling, matching the model's 24-fret bound.</summary>
    private const int MaxAnchorFret = 24;

    /// <summary>
    ///     Hand-span bounds for a nudge. 4 is Rocksmith's neutral span (the
    ///     finger derivation subtracts <c>width - 4</c>), and the range is the one
    ///     Log Charter's extendable zones use across the neck.
    /// </summary>
    private const int MinAnchorWidth = 3;

    private const int MaxAnchorWidth = 7;

    private readonly IStrictCollisionService _collisionService;
    private readonly IConfigurationService _configService;
    private readonly IHistoryService _historyManager;
    private readonly IAppLogger _logger;
    private readonly ProjectState _projectState;
    private readonly IEditingScope _scope;
    private readonly ISelectionService _selectionManager;
    private readonly IEditTargetService _targets;

    public HighwayEditingService(
        ProjectState projectState,
        ISelectionService selectionManager,
        IHistoryService historyManager,
        IStrictCollisionService collisionService,
        IConfigurationService configService,
        IEditingScope scope,
        IEditTargetService targets,
        IAppLogger logger)
    {
        _projectState = projectState;
        _selectionManager = selectionManager;
        _historyManager = historyManager;
        _collisionService = collisionService;
        _configService = configService;
        _scope = scope;
        _targets = targets;
        _logger = logger;
    }

    private Action? _linkNextUpdater;

    /// <summary>
    ///     The whole-level spacing pass every collection action carries. ONE
    ///     delegate per service — it reads the scope's level at invocation, so
    ///     caching changes nothing semantically, and it lets a composite's
    ///     <see cref="DotCharter.Services.State.DeferredPostPassScope" /> dedupe
    ///     the six copies a drag-drop hands out into one pass per gesture.
    /// </summary>
    public Action GetLinkNextUpdater()
    {
        return _linkNextUpdater ??= () =>
        {
            ChartSpacingEnforcer.EnforceLevel(_scope.Level, _scope.Beats, _configService.LoadAppConfig());
            _logger.Trace(AppLogCategory.Editing,
                "Active level spacing/link-next enforcement completed after chart action.");
        };
    }

    public Action GetLevelSpacingUpdater(ChartLevel level) =>
        () => ChartSpacingEnforcer.EnforceLevel(level, _scope.Beats, _configService.LoadAppConfig());

    public void HandleRightClickChord(Chord hitChord, int stringIndex)
    {
        _logger.Info(AppLogCategory.Editing,
            $"Right-click chord edit requested at string={stringIndex}. Target before edit: {FormatChord(hitChord)}.");
        var command = new EditChordStringCommand(
            _scope.Notes,
            _scope.Chords,
            _scope.Arrangement,
            hitChord,
            stringIndex,
            GetLinkNextUpdater());

        if (!ExecuteHighwayCommand(command)) return;

        switch (command.Outcome)
        {
            case EditChordStringOutcome.CollapsedChordToNote when command.CollapsedNote != null:
                _logger.Info(AppLogCategory.Editing,
                    $"Removed chord note on string={stringIndex}; chord collapsed into standalone {FormatNote(command.CollapsedNote)}.");
                _selectionManager.SelectedChords.Remove(hitChord);
                break;
            case EditChordStringOutcome.RemovedChordNote:
                _logger.Info(AppLogCategory.Editing,
                    $"Removed chord note on string={stringIndex}. New chord state: {FormatChord(command.NewChordState ?? hitChord)}.");
                break;
            case EditChordStringOutcome.AddedChordNote:
                _logger.Info(AppLogCategory.Editing,
                    $"Added chord note on string={stringIndex}. New chord state: {FormatChord(command.NewChordState ?? hitChord)}.");
                break;
        }
    }

    public void RemoveNote(GuitarNote hitNote)
    {
        _selectionManager.SelectedNotes.Remove(hitNote);
        _historyManager.Do(new RemoveNotesAction(_scope.Notes, new[] { hitNote }, GetLinkNextUpdater()));
        _logger.Info(AppLogCategory.Editing, $"Removed {FormatNote(hitNote)}.");
    }

    public void ApplyPlacement(FractionalPosition targetPos, int stringIndex)
    {
        var previewFret = GetPreviewFret();
        _logger.Info(AppLogCategory.Editing,
            $"Note placement requested at string={stringIndex}, fret={previewFret}, position={FormatPosition(targetPos)}.");

        var command = new ApplyNotePlacementCommand(
            _scope.Notes,
            _scope.Chords,
            _collisionService,
            _scope.Arrangement,
            targetPos,
            stringIndex,
            previewFret,
            GetLinkNextUpdater());

        if (ExecuteHighwayCommand(command))
        {
            _logger.Info(AppLogCategory.Editing,
                $"Note placement committed at string={stringIndex}, fret={previewFret}, position={FormatPosition(targetPos)}. ExistingChord={command.ExistingChordFound}, existingNotes={command.ExistingNotesCount}, exactNoteRemoved={command.ExactNoteRemoved}, actions={command.ActionCount}.");
        }
        else
        {
            _logger.Trace(AppLogCategory.Editing,
                $"Note placement produced no action at string={stringIndex}, position={FormatPosition(targetPos)}.");
        }

        _selectionManager.SelectedNotes.Clear();
        _selectionManager.SelectedChords.Clear();
    }

    public void RemoveHandShape(HandShape handShape)
    {
        // Deselect first, exactly as RemoveNote does: a deleted bar left in
        // SelectedHandShapes still CLAIMS the bare wheel notch
        // (TimelineWheelAdjuster.TryAdjustHandShapeLength reports "not targeted"
        // only when the selection AND the hover are empty), so the notch is
        // applied to a detached object instead of falling through the adjust
        // chain — and the rung's grip rebuild can mint a ChordTemplate for a bar
        // that does not exist.
        _selectionManager.SelectedHandShapes.Remove(handShape);
        if (ReferenceEquals(_selectionManager.HoveredHandShape, handShape))
            _selectionManager.HoveredHandShape = null;
        _historyManager.Do(new RemoveHandShapesAction(_scope.HandShapes, new[] { handShape },
            GetLinkNextUpdater()));
        _logger.Info(AppLogCategory.Editing, $"Removed {FormatHandShape(handShape)}.");
    }

    public void AddHandShape(HandShape hs, IEnumerable<ChordTemplate>? mintedTemplates = null)
    {
        _logger.Info(AppLogCategory.Editing, $"Adding new {FormatHandShape(hs)}.");
        CommitHandShapeDragAndDrop(
            [], [],
            [], [],
            [], [hs],
            [], [],
            mintedTemplates: mintedTemplates
        );
    }

    public bool CommitHandShapeDragAndDrop(
        List<GuitarNote> targetNotes, List<GuitarNote> newNotes,
        List<Chord> targetChords, List<Chord> newChords,
        List<HandShape> targetHandShapes, List<HandShape> newHandShapes,
        List<FretAnchor> targetAnchors, List<FretAnchor> newAnchors,
        IEnumerable<IChartAction>? additionalActions = null,
        IReadOnlyDictionary<int, ChordTemplate>? incomingTemplates = null,
        IEnumerable<ChordTemplate>? mintedTemplates = null)
    {
        var change = new HighwayDragDropChange(
            targetNotes, newNotes,
            targetChords, newChords,
            targetHandShapes, newHandShapes,
            targetAnchors, newAnchors);
        var command = new CommitHighwayDragDropCommand(
            _scope,
            _collisionService,
            change,
            additionalActions,
            GetLinkNextUpdater(),
            _logger,
            FormatPosition,
            FormatChord,
            incomingTemplates,
            mintedTemplates);

        if (!ExecuteHighwayCommand(command))
        {
            if (!command.WasStatePreserving)
                _logger.Trace(AppLogCategory.Editing, "Drag/drop commit produced no history action.");
            return false;
        }

        _logger.Info(AppLogCategory.Editing,
            $"Drag/drop committed with {command.ActionCount} history action(s). Added notes={command.NewNotes.Count}, added chords={command.NewChords.Count}, added handshapes={command.NewHandShapes.Count}, added anchors={command.NewAnchors.Count}, removed notes={command.RemovedNotesCount}, removed chords={command.RemovedChordsCount}, overridden by hand shapes={command.OverriddenSoundCount}, overridden anchors={command.OverriddenAnchorCount}.");
        LogMovedSounds(command.TargetNotes, command.RequestedNewNotes, command.TargetChords,
            command.RequestedNewChords, command.NewNotes, command.NewChords);
        for (var i = 0; i < command.NewHandShapes.Count && i < LogDetailCap; i++)
            _logger.Info(AppLogCategory.Editing, $"Result {FormatHandShape(command.NewHandShapes[i])}.");
        LogElision("hand shape result", command.NewHandShapes.Count);
        for (var i = 0; i < command.NewAnchors.Count && i < LogDetailCap; i++)
            _logger.Info(AppLogCategory.Editing, $"Result {FormatAnchor(command.NewAnchors[i])}.");
        LogElision("anchor result", command.NewAnchors.Count);

        _selectionManager.ClearSelections();

        // ONE notification per collection, not one per sound. Every Selected*
        // collection has a handler that re-reads the WHOLE selection — the
        // service re-derives its scalars and raises SelectionChanged, and the
        // inspector answers with seven full scans — so adding a dragged phrase
        // back one item at a time is quadratic. That is what SelectionBulkOps
        // exists for, and it says in its own summary that the loop shape "has
        // been reintroduced three times"; this was the fourth.
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedNotes, command.NewNotes);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedChords, command.NewChords);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedHandShapes, command.NewHandShapes);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedAnchors, command.NewAnchors);
        return true;
    }

    /// <summary>
    ///     What every keyboard mutation verb acts on — notes AND chords,
    ///     filtered by the persistent <see cref="StringEditMask" />.
    ///     <para>
    ///         The sounds come from the ONE ladder
    ///         (<see cref="IEditTargetService" />: selection → hand shape →
    ///         pen), so a verb does not decide for itself what "the target"
    ///         means — that inconsistency (the pen resolved its own implied
    ///         target while every other verb was selection-only) is what
    ///         Stage 3 removes. The implied rungs are promoted to a real
    ///         selection so the edit is visible, and are inert while
    ///         <c>AppConfig.UseFastChartingInput</c> is off.
    ///     </para>
    ///     <para>
    ///         The lists the ladder returns are already snapshots — load-bearing,
    ///         because the actions capture the instances and the spacing
    ///         enforcer running in their post-execute can mutate the live
    ///         selection collections.
    ///     </para>
    /// </summary>
    private SelectedSoundMutationTargets MutationTargets() => MutationTargets(_targets.ResolveAndPromote());

    private SelectedSoundMutationTargets MutationTargets(EditTarget target)
    {
        return new SelectedSoundMutationTargets(
            _scope.Notes,
            _scope.Chords,
            target.Notes,
            target.Chords,
            _scope.Arrangement,
            _projectState.StringEditMask,
            GetLinkNextUpdater());
    }

    public void MoveSelectedStringUp(int maxStrings)
    {
        RunMutation(t => SelectedNoteMutationCommands.MoveStringUp(t, maxStrings),
            $"moved one string up (maxStrings={maxStrings})");
    }

    public void MoveSelectedStringDown() => RunMutation(SelectedNoteMutationCommands.MoveStringDown, "moved one string down");

    public void MoveSelectedStringPreservingPitch(int delta, int maxStrings)
    {
        RunMutation(t => SelectedNoteMutationCommands.MoveStringPreservingPitch(t, delta, maxStrings),
            $"moved {(delta > 0 ? "one string up" : "one string down")} at the same pitch (maxStrings={maxStrings})");
    }

    public void MoveSelectedFretUp() => RunMutation(SelectedNoteMutationCommands.MoveFretUp, "fret +1");

    public void MoveSelectedFretDown() => RunMutation(SelectedNoteMutationCommands.MoveFretDown, "fret -1");

    public void MoveSelectedFretByOctave(int direction)
    {
        if (direction == 0) return;
        var delta = Math.Sign(direction) * 12;
        RunMutation(t => SelectedNoteMutationCommands.MoveFretBy(t, delta), $"fret {delta:+0;-0} (octave)");
    }

    public void SetSelectedFret(int fret) => RunMutation(t => SelectedNoteMutationCommands.SetFret(t, fret), $"set fret={fret}");

    public void ToggleSelectedHarmonic() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleHarmonic, "toggled harmonic");

    public void ToggleSelectedHopo() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleHopo, "toggled HOPO");

    public void ToggleSelectedMute() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleMute, "toggled mute");

    public void ToggleSelectedLinkNext() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleLinkNext, "toggled Link Next");

    public void ToggleSelectedVibrato() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleVibrato, "toggled vibrato");

    public void ToggleSelectedTremolo() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleTremolo, "toggled tremolo");

    public void ToggleSelectedPicking() => RunTechniqueMutation(SelectedNoteMutationCommands.TogglePicking, "toggled bass picking");

    public void ToggleSelectedAccent() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleAccent, "toggled accent");

    public void ToggleSelectedIgnore() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleIgnore, "toggled ignore");

    public void ToggleSelectedPassOtherNotes() => RunTechniqueMutation(SelectedNoteMutationCommands.TogglePassOtherNotes, "toggled pass-other-notes");

    public void ToggleSelectedHarmonicGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleHarmonicGroup, "group-toggled harmonic");
    public void ToggleSelectedHopoGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleHopoGroup, "group-toggled HOPO");
    public void ToggleSelectedMuteGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleMuteGroup, "group-toggled mute");
    public void ToggleSelectedLinkNextGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleLinkNextGroup, "group-toggled Link Next");
    public void ToggleSelectedVibratoGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleVibratoGroup, "group-toggled vibrato");
    public void ToggleSelectedTremoloGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleTremoloGroup, "group-toggled tremolo");
    public void ToggleSelectedPickingGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.TogglePickingGroup, "group-toggled bass picking");
    public void ToggleSelectedAccentGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleAccentGroup, "group-toggled accent");
    public void ToggleSelectedIgnoreGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.ToggleIgnoreGroup, "group-toggled ignore");
    public void ToggleSelectedPassOtherNotesGroup() => RunTechniqueMutation(SelectedNoteMutationCommands.TogglePassOtherNotesGroup, "group-toggled pass-other-notes");

    public IChordTechniqueReach? TechniqueReach { get; set; }

    private void RunMutation(Func<SelectedSoundMutationTargets, IHighwayEditCommand> factory, string label)
    {
        var targets = MutationTargets();
        if (!ExecuteHighwayCommand(factory(targets))) return;
        LogMutation(targets, label);
    }

    /// <summary>
    /// A TECHNIQUE verb: on the user's own chord selection it asks
    ///     <see cref="TechniqueReach" /> — the inspector's "Apply to" ladder — whether it reaches
    ///     further, and then writes the rung's chords on every level in the same node as the selected
    ///     notes. An implied target (the hand-shape or pen rung of <see cref="IEditTargetService" />)
    ///     never widens: nobody picked it, so no ladder was shown for it.
    /// </summary>
    private void RunTechniqueMutation(Func<SelectedSoundMutationTargets, IHighwayEditCommand> factory, string label)
    {
        var target = _targets.ResolveAndPromote();
        var targets = MutationTargets(target);
        var reach = target.Source == EditTargetSource.Selection && target.Chords.Count > 0
            ? TechniqueReach?.ChordsFor(target.Chords)
            : null;
        if (reach is { Count: > 0 } && _scope.Arrangement is { } arrangement)
            targets = targets with { Ladder = new ChordLadderReach(reach, arrangement.Levels, GetLevelSpacingUpdater) };

        if (!ExecuteHighwayCommand(factory(targets))) return;
        LogMutation(targets, label);
        if (targets.Ladder == null) return;

        var written = targets.TargetChords();
        _logger.Info(AppLogCategory.Editing,
            $"The 'Apply to' ladder took {label} to {written.Count} chord(s) on {LevelsOf(written, targets.Ladder.Levels)} level(s).");
        TechniqueReach?.Reached(written);
    }

    private static int LevelsOf(IReadOnlyList<Chord> chords, IReadOnlyList<ChartLevel> levels)
    {
        var set = new HashSet<Chord>(chords, ReferenceEqualityComparer.Instance);
        return levels.Count(l => l.Chords.Any(set.Contains));
    }

    private void LogMutation(SelectedSoundMutationTargets targets, string label)
    {
        _logger.Info(AppLogCategory.Editing,
            $"Selection {label}. Notes={targets.SelectedNotes.Count}, chords={targets.SelectedChords.Count}, mask={targets.Mask}. " +
            $"Current notes={Describe(targets.SelectedNotes, FormatNote)}. " +
            $"Current chords={Describe(targets.SelectedChords, FormatChord)}.");
    }

    public bool AdjustSelectedAnchors(int delta, AnchorNudgeTarget target, int capo = 0)
    {
        if (delta == 0) return false;

        var selected = _selectionManager.SelectedAnchors.ToList();
        if (selected.Count == 0) return false;

        // The lowest legal anchor is capo+1: an anchor on or below the capo is
        // inspector rule L02, because the capo already stops the string there.
        // CLAMPED to the neck, like ChartImprovementService.MinAnchorFret and
        // FretAnchorGenerationService.MinAnchorFret: the capo spinner goes to
        // 24, and an unclamped floor of 25 inverts the Math.Clamp below —
        // which THROWS ArgumentException on the UI thread.
        var minFret = Math.Clamp(capo + 1, 1, MaxAnchorFret);

        var targets = new List<FretAnchor>();
        var oldStates = new List<FretAnchor>();
        var newStates = new List<FretAnchor>();

        foreach (var anchor in selected)
        {
            var oldState = anchor.Clone();
            var newState = anchor.Clone();

            if (target == AnchorNudgeTarget.Fret)
                newState.Fret = Math.Clamp(newState.Fret + delta, minFret, MaxAnchorFret);
            else
                newState.Width = Math.Clamp(newState.Width + delta, MinAnchorWidth, MaxAnchorWidth);

            // A clamped anchor contributes nothing; without this, nudging a
            // selection where one anchor is already at the ceiling would still
            // push a node and make the whole gesture look like it did something.
            if (ChartActionStateComparer.AnchorsEqual(oldState, newState)) continue;

            targets.Add(anchor);
            oldStates.Add(oldState);
            newStates.Add(newState);
        }

        if (targets.Count == 0) return false;

        var verb = target == AnchorNudgeTarget.Fret ? MergeVerbs.AnchorFret : MergeVerbs.AnchorWidth;
        if (!_historyManager.Do(new MutateAnchorsAction(
                _scope.Anchors, targets, oldStates, newStates, null, verb)))
        {
            return false;
        }

        _logger.Info(AppLogCategory.Editing,
            $"Nudged FHP anchor {target.ToString().ToLowerInvariant()} by {delta:+0;-0} on {targets.Count} anchor(s). " +
            $"Now {Describe(targets, a => $"fret={a.Fret}, width={a.Width}")}.");
        return true;
    }

    public bool AdjustSelectedNotesSustain(int deltaGridSteps, double totalAudioLengthMs, int gridDivisor,
        IList<BeatMarker> beats)
    {
        // Same ladder as every other keyboard verb: `]`/`[` on the sound under
        // the playhead is not a silent no-op.
        var target = _targets.ResolveAndPromote();
        var selectedNotes = target.Notes;
        var selectedChords = target.Chords;
        if (selectedNotes.Count == 0 && selectedChords.Count == 0) return false;

        var command = new AdjustSelectedSustainsCommand(
            _scope.Notes,
            _scope.Chords,
            selectedNotes,
            selectedChords,
            new FractionalPosition(new Fraction(deltaGridSteps, gridDivisor)),
            totalAudioLengthMs,
            beats,
            _configService.LoadAppConfig(),
            GetLinkNextUpdater());

        if (!ExecuteHighwayCommand(command)) return false;

        // `]`/`[` is ALSO the timeline mouse wheel (TimelineWheelAdjuster), so one
        // notch over a whole-level selection would be one interpolated line per
        // sound on the UI thread. Same cap as every other verb (EditLogDetail).
        _logger.Info(AppLogCategory.Editing,
            $"Adjusted sustain by {deltaGridSteps:+0;-0}/{gridDivisor}. " +
            $"Notes={selectedNotes.Count}, chords={selectedChords.Count}. " +
            $"Current notes={Describe(selectedNotes, FormatNote)}. " +
            $"Current chords={Describe(selectedChords, FormatChord)}.");
        return true;
    }

    // Whole (4/1) down to a 64th (1/16) — the binary note-value ladder the +/-
    // keys walk. The absolute palette set is NOT clamped here (dotted/triplet
    // values legitimately sit off this ladder); only the scale path is.
    private static readonly Fraction MinNotationDuration = new(1, 16);
    private static readonly Fraction MaxNotationDuration = new(4, 1);

    public bool SetSelectedNotationDuration(Fraction? duration)
    {
        return MutateSelectedNotationDuration(_ => duration,
            duration is { } d ? $"set notation duration {d}" : "cleared notation duration");
    }

    public bool ScaleSelectedNotationDuration(Fraction factor)
    {
        return MutateSelectedNotationDuration(current => ScaleDuration(current, factor),
            $"scaled notation duration ×{factor}");
    }

    /// <summary>Null hint counts as a quarter (1/1) so a first +/- on an
    /// unauthored note moves off the most common value; result clamped to the
    /// whole..64th ladder so repeated halving/doubling can never run to 0 or
    /// blow up.</summary>
    private static Fraction ScaleDuration(Fraction? current, Fraction factor)
    {
        var scaled = (current ?? Fraction.One) * factor;
        if (scaled < MinNotationDuration) scaled = MinNotationDuration;
        if (scaled > MaxNotationDuration) scaled = MaxNotationDuration;
        return scaled;
    }

    /// <summary>
    /// Applies a duration-hint transform to every selected note AND chord as ONE
    /// composite undo step (a chord and its notes are both first-class sheet
    /// beats). Notes go through the mutate-fn action; chords carry explicit
    /// old/new clones. Nothing selected ⇒ no-op, no history node.
    /// </summary>
    private bool MutateSelectedNotationDuration(Func<Fraction?, Fraction?> transform, string label)
    {
        var target = _targets.ResolveAndPromote();
        var selectedNotes = target.Notes;
        var selectedChords = target.Chords;
        if (selectedNotes.Count == 0 && selectedChords.Count == 0) return false;

        var actions = new List<IChartAction>();
        if (selectedNotes.Count > 0)
        {
            actions.Add(new MutateNotesAction(_scope.Notes, selectedNotes,
                n => n.NotationDuration = transform(n.NotationDuration), GetLinkNextUpdater()));
        }

        if (selectedChords.Count > 0)
        {
            var oldStates = selectedChords.Select(c => c.Clone()).ToList();
            var newStates = selectedChords.Select(c =>
            {
                var clone = c.Clone();
                clone.NotationDuration = transform(clone.NotationDuration);
                return clone;
            }).ToList();
            actions.Add(new MutateChordsAction(_scope.Chords, selectedChords, oldStates, newStates,
                GetLinkNextUpdater()));
        }

        if (!_historyManager.Do(new CompositeAction(actions))) return false;
        _logger.Info(AppLogCategory.Editing,
            $"Sheet: {label} on {selectedNotes.Count} note(s) + {selectedChords.Count} chord(s).");
        return true;
    }

    private int GetPreviewFret() => _projectState.PreviewNoteFret;

    private bool ExecuteHighwayCommand(IHighwayEditCommand command) => command.Execute(_historyManager);

    /// <summary>
    ///     How many sounds an editing log line spells out before it summarises the
    ///     rest — see <see cref="EditLogDetail" /> for why every verb in this file
    ///     has to obey it.
    /// </summary>
    private const int LogDetailCap = EditLogDetail.Cap;

    /// <inheritdoc cref="EditLogDetail.Describe{T}" />
    private static string Describe<T>(IReadOnlyList<T> items, Func<T, string> format)
        => EditLogDetail.Describe(items, format);

    private void LogMovedSounds(
        IReadOnlyList<GuitarNote> targetNotes,
        IReadOnlyList<GuitarNote> requestedNewNotes,
        IReadOnlyList<Chord> targetChords,
        IReadOnlyList<Chord> requestedNewChords,
        IReadOnlyList<GuitarNote> finalNewNotes,
        IReadOnlyList<Chord> finalNewChords)
    {
        // One line PER SOUND, so a drag that moves a whole phrase wrote a
        // thousand of them — the same unconditional-formatting cost as the
        // mutation verbs, spread over the log instead of one line.
        var movedNotes = Math.Min(targetNotes.Count, requestedNewNotes.Count);
        for (var i = 0; i < movedNotes && i < LogDetailCap; i++)
        {
            _logger.Info(AppLogCategory.Editing,
                $"Moved note from {FormatNote(targetNotes[i])} to requested {FormatNote(requestedNewNotes[i])}.");
        }

        LogElision("moved note", movedNotes);

        var movedChords = Math.Min(targetChords.Count, requestedNewChords.Count);
        for (var i = 0; i < movedChords && i < LogDetailCap; i++)
        {
            _logger.Info(AppLogCategory.Editing,
                $"Moved chord from {FormatChord(targetChords[i])} to requested {FormatChord(requestedNewChords[i])}.");
        }

        LogElision("moved chord", movedChords);

        for (var i = 0; i < finalNewNotes.Count && i < LogDetailCap; i++)
            _logger.Info(AppLogCategory.Editing, $"Final placed {FormatNote(finalNewNotes[i])}.");
        LogElision("final placed note", finalNewNotes.Count);

        for (var i = 0; i < finalNewChords.Count && i < LogDetailCap; i++)
            _logger.Info(AppLogCategory.Editing, $"Final placed {FormatChord(finalNewChords[i])}.");
        LogElision("final placed chord", finalNewChords.Count);
    }

    private void LogElision(string what, int total)
    {
        if (total > LogDetailCap)
            _logger.Info(AppLogCategory.Editing, $"…and {total - LogDetailCap} more {what} lines not written.");
    }

    private string FormatPosition(FractionalPosition position)
        => position.FormatWithMs(_projectState.Beats);

    private string FormatNote(GuitarNote note) => $"note string={note.StringIndex}, fret={note.Fret}, position={FormatPosition(note.Position)}, sustain={note.Sustain}, end={FormatPosition(note.EndPosition)}, linkNext={note.LinkNext}, passOther={note.PassOtherNotes}";

    private string FormatChord(Chord chord)
    {
        var notes = string.Join(", ", chord.ChordNotes.Select(n =>
            $"string={n.StringIndex}/fret={n.Fret}/sustain={n.Sustain}/linkNext={n.LinkNext}"));
        return
            $"chord position={FormatPosition(chord.Position)}, end={FormatPosition(chord.EndPosition)}, passOther={chord.PassOtherNotes}, templateId={chord.TemplateId}, notes=[{notes}]";
    }

    private string FormatHandShape(HandShape handShape) => $"handshape name='{handShape.Name}', templateId={handShape.TemplateId}, position={FormatPosition(handShape.Position)}, end={FormatPosition(handShape.EndPosition)}";

    private string FormatAnchor(FretAnchor anchor) => $"fhp fret={anchor.Fret}, width={anchor.Width}, position={FormatPosition(anchor.Position)}";
}
