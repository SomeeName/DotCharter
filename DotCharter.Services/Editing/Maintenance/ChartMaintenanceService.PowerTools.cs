// ChartMaintenanceService: the charting power tools - level generation, phrase-iteration propagation, the MIDI draft apply and refret.
using DotCharter.Services.Editing.Improvement;
using DotCharter.Services.Parsing.Midi;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

// The "charting power tools": auto difficulty-level generation, phrase-iteration
// propagation, the MIDI draft apply, and the refret engine.
//
// All of them mutate the chart, so all of them go through IHistoryService — a
// power tool that cannot be undone is a trap. Level generation, the MIDI apply
// and the refret rewrite whole-project state (levels / arrangements / chord
// templates / anchors), so they use the same ProjectSnapshotAction +
// ChartProjectCloner snapshot the squash and improve pipelines use. Phrase
// propagation is already undoable inside PhraseIterationPropagationService (one
// CompositeAction across every target), so this layer only resolves the source
// iteration and refreshes the view.
/// <summary>
///     Level generation's position: levels BUILT so far, out of the request.
/// </summary>
public readonly record struct LevelGenerationProgress(int CompletedLevels, int TotalLevels)
{
    /// <summary>0..1, clamped. 0 for an empty request rather than NaN.</summary>
    public double Fraction =>
        TotalLevels <= 0 ? 0.0 : Math.Clamp(CompletedLevels / (double)TotalLevels, 0.0, 1.0);
}

public sealed partial class ChartMaintenanceService
{
    /// <summary>
    ///     Expands the active arrangement's hardest level into
    ///     <paramref name="levelCount" /> progressively thinner difficulty levels.
    ///     The inverse of <see cref="SquashLevels" />; recorded as one undoable
    ///     project snapshot.
    /// </summary>
    public bool GenerateLevels(int levelCount)
    {
        // offThread: false takes no await, so the task is already completed here
        // — this is a shape-sharing wrapper, not a sync-over-async block.
        var run = GenerateLevelsCoreAsync(levelCount, progress: null, offThread: false);
        return run.GetAwaiter().GetResult();
    }

    /// <inheritdoc cref="GenerateLevels" />
    public Task<bool> GenerateLevelsAsync(int levelCount, IProgress<LevelGenerationProgress>? progress = null)
        => GenerateLevelsCoreAsync(levelCount, progress, offThread: true);

