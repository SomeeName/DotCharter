using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Lyrics;

public enum LyricAutoSyncRunStatus
{
    /// <summary>The aligner produced phrases for the project the run started on.</summary>
    Synced,

    /// <summary>A model is missing and could not be fetched.</summary>
    NoModel,

    /// <summary>The active project has no resolvable audio file.</summary>
    NoAudio,

    /// <summary>The active project changed while the run was in flight; the result must not be applied.</summary>
    ProjectChanged,

    /// <summary>The aligner ran but returned nothing usable; <see cref="LyricAutoSyncRunResult.Error" /> says why.</summary>
    Failed
}

/// <summary>What a run produced. Carries data only: the caller words the status and applies the phrases.</summary>
public sealed record LyricAutoSyncRunResult(
    LyricAutoSyncRunStatus Status,
    LyricAutoSyncOutcome? Outcome = null,
    string? ModelDirectory = null,
    string? Error = null);

/// <summary>
///     The auto-sync workflow behind the lyrics inspector: fetch missing models, resolve the song's audio,
///     align the pasted lyrics against a snapshot of the beat grid, and guard against the project having
///     changed meanwhile. It never touches the chart — applying the phrases (one undo node) stays with the caller.
/// </summary>
public interface ILyricAutoSyncRunner
{
    /// <param name="rawLyrics">The pasted lyric sheet.</param>
    /// <param name="liveBeats">The active project's beat grid; copied before the aligner sees it.</param>
    /// <param name="onModelDownloadStarting">Called synchronously with the megabytes about to be fetched.</param>
    /// <param name="progress">Step text for both the model download and the alignment.</param>
    Task<LyricAutoSyncRunResult> RunAsync(string rawLyrics, IList<BeatMarker> liveBeats,
        Action<int> onModelDownloadStarting, IProgress<string> progress);
}

/// <param name="autoSync">The CTC aligner.</param>
/// <param name="modelDownloads">Null when no downloader is wired — the models then have to be present already.</param>
/// <param name="configService">Null in tests — auto-sync then runs with product-default options.</param>
/// <param name="projectState">Source of the active project, its path and its audio file.</param>
public sealed class LyricAutoSyncRunner(
    ILyricAutoSyncService autoSync,
    ILyricModelDownloadService? modelDownloads,
    IConfigurationService? configService,
    ProjectState projectState) : ILyricAutoSyncRunner
{
    public async Task<LyricAutoSyncRunResult> RunAsync(string rawLyrics, IList<BeatMarker> liveBeats,
        Action<int> onModelDownloadStarting, IProgress<string> progress)
    {
        // Capture the owner and every mutable input before model installation can yield.
        var syncedProject = projectState.ActiveProject;
        var projectPath = projectState.CurrentProjectPath;
        var audioFileName = projectState.CurrentAudioFileName;
        var audioPath = ProjectAudioLocator.TryResolve(projectPath, audioFileName);
        var beats = liveBeats.Select(beat => beat.Clone()).ToList();
        var options = CaptureOptions(syncedProject, projectPath, audioFileName);

        // The models are a one-time download, so a missing one is a step to take
        // rather than a dead end. Fetch what THIS sheet needs — the multilingual
        // model always, the English head only when the router wants it — and only
        // report "no model" if that could not be done.
        if (modelDownloads != null)
        {
            var sheet = LyricSheetParser.Parse(rawLyrics);
            var megabytes = modelDownloads.PendingMegabytesFor(sheet);
            if (megabytes > 0)
            {
                onModelDownloadStarting(megabytes);
                if (!ReferenceEquals(projectState.ActiveProject, syncedProject))
                    return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);

                bool installed;
                try
                {
                    installed = await modelDownloads.EnsureModelsForAsync(sheet, progress);
                }
                catch (Exception) when (!ReferenceEquals(projectState.ActiveProject, syncedProject))
                {
                    return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);
                }

                if (!ReferenceEquals(projectState.ActiveProject, syncedProject))
                    return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);
                if (!installed)
                {
                    return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.NoModel,
                        ModelDirectory: autoSync.ModelDirectory);
                }
            }
        }

        if (!ReferenceEquals(projectState.ActiveProject, syncedProject))
            return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);

        if (!autoSync.IsModelInstalled)
            return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.NoModel, ModelDirectory: autoSync.ModelDirectory);

        if (string.IsNullOrWhiteSpace(audioPath))
            return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.NoAudio);

        if (!ReferenceEquals(projectState.ActiveProject, syncedProject))
            return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);

        LyricAutoSyncOutcome outcome;
        try
        {
            outcome = await autoSync.SyncAsync(rawLyrics, audioPath, beats, options, progress);
        }
        catch (Exception) when (!ReferenceEquals(projectState.ActiveProject, syncedProject))
        {
            return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);
        }

        // Reference identity, including a reopened instance of the same persisted project.
        if (!ReferenceEquals(projectState.ActiveProject, syncedProject))
            return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);

        if (!outcome.Ran || outcome.Phrases.Count == 0)
            return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.Failed, outcome, Error: outcome.Error ?? "no phrases");

        if (!beats.Select(SpacingBasis).SequenceEqual(liveBeats.Select(SpacingBasis)))
        {
            if (liveBeats.Count == 0) return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);
            var currentBeats = liveBeats.Select(beat => beat.Clone()).ToList();
            var project = new ChartProject();
            project.VocalTracks.Add(new VocalTrack { Phrases = [.. outcome.Phrases] });
            project = ChartProjectCloner.Clone(project, retainIdentities: true);
            ChartProjectPositions.Remap(project, position => TimelineMath.PositionFromMs(
                TimelineMath.CalculateTimeMs(position, beats), currentBeats));
            var remapped = project.VocalTracks[0].Phrases;
            LyricPhraseSpacing.CloseGaps(remapped, currentBeats, options.SpacingConfig ?? new AppConfig(),
                options.SpanWordEndToNextWord);
            outcome = outcome with { Phrases = remapped };
        }

        return new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.Synced, outcome);
    }

    private static (double Time, double Bpm, bool IsMeasure, int BeatsInMeasure) SpacingBasis(BeatMarker beat) =>
        (beat.Time, beat.Bpm, beat.IsMeasure, beat.BeatsInMeasure);

    private LyricAutoSyncOptions CaptureOptions(ChartProject? project, string? projectPath, string? audioFileName)
    {
        var config = configService?.LoadAppConfig();
        // Only these settings feed the aligner. Keep its spacing config independent too.
        var captured = config == null ? null : new AppConfig
        {
            LyricSpanWordGap = config.LyricSpanWordGap,
            LyricSpanPhraseGap = config.LyricSpanPhraseGap,
            MinNoteDistanceMode = config.MinNoteDistanceMode,
            MinNoteDistanceMs = config.MinNoteDistanceMs,
            MinNoteDistanceDivisor = config.MinNoteDistanceDivisor
        };
        var options = captured != null ? LyricAutoSyncOptions.FromConfig(captured) : LyricAutoSyncOptions.Default;
        return options with
        {
            ProjectStems = project?.Stems.Select(s => new Stem { Name = s.Name, Path = s.Path, Offset = s.Offset }).ToArray(),
            ProjectPath = projectPath,
            ProjectAudioFileName = audioFileName
        };
    }
}
