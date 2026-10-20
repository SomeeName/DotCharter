// ChartMaintenanceService: copying a song's STRUCTURE — its section and phrase
// markers — from the arrangement being charted onto the others.

using DotCharter.Services.Editing.Sync;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

public sealed partial class ChartMaintenanceService
{
    /// <summary>
    ///     Copies the active arrangement's section / phrase markers onto every
    ///     other arrangement in the project.
    ///     <para>
    ///         A song's structure is a property of the SONG, not of one
    ///         arrangement — the chorus starts where the chorus starts whether
    ///         you are looking at Lead or Bass. The event dialog can already fan
    ///         a single marker out to several arrangements, but only if the box
    ///         was ticked at the time, and a charter who works through the whole
    ///         song on Lead first has no way back: the others end up with
    ///         nothing, and RS-XML needs phrases on all of them.
    ///     </para>
    ///     <para>
    ///         MERGE, never replace. A marker the target already has at that
    ///         position is overwritten; one it has that the source does not is
    ///         left alone. So it is safe to run twice, and safe to run after the
    ///         other arrangements have structure of their own.
    ///     </para>
    ///     <para>
    ///         <see cref="PhraseInfo" /> travels with the phrases that reference
    ///         it — the solo flag is keyed by phrase NAME, so a phrase copied
    ///         without it would silently stop being a solo on the target.
    ///     </para>
    /// </summary>
    /// <returns>How many markers were written across all targets.</returns>
    public int CopyStructureToOtherArrangements()
    {
        var project = _projectState.ActiveProject;
        var source = _projectState.ActiveArrangement;
        if (project == null || source == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return 0;
        }

        var sourceIndex = project.Arrangements.IndexOf(source);
        if (sourceIndex < 0 || project.Arrangements.Count < 2)
        {
            _projectState.MainDisplayMessage = L("Status_CopyStructureNoTargets");
            return 0;
        }

        if (source.EventPoints.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_CopyStructureNothingToCopy");
            return 0;
        }

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            var written = 0;
            var targets = 0;
            var afterSource = after.Arrangements[sourceIndex];
            for (var i = 0; i < after.Arrangements.Count; i++)
            {
                if (i == sourceIndex) continue;
                targets++;
                written += MergeStructureInto(afterSource, after.Arrangements[i]);
            }

            if (written == 0)
            {
                _projectState.MainDisplayMessage = L("Status_CopyStructureAlreadyMatched");
                return 0;
            }

            _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                vocalTrackIndex));
            _chartEditor.RequestRedraw();
            _projectState.SetMessage(L("Status_CopyStructureApplied", written, targets), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Editing,
                $"Copied structure from '{source.Name}' to {targets} arrangement(s): {written} marker(s) written " +
                $"from {source.EventPoints.Count} source marker(s).");
            return written;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Copy structure to other arrangements failed.", ex);
            _projectState.SetMessage(L("Status_CopyStructureFailed", ex.Message), StatusSeverity.Error);
            return 0;
        }
    }

    /// <summary>
    ///     Writes <paramref name="source" />'s markers into <paramref name="target" />
    ///     and returns how many actually changed something. Visible for testing.
    /// </summary>
    internal static int MergeStructureInto(Arrangement source, Arrangement target)
    {
        var written = 0;
        var byPosition = new Dictionary<FractionalPosition, EventPoint>();
        foreach (var existing in target.EventPoints) byPosition.TryAdd(existing.Position, existing);

        foreach (var marker in source.EventPoints)
        {
            if (byPosition.TryGetValue(marker.Position, out var existing))
            {
                if (string.Equals(existing.Section, marker.Section, StringComparison.Ordinal) &&
                    string.Equals(existing.Phrase, marker.Phrase, StringComparison.Ordinal) &&
                    string.Equals(existing.Events, marker.Events, StringComparison.Ordinal) &&
                    existing.Status == marker.Status)
                {
                    continue;
                }

                existing.Section = marker.Section;
                existing.Phrase = marker.Phrase;
                existing.Events = marker.Events;
                existing.Status = marker.Status;
                written++;
                continue;
            }

            var copy = new EventPoint
            {
                Position = marker.Position,
                Section = marker.Section,
                Phrase = marker.Phrase,
                Events = marker.Events,
                Status = marker.Status
            };
            target.EventPoints.Add(copy);
            byPosition[copy.Position] = copy;
            written++;
        }

        if (written > 0)
        {
            var ordered = target.EventPoints.OrderBy(e => e.Position).ToList();
            target.EventPoints.Clear();
            foreach (var e in ordered) target.EventPoints.Add(e);
        }

        // The solo flag is keyed by phrase NAME, so it has to follow the phrases
        // that were copied or a solo phrase silently stops being one.
        //
        // ONLY the solo flag. MaxDifficulty is a fact about the TARGET's own
        // level stack, not about the phrase: a lead with eight generated levels
        // copied its structure onto a single-level rhythm and left every phrase
        // there claiming a ceiling of 7. LiveExpectedTimeline.BuildPhraseSpans and
        // LevelSquashService.ResolveSourceLevelId both read that number back —
        // only the matcher clamps it, and the value is what the RS export writes.
        var targetCeiling = TopDifficultyIndex(target);
        foreach (var marker in source.EventPoints)
        {
            if (string.IsNullOrWhiteSpace(marker.Phrase)) continue;
            if (!source.PhraseInfos.TryGetValue(marker.Phrase, out var info)) continue;
            if (target.PhraseInfos.TryGetValue(marker.Phrase, out var existingInfo)) existingInfo.Solo = info.Solo;
            else target.PhraseInfos[marker.Phrase] = new PhraseInfo(targetCeiling, info.Solo);
        }

        return written;
    }

    /// <summary>The highest difficulty an arrangement actually has — the same
    /// value <c>LiveExpectedTimeline.BuildPhraseSpans</c> falls back to for a phrase
    /// carrying no <see cref="PhraseInfo" />, so it is the neutral ceiling for a
    /// phrase this copy has to invent one for.</summary>
    private static int TopDifficultyIndex(Arrangement arrangement) =>
        arrangement.Levels.Count == 0 ? 0 : Math.Max(0, arrangement.Levels.Max(level => level.DifficultyIndex));

    /// <summary>
    ///     Carries the charting work done on <paramref name="source" /> over to
    ///     <paramref name="target" />, wherever the two play the same thing.
    ///     <para>
    ///         The rule and its reasoning live in
    ///         <see cref="ArrangementSoundSync" />; this is the undoable wrapper.
    ///         It goes through <c>ProjectSnapshotAction</c> like every other
    ///         multi-arrangement verb here, so one Ctrl+Z takes the whole sync
    ///         back — which is what makes an OVERWRITE safe to offer.
    ///     </para>
    /// </summary>
    public ArrangementSyncSummary SyncArrangement(Arrangement source, Arrangement target)
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return ArrangementSyncSummary.Empty;
        }

        var sourceIndex = project.Arrangements.IndexOf(source);
        var targetIndex = project.Arrangements.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            _projectState.MainDisplayMessage = L("Status_ArrangementSyncNoPair");
            return ArrangementSyncSummary.Empty;
        }

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            var summary = ArrangementSoundSync.Apply(after.Arrangements[sourceIndex], after.Arrangements[targetIndex]);
            if (!summary.WouldChangeAnything)
            {
                _projectState.MainDisplayMessage = L("Status_ArrangementSyncNothingToDo");
                return summary;
            }

            _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                vocalTrackIndex));
            // The target's old sounds were reconciled in place, but a selection
            // pointing at a hand shape or anchor that was REPLACED is dangling.
            _chartEditor.Selection.ClearAll();
            _chartEditor.RequestRedraw();
            _projectState.SetMessage(
                L("Status_ArrangementSyncApplied", summary.SoundsChanged, summary.HandShapes, summary.Anchors,
                    target.Name), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Editing,
                $"Synced '{source.Name}' -> '{target.Name}': {summary.SharedSounds} shared sound(s), " +
                $"{summary.SoundsChanged} changed, {summary.HandShapes} hand shape(s), " +
                $"{summary.Anchors} anchor(s), {summary.Templates} template(s) minted.");
            return summary;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Arrangement sync failed.", ex);
            _projectState.SetMessage(L("Status_ArrangementSyncFailed", ex.Message), StatusSeverity.Error);
            return ArrangementSyncSummary.Empty;
        }
    }
}
