using DotCharter.Localization;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.State;

namespace DotCharter.Services.Import.GuitarPro;

/// <summary>How a Guitar Pro score lands on the open project.</summary>
public enum GpImportMode
{
    /// <summary>Each chosen track becomes a new arrangement, on the score's own tempo map.</summary>
    AddNew,

    /// <summary>Only techniques (bends, slides, flags) fold onto arrangements the project already has.</summary>
    TechniqueOnly,

    /// <summary>Only what changed between an older and this version of the score folds onto them.</summary>
    DiffCompare
}

/// <summary>One chosen track, and the arrangement it merges into (technique-only and diff-compare).</summary>
public sealed record GpTrackTarget(string Name, Arrangement? Target);

/// <summary>
///     The apply half of a Guitar Pro import, run on the UI thread against the
///     LIVE project after the score was parsed off it — and always as ONE undo
///     step. Add-new replaces the beat map, which moves every already-charted
///     element in wall-clock time; technique-only overwrites flags in place.
///     Both are destructive, so neither may happen outside the history.
/// </summary>
public sealed class GpImportApplier(
    ProjectState state,
    IChartEditorSurface editor,
    IEditorTransport transport,
    IConfigurationService config,
    IChartMaintenanceService maintenance,
    IAppLogger logger,
    ILocalizationService localization)
{
    public void Apply(GpImportMode mode, string filePath, IReadOnlyList<GpTrackTarget> tracks, GpImportResult parsed,
        GpImportResult? older)
    {
        var appConfig = config.LoadAppConfig();
        maintenance.RunAsSingleUndoStep(() =>
        {
            switch (mode)
            {
                case GpImportMode.AddNew:
                    ApplyAsNewArrangements(filePath, parsed, appConfig);
                    break;
                case GpImportMode.TechniqueOnly:
                    ApplyMerge(tracks, parsed, null, diff: false);
                    break;
                case GpImportMode.DiffCompare:
                    ApplyMerge(tracks, parsed, older, diff: true);
                    break;
            }
        });
    }

    private void ApplyAsNewArrangements(string filePath, GpImportResult result, AppConfig appConfig)
    {
        var imported = result.Arrangements;

        // The Guitar Pro beat map MUST land BEFORE spacing enforcement: the
        // enforcer resolves positions to MILLISECONDS against the map it is
        // handed, and ProjectState.Beats IS the list ApplyTempoAndKey refills —
        // enforcing first measured the chart against a grid thrown away one call
        // later. ApplyTempoAndKey still reads the OLD first beat for its start
        // offset, so this is a resequence, never a clear-first.
        ApplyTempoAndKey(result.Metadata);

        foreach (var arrangement in imported)
            ChartSpacingEnforcer.EnforceArrangement(arrangement, state.Beats, appConfig);
        logger.Info(AppLogCategory.Import,
            $"Guitar Pro tracks imported and spacing enforced. File='{filePath}', arrangements={imported.Count}, names={string.Join(", ", imported.Select(a => a.Name))}.");

        var project = state.ActiveProject;
        if (project == null) return;
        RemoveDefaultEmptyArrangement(project);
        foreach (var arrangement in imported) project.Arrangements.Add(arrangement);
        if (state.ActiveArrangement == null && project.Arrangements.Count > 0)
            state.ActiveArrangement = project.Arrangements[0];

        editor.RequestRedraw();
        logger.Info(AppLogCategory.Import,
            $"Guitar Pro arrangements added to project. Total arrangements={project.Arrangements.Count}, tempoPoints={result.Metadata.TempoPoints.Count}, bars={result.Metadata.Bars.Count}, keyAccidentals={result.Metadata.KeyAccidentals}, keyIsMinor={result.Metadata.KeyIsMinor}.");
        state.SetMessage(localization.Format("Status_GpImport_AddNewApplied", string.Join(", ", imported.Select(a => a.Name))),
            StatusSeverity.Success);
    }

    private void ApplyMerge(IReadOnlyList<GpTrackTarget> tracks, GpImportResult newer, GpImportResult? older, bool diff)
    {
        var (updated, scanned, unmatched, fields) = (0, 0, 0, 0);
        foreach (var track in tracks)
        {
            var source = ByName(newer, track.Name);
            var old = older == null ? null : ByName(older, track.Name);
            if (source == null || track.Target is not { } target)
            {
                logger.Warning(AppLogCategory.Import,
                    $"GP {(diff ? "diff-compare" : "technique-only")}: skipped track '{track.Name}' (src={source?.Name ?? "null"}, old={old?.Name ?? "n/a"}, dst={track.Target?.Name ?? "null"}).");
                continue;
            }

            var stats = diff && old != null
                ? GpTechniqueMerger.ApplyDiff(target, old, source)
                : GpTechniqueMerger.ApplyTechniquesOnly(target, source);
            updated += stats.NotesUpdated;
            scanned += stats.NotesScanned;
            unmatched += stats.NotesUnmatched;
            fields += stats.FieldsChanged;
            logger.Info(AppLogCategory.Import,
                $"GP {(diff ? "diff-compare" : "technique-only")} merged. src='{source.Name}' (vs old='{old?.Name ?? "n/a"}') → dst='{target.Name}': scanned={stats.NotesScanned} updated={stats.NotesUpdated} unmatched={stats.NotesUnmatched} fields={stats.FieldsChanged} " +
                $"chords={stats.ChordsScanned}/{stats.ChordsUpdated} chordsUnmatched={stats.ChordsUnmatched}.");
        }

        editor.RequestRedraw();
        state.SetMessage(diff
            ? localization.Format("Status_GpImport_DiffApplied", updated, scanned, unmatched, fields)
            : localization.Format("Status_GpImport_TechniqueApplied", updated, scanned, unmatched, fields));
    }

    private static Arrangement? ByName(GpImportResult result, string name) =>
        result.Arrangements.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? result.Arrangements.FirstOrDefault();

    /// <summary>A fresh project's one untouched stub arrangement goes, rather than sitting beside the imports.</summary>
    private void RemoveDefaultEmptyArrangement(ChartProject project)
    {
        if (project.Arrangements.Count != 1) return;
        var only = project.Arrangements[0];
        if (only.Levels.Any(l => l.Notes.Count > 0 || l.Chords.Count > 0 || l.HandShapes.Count > 0 || l.Anchors.Count > 0))
            return;

        logger.Info(AppLogCategory.Import, $"Removing default empty arrangement '{only.Name}' before adding GP imports.");
        if (ReferenceEquals(state.ActiveArrangement, only)) state.ActiveArrangement = null;
        project.Arrangements.Remove(only);
    }

    /// <summary>
    ///     Replaces the beat map with the score's tempo/time-signature map (unless
    ///     it is locked) and copies the key and the song metadata over. Never
    ///     touches the arrangements, which is why it can run before they are
    ///     enforced and added.
    /// </summary>
    private void ApplyTempoAndKey(GpImportMetadata meta)
    {
        var project = state.ActiveProject;
        if (project == null) return;

        if (project.IsBeatMapLocked)
        {
            logger.Info(AppLogCategory.Import, "Beat map is locked — tempo/time-signature import skipped.");
        }
        else if (meta.Bars.Count > 0)
        {
            var appConfig = config.LoadAppConfig();
            // The EARLIEST tempo point by (bar, position), not the first one the parser appended.
            var initialBpm = GpBeatMapBuilder.InitialBpm(meta, project.Bpm);
            if (initialBpm <= 0) initialBpm = 120.0;

            var startOffset = project.Beats.Count > 0 ? project.Beats[0].Time : 0.0;
            var newBeats = GpBeatMapBuilder.Build(meta, initialBpm, startOffset,
                appConfig.GpImportTimeSignatures, appConfig.GpImportDropMidBeatTempoChanges);
            project.Beats.Clear();
            foreach (var beat in newBeats) project.Beats.Add(beat);

            project.Bpm = initialBpm;
            transport.Bpm = initialBpm;
            logger.Info(AppLogCategory.Import,
                $"Applied Guitar Pro tempo map. bars={meta.Bars.Count}, tempoPoints={meta.TempoPoints.Count}, initialBpm={initialBpm:0.###}, beats={newBeats.Count}, importTs={appConfig.GpImportTimeSignatures}, dropMidBeatTempo={appConfig.GpImportDropMidBeatTempoChanges}.");
        }

        // The key is informational, so the tempo lock does not gate it.
        project.Chart.KeyAccidentals = meta.KeyAccidentals;
        project.Chart.KeyIsMinor = meta.KeyIsMinor;
        ApplyScoreMetadata(project, meta);
    }

    /// <summary>
    ///     A NON-EMPTY score value wins (the import is one undo step); an empty
    ///     one never erases what the user authored — Guitar Pro writes blanks for
    ///     unfilled properties. Only fields the .rscp round-trips are mapped.
    /// </summary>
    private void ApplyScoreMetadata(ChartProject project, GpImportMetadata meta)
    {
        var applied = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(meta.Title))
        {
            project.Chart.SongName = meta.Title;
            applied.Add($"title='{meta.Title}'");
        }

        if (!string.IsNullOrWhiteSpace(meta.Artist))
        {
            project.Chart.Artist = meta.Artist;
            applied.Add($"artist='{meta.Artist}'");
        }

        if (!string.IsNullOrWhiteSpace(meta.Album))
        {
            project.Chart.AlbumName = meta.Album;
            applied.Add($"album='{meta.Album}'");
        }

        if (!string.IsNullOrWhiteSpace(meta.Tabber))
        {
            project.Chart.Charter = meta.Tabber;
            applied.Add($"charter='{meta.Tabber}'");
        }

        if (applied.Count > 0)
            logger.Info(AppLogCategory.Import, $"Applied Guitar Pro score metadata: {string.Join(", ", applied)}.");
    }
}
