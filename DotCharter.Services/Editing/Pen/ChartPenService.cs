using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Snapping;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Services.Editing.Pen;

/// <summary>
///     Drives <see cref="ChartPen" /> against the chart. The state machine
///     decides; this is the only thing that edits, and every edit goes through an
///     <see cref="IHighwayEditCommand" /> so it is history-backed by construction.
///     <para>
///         <b>It reuses the existing verbs rather than adding a second set.</b>
///         A string toggle IS <see cref="ApplyNotePlacementCommand" /> — the same
///         toggle tree, collision service and template repointing the pointer's
///         right-click uses — and a typed fret IS
///         <c>SelectedNoteMutationCommands.SetFret</c> /
///         <c>TransposeShapeTo</c>, so the chord semantics and the
///         <c>ChordTemplateResolver</c> repoint come along for free.
///     </para>
/// </summary>
public sealed class ChartPenService : IChartPenService
{
    private readonly IStrictCollisionService _collisions;
    private readonly IConfigurationService _config;
    private readonly IHighwayEditingService _highway;
    private readonly IHistoryService _history;
    private readonly IAppLogger _logger;
    private readonly ChartPen _pen = new();
    private readonly ProjectState _projectState;
    private readonly IEditingScope _scope;
    private readonly ISelectionService _selection;
    private readonly IEditTargetService _targets;
    private readonly ITempoService _tempo;

    /// <summary>The sound the open typing run is aimed at. A digit landing on a
    /// DIFFERENT target ends the run — otherwise seeking between two digits would
    /// undo the fret written on the first sound and apply the extended value to
    /// the second. Reference identity on purpose.</summary>
    private object? _runTarget;

    public ChartPenService(
        ProjectState projectState,
        IEditingScope scope,
        IHistoryService history,
        ISelectionService selection,
        IStrictCollisionService collisions,
        ITempoService tempo,
        IHighwayEditingService highway,
        IEditTargetService targets,
        IConfigurationService config,
        IAppLogger logger)
    {
        _projectState = projectState;
        _scope = scope;
        _history = history;
        _selection = selection;
        _collisions = collisions;
        _tempo = tempo;
        _highway = highway;
        _targets = targets;
        _config = config;
        _logger = logger;
    }

    public bool IsEnabled => _config.LoadAppConfig().UseFastChartingInput;

    public void ToggleString(int stringIndex, int gridDivisor)
    {
        if (!IsEnabled) return;
        if (_scope.Level == null) return;

        var decision = _pen.PressString(stringIndex);
        _runTarget = null;

        var position = PenPosition(gridDivisor);
        var command = new ApplyNotePlacementCommand(
            _scope.Notes,
            _scope.Chords,
            _collisions,
            _scope.Arrangement,
            position,
            decision.StringIndex,
            _projectState.PreviewNoteFret,
            _highway.GetLinkNextUpdater());

        if (!command.Execute(_history))
        {
            _logger.Trace(AppLogCategory.Editing,
                $"Pen: string {stringIndex + 1} produced no action at {position}.");
            return;
        }

        // The implied target, made VISIBLE: the pen selects what it just wrote,
        // so the highway draws it and the next fret/technique key has an obvious
        // subject. EOF's eof_update_implied_note_selection does the same thing
        // (menu/note.c:11065-11112) — this is not invisible modal state.
        SelectSoundAt(position);

        // "Pressing a string key also sets the mask to that string, so the next
        // typed fret lands where you just were" (audit 03 §6.3). Only when the
        // string is now PLAYED: pointing the mask at a string the same keystroke
        // just removed would aim every following per-string verb at nothing.
        // The mask is drawn on the lane headers, so this is visible.
        if (command.StringIsNowPlayed)
            _projectState.StringEditMask = StringEditMask.FromStrings(stringIndex);

        _logger.Info(AppLogCategory.Editing,
            $"Pen: toggled string {stringIndex + 1} at {position} (now played={command.StringIsNowPlayed}).");
    }