    /// <summary>
    ///     <para>
    ///         <b>What is safe to move off the UI thread, and what is not.</b>
    ///         Level generation does NOT mutate the live project while it works:
    ///         it takes two <c>ChartProjectCloner.Clone</c> snapshots, runs the
    ///         generator over the <c>after</c> CLONE, and publishes the result
    ///         through one <see cref="ProjectSnapshotAction" />, which reconciles
    ///         it into the live graph (<c>ChartProjectRestorer</c>).
    ///     </para>
    ///     <para>
    ///         So the PROLOGUE (both clones read the live graph) and the EPILOGUE
    ///         (the history node writes it) stay on the UI thread — exactly the
    ///         reason the save pipeline clones there. The middle phase is the only
    ///         one whose cost scales with the request (up to 20 levels × every
    ///         sound of the source level), owns nothing but the clone, and is
    ///         therefore the one that runs on a worker.
    ///     </para>
    ///     <para>
    ///         The dialog that drives this stays OPEN for the duration, which is
    ///         what makes the split safe rather than merely fast: its scrim blocks
    ///         the pointer and <c>GlobalChordGate</c> refuses every editing key
    ///         while <c>HasOpenDialog</c>, so nothing can edit the live project
    ///         between the clone and the apply and make <c>before</c> a lie.
    ///     </para>
    /// </summary>
    private async Task<bool> GenerateLevelsCoreAsync(int levelCount,
        IProgress<LevelGenerationProgress>? progress, bool offThread)
    {
        var project = _projectState.ActiveProject;
        var arrangement = _projectState.ActiveArrangement;
        if (project == null || arrangement == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return false;
        }

        if (levelCount < 2)
        {
            _projectState.MainDisplayMessage = L("Status_GenerateLevelsNoChange");
            return false;
        }

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var live = LiveArrangementAt(project, arrangementIndex);
            if (live == null)
            {
                _projectState.MainDisplayMessage = L("Status_GenerateLevelsNoChange");
                return false;
            }

            // One arrangement changes, so the step snapshots one. The beats are copied:
            // the generator may run off the UI thread while the charter keeps editing.
            var before = ChartProjectCloner.CloneArrangement(live);
            var target = ChartProjectCloner.CloneArrangement(live);
            var templates = LevelTemplateOrphans.Capture(target);
            var beats = project.Beats.Select(b => b.Clone()).ToList();

            // Forwards SYNCHRONOUSLY. Marshalling is the caller's decision and
            // the caller already made it — the dialog hands in a Progress<T>
            // built on the UI thread. A second Progress<T> here would post a
            // post, and make the report order untestable.
            var levelProgress = progress == null ? null : new LevelProgressAdapter(progress, levelCount);

            // The beat grid is what tells the generator which sounds sit on a
            // measure downbeat — without it only on/off-beat importance survives.
            var generated = offThread
                ? await Task.Run(() =>
                    _levelGenerationService.TryGenerate(target, levelCount, beats, levelProgress))
                : _levelGenerationService.TryGenerate(target, levelCount, beats, levelProgress);

            if (!generated)
            {
                _projectState.MainDisplayMessage = L("Status_GenerateLevelsNoChange");
                return false;
            }

            var previousLevelCount = arrangement.Levels.Count;

            // Land on the TOP level (index N-1) — that one is a verbatim clone of
            // the source chart, so the editor shows what the user just had. The reduced templates
            // the replaced levels played and the new ones do not go in the same node.
            var levels = LevelOperationAction(live, arrangementIndex, before, target, templates,
                levelCount - 1, vocalTrackIndex);
            _historyService.Do(new CompositeAction([levels]) { LinkPolicyOverride = LinkPolicy.ShareFirstChanged });

            _chartEditor.RequestRedraw();
            _projectState.SetMessage(L("Status_GenerateLevelsApplied", levelCount, target.Name), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Editing,
                $"Generated difficulty levels for arrangement '{target.Name}'. PreviousLevelCount={previousLevelCount}, NewLevelCount={levelCount}.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Difficulty-level generation failed.", ex);
            _projectState.SetMessage(L("Status_GenerateLevelsFailed", ex.Message), StatusSeverity.Error);
            return false;
        }
    }

    /// <summary>Maps the generator's raw "levels built" counter onto the caller's
    /// report. See the construction site for why it does not marshal.</summary>
    private sealed class LevelProgressAdapter(IProgress<LevelGenerationProgress> inner, int total)
        : IProgress<int>
    {
        public void Report(int built) => inner.Report(new LevelGenerationProgress(built, total));
    }

    /// <summary>
    ///     "This chorus appears 4× — apply my edit to all of them": copies the
    ///     content of the phrase iteration under the playhead into every other
    ///     iteration of the same phrase. Returns the number of rewritten targets.
    /// </summary>
    public int PropagatePhraseAtPlayhead()
    {
        var arrangement = _projectState.ActiveArrangement;
        if (arrangement == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return 0;
        }

        if (arrangement.PhraseIterations.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_PropagatePhraseNoIterations");
            return 0;
        }

        // An iteration's span runs from its position to the next iteration's, so
        // the one that OWNS the playhead is the last iteration at or before it.
        // Before the first iteration, fall back to the first one.
        var playhead = TimelineMath.PositionFromMs(_projectState.CurrentTimeMs, _chartEditor.Beats);
        var ordered = arrangement.PhraseIterations.OrderBy(pi => pi.Position).ToList();
        var source = ordered.LastOrDefault(pi => pi.Position <= playhead) ?? ordered[0];

        try
        {
            var count = _phrasePropagationService.PropagateToAllIterations(source);
            if (count == 0)
            {
                _projectState.MainDisplayMessage = L("Status_PropagatePhraseNoTargets");
                return 0;
            }

            // The targets' old sounds were removed from the live collections —
            // any selection still pointing at them is dangling.
            _chartEditor.Selection.ClearAll();
            _chartEditor.RequestRedraw();
            _projectState.SetMessage(_localization.Plural("Status_PropagatePhraseAppliedOne", "Status_PropagatePhraseApplied", count), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Editing,
                $"Propagated phrase iteration at {FormatPosition(source.Position)} (phrase {source.PhraseId}) to {count} other iteration(s) of arrangement '{arrangement.Name}'.");
            return count;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Phrase-iteration propagation failed.", ex);
            _projectState.SetMessage(L("Status_PropagatePhraseFailed", ex.Message), StatusSeverity.Error);
            return 0;
        }
    }

    /// <summary>
    ///     Accepts a parsed MIDI draft as a NEW arrangement ("draft layer") —
    ///     nothing existing is touched, so rejecting the import is deleting the
    ///     arrangement. Optionally replaces the project beat grid with the MIDI's
    ///     own tempo map (the draft's note positions are expressed on THAT grid).
    ///     One undoable project snapshot.
    /// </summary>
    public bool ApplyMidiDraft(MidiChartDraft draft, bool replaceBeatGrid)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_MidiImportNoProject");
            return false;
        }

        if (draft.Notes.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_MidiImportNothingFound");
            return false;
        }

        try
        {
            var previousArrangementIndex = GetActiveArrangementIndex();
            var previousLevelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            var template = previousArrangementIndex >= 0 && previousArrangementIndex < after.Arrangements.Count
                ? after.Arrangements[previousArrangementIndex]
                : after.Arrangements.FirstOrDefault();

            // Through the one factory — it carries the template's TUNING OFFSETS
            // as well as its letters, which a hand-written initializer would drop (see
            // DraftArrangementFactory's remarks).
            var draftArrangement = DraftArrangementFactory.FromTemplate(
                template, UniqueArrangementName(after, L("MidiImport_DraftName")));

            var level = new ChartLevel();
            foreach (var draftNote in draft.Notes) level.Notes.Add(draftNote.Note.Clone());
            draftArrangement.Levels.Add(level);
            after.Arrangements.Add(draftArrangement);

            // "Lock beat map" is a promise no verb may break, and this is the
            // most destructive grid writer in the app: it CLEARS the beat list.
            // Skip that half and keep the draft, exactly as the Guitar Pro
            // import's ApplyTempoAndKey does — the lock covers the grid, not the
            // import the user asked for.
            if (replaceBeatGrid && after.IsBeatMapLocked)
            {
                replaceBeatGrid = false;
                _logger.Info(AppLogCategory.Import,
                    "Beat map is locked — MIDI tempo-map replacement skipped; the draft arrangement still applies.");
            }

            if (replaceBeatGrid && draft.SuggestedBeats.Count > 0)
            {
                after.Beats.Clear();
                foreach (var beat in draft.SuggestedBeats)
                    after.Beats.Add(beat.Clone());
                if (after.Beats[0].Bpm > 0) after.Bpm = after.Beats[0].Bpm;
            }

            // Undo must land back on the arrangement the user was editing, so the
            // action carries the PREVIOUS indices; the freshly imported draft is
            // selected below, on the live project the action just installed.
            _historyService.Do(new ProjectSnapshotAction(this, before, after, previousArrangementIndex,
                previousLevelIndex, vocalTrackIndex));

            var applied = _projectState.ActiveProject;
            if (applied != null && applied.Arrangements.Count > 0)
                _projectState.ActiveArrangement = applied.Arrangements[^1];

            _chartEditor.RequestRedraw();
            _projectState.MainDisplayMessage =
                L("Status_MidiImportApplied", draftArrangement.Name, draft.Notes.Count);
            _logger.Info(AppLogCategory.Import,
                $"MIDI draft applied. Arrangement='{draftArrangement.Name}', notes={draft.Notes.Count}, replacedBeatGrid={replaceBeatGrid}, beats={after.Beats.Count}.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, "MIDI draft apply failed.", ex);
            _projectState.SetMessage(L("Status_MidiImportFailed", ex.Message), StatusSeverity.Error);
            return false;
        }
    }

    private static string UniqueArrangementName(ChartProject project, string baseName)
    {
        var name = baseName;
        var i = 2;
        while (project.Arrangements.Any(a => a.Name == name)) name = $"{baseName} {i++}";
        return name;
    }

    /// <summary>
    ///     The refret engine: re-assigns string/fret positions for the SAME
    ///     sounding pitches to minimize hand movement and stretch. Operates on
    ///     the selection's range when notes/chords are selected, else on the
    ///     whole active arrangement. One undoable project snapshot.
    /// </summary>
    public bool Refret()
    {
        var project = _projectState.ActiveProject;
        var arrangement = _projectState.ActiveArrangement;
        if (project == null || arrangement == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return false;
        }

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var range = FhpGenerationCommands.GetSelectedSoundRange(_chartEditor);
            var live = LiveArrangementAt(project, arrangementIndex);
            if (live == null)
            {
                _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
                return false;
            }

            // One arrangement changes, so the step snapshots one.
            var before = ChartProjectCloner.CloneArrangement(live);
            var target = ChartProjectCloner.CloneArrangement(live);

            var result = range != null && levelIndex >= 0 && levelIndex < target.Levels.Count
                ? _refretService.RefretRange(target, target.Levels[levelIndex], project.Beats,
                    range.Value.Start, range.Value.End)
                : _refretService.RefretArrangement(target, project.Beats);

            if (!result.HasChanges)
            {
                _projectState.MainDisplayMessage = L("Status_RefretNoChange");
                return false;
            }

            _historyService.Do(new ArrangementSnapshotAction(this, arrangementIndex, before, target,
                levelIndex, vocalTrackIndex));

            _projectState.MainDisplayMessage =
                _localization.Plural("Status_RefretAppliedOne", "Status_RefretApplied", result.SoundsChanged,
                    result.TravelImprovementPercent);
            _logger.Info(AppLogCategory.Editing,
                $"Refret applied to arrangement '{target.Name}'. Scope={(range != null ? "selection" : "arrangement")}, " +
                $"sounds={result.SoundsConsidered}, notesChanged={result.NotesChanged}, chordsChanged={result.ChordsChanged}, " +
                $"templatesAdded={result.TemplatesAdded}, anchorsRegenerated={result.AnchorsRegenerated}, " +
                $"cost {result.CostBefore:0.##}→{result.CostAfter:0.##}, " +
                $"travel {result.TravelBefore:0.#}→{result.TravelAfter:0.#} (−{result.TravelImprovementPercent}%).");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Refret failed.", ex);
            _projectState.SetMessage(L("Status_RefretFailed", ex.Message), StatusSeverity.Error);
            return false;
        }
    }
}