    public void TypeFretDigit(int digit, int gridDivisor)
    {
        if (!IsEnabled)
        {
            // Pre-v0.35 Alt+0..9: single digit, selection only, no accumulator.
            _highway.SetSelectedFret(digit);
            return;
        }

        if (_scope.Level == null) return;

        var targets = ResolveFretTargets(gridDivisor, out var targetKey);
        if (!ReferenceEquals(targetKey, _runTarget)) _pen.EndTypingRun();

        var decision = _pen.PressDigit(digit, Environment.TickCount64,
            _projectState.StringEditMask.IsEmpty, _history.CurrentNodeId);

        // A continuation replaces the run's own node instead of stacking a
        // second one, so "1 then 2" is ONE step on the undo path and Ctrl+Z
        // lands on the fret that was there before the run started.
        if (decision.ContinuesTypingRun) _history.Undo();

        var committed = false;
        if (targetKey != null)
        {
            var command = decision.Verb == PenVerb.TransposeShape
                ? SelectedNoteMutationCommands.TransposeShapeTo(targets, decision.Fret)
                : SelectedNoteMutationCommands.SetFret(targets, decision.Fret);
            committed = command.Execute(_history);
        }

        _pen.Settle(_history.CurrentNodeId, committed);
        _runTarget = targetKey;

        // The keyboard route to frets 10..24 the editor never had: the ambient
        // placement fret was mouse-wheel-only, so the next note the pen creates
        // now inherits what you just typed. Written even when there was nothing
        // to fret, which closes the other half of the loop — type the fret with
        // nothing under the pen, THEN press a string key and the note arrives at
        // that fret instead of at whatever the mouse wheel last left behind.
        _projectState.PreviewNoteFret = Math.Clamp(decision.Fret, 0, FretDigitAccumulator.MaxTypedFret);

        _logger.Info(AppLogCategory.Editing,
            $"Pen: typed fret {decision.Fret} ({decision.Verb}, mask={_projectState.StringEditMask}, " +
            $"continues={decision.ContinuesTypingRun}, committed={committed}).");
    }

    public bool RevertPendingEntry()
    {
        if (!IsEnabled) return false;

        var decision = _pen.PressEscape(Environment.TickCount64, _history.CurrentNodeId);
        _runTarget = null;
        if (decision.Verb != PenVerb.RevertTyping) return false;

        _history.Undo();
        _logger.Info(AppLogCategory.Editing, "Pen: reverted the in-progress fret entry.");
        return true;
    }

    /// <summary>
    ///     Where the pen is: the playhead, snapped exactly the way the pointer
    ///     snaps — grid, with an existing sound winning when it is nearer
    ///     (<see cref="SoundMagnetSnap" />). That is what makes "seek onto a
    ///     sound, press a string key" add to THAT sound rather than start a
    ///     second one a few ms away.
    ///     <para>
    ///         Public since Stage 3 because it is also the bottom rung of the
    ///         SHARED target ladder: the composition root installs it as
    ///         <c>IEditTargetService</c>'s pen-position provider, so a technique
    ///         keybind resolves the implied target at exactly the position a
    ///         typed fret does.
    ///     </para>
    /// </summary>
    public FractionalPosition PenPosition(int gridDivisor)
    {
        var beats = _scope.Beats;
        var timeMs = _projectState.CurrentTimeMs;
        var gridSnap = _tempo.SnapToGrid(timeMs, beats, gridDivisor);
        return SoundMagnetSnap.Apply(timeMs, gridSnap, beats, _scope.Notes, _scope.Chords);
    }

    /// <summary>
    ///     The SHARED ladder — <see cref="IEditTargetService" />, selection →
    ///     hand shape → pen. The pen does not own this resolution privately
    ///     (rscharter's <c>GuitarModeHandler.java:337-355</c>, EOF's implied
    ///     selection at <c>menu/note.c:11065-11112</c>) while every other
    ///     keyboard verb was selection-only; Stage 3 lifted it out so both go
    ///     through one implementation and a typed fret reaches the same sound a
    ///     technique toggle does.
    /// </summary>
    private SelectedSoundMutationTargets ResolveFretTargets(int gridDivisor, out object? targetKey)
    {
        var target = _targets.ResolveAndPromoteAt(PenPosition(gridDivisor));
        targetKey = target.Anchor;

        return new SelectedSoundMutationTargets(
            _scope.Notes,
            _scope.Chords,
            target.Notes,
            target.Chords,
            _scope.Arrangement,
            _projectState.StringEditMask,
            _highway.GetLinkNextUpdater());
    }

    private void SelectSoundAt(FractionalPosition position)
    {
        _selection.ClearSelections();

        var chord = FindChordAt(position);
        if (chord != null)
        {
            _selection.SelectedChords.Add(chord);
            return;
        }

        foreach (var note in _scope.Notes.Where(n => n.Position == position))
            _selection.SelectedNotes.Add(note);
    }

    private Chord? FindChordAt(FractionalPosition position)
    {
        foreach (var chord in _scope.Chords)
        {
            if (chord.Position == position)
                return chord;
        }

        return null;
    }
}
