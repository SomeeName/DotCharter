using System.Globalization;
using DotCharter.Localization;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Session;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
/// Tempo map ▸ Add silence, the verb behind
///     <see cref="IChartMaintenanceService.AddSilenceAtBeginningAsync" /> and
///     <see cref="IChartMaintenanceService.AddSilenceAtEndAsync" />: padded copies of the song
///     and its stems are written beside the originals in the project folder, and the project
///     switches to them in ONE undo step. At the start the chart moves with the music
///     (<see cref="SilenceSpliceTimeline" />); at the end nothing moves. Undo points the project
///     back at the original files, which are never touched.
///     <para>
///         <see cref="ChartMaintenanceService" /> owns it and hands it the whole-project
///         snapshot step every tempo verb records, so the undo node is the same kind the
///         other tempo-map verbs push; this class adds only the audio reload around it.
///     </para>
/// </summary>
internal sealed class ProjectSilencePadder(
    ProjectState projectState,
    IProjectSessionService projectSession,
    IHistoryService history,
    IAppLogger logger,
    ILocalizationService localization,
    IAudioSilenceSplicer? splicer,
    Func<ChartProject, ChartProject, IChartAction> projectSnapshot)
{
    /// <summary>The audio reload the last add-silence step started; awaited before its status line.</summary>
    private Task _pendingAudioReload = Task.CompletedTask;

    /// <summary>Pads <paramref name="seconds" /> of silence at the start or the end; the status
    /// line says what happened, or why nothing did.</summary>
    public async Task AddSilenceAsync(double seconds, bool atStart)
    {
        var project = projectState.ActiveProject;
        if (project == null)
        {
            projectState.MainDisplayMessage = localization["Status_NoChartLoaded"];
            return;
        }

        var sanitized = Math.Max(0, seconds);
        if (sanitized <= 0)
        {
            projectState.MainDisplayMessage = localization["Status_TempoMapOperationNoChange"];
            return;
        }

        var audioPath = ResolveCurrentAudioPath();
        if (audioPath == null)
        {
            projectState.SetMessage(localization["Status_ProjectAudioMissing"], StatusSeverity.Warning);
            return;
        }

        // The padded files are written into the project folder, never beside a source the
        // charter keeps elsewhere, so an unsaved session has nowhere to put them.
        var projectDir = ProjectDirectory();
        if (projectDir == null || splicer == null)
        {
            projectState.SetMessage(localization["Status_AddSilenceNeedsSavedProject"], StatusSeverity.Warning);
            return;
        }

        var amount = sanitized.ToString("0.###", CultureInfo.CurrentCulture);
        projectState.MainDisplayMessage = localization.Format("Status_AddingSilence", amount);
        var stems = ResolveStemFiles(project, projectDir);
        var request = new SilenceSpliceRequest(audioPath, [.. stems.Select(s => s.File)], projectDir,
            atStart ? sanitized : 0, atStart ? 0 : sanitized, projectState.CurrentSongIdentity);
        var result = await splicer.SpliceAsync(request);
        if (!result.Succeeded)
        {
            projectState.SetMessage(localization[result.Failure switch
            {
                SilenceSpliceFailure.NoFfmpeg => "Status_AddSilenceFailed_NoFfmpeg",
                SilenceSpliceFailure.StemFailed => "Status_AddSilenceFailed_Stem",
                _ => "Status_AddSilenceFailed_Audio"
            }], StatusSeverity.Error);
            return;
        }

        if (!ReferenceEquals(projectState.ActiveProject, project))
        {
            // Another song was opened while the files were written; they stay on disk unused.
            logger.Warning(AppLogCategory.Editing, "Add silence finished for a song that is no longer open; not applied.");
            return;
        }

        var before = ChartProjectCloner.Clone(project);
        var after = ChartProjectCloner.Clone(project);
        after.AudioFileName = Path.GetRelativePath(projectDir, result.AudioPath!);
        for (var i = 0; i < stems.Count; i++)
            after.Stems[stems[i].Index].Path = Path.GetRelativePath(projectDir, result.Stems[i].Path);
        if (atStart) SilenceSpliceTimeline.ShiftForLeadingSilence(after, sanitized * 1000.0);

        history.Do(new AudioSpliceAction(this, projectSnapshot(before, after)));
        await _pendingAudioReload;

        projectState.MainDisplayMessage = localization.Format(
            atStart ? "Status_SilenceAddedAtStart" : "Status_SilenceAddedAtEnd", amount);
        logger.Info(AppLogCategory.Editing,
            $"Added {sanitized:0.###} s of silence at the {(atStart ? "start" : "end")}: audio '{after.AudioFileName}', " +
            $"{stems.Count} stem(s) padded{(atStart ? ", chart shifted" : "")}.");
    }

    /// <summary>The project's stems that exist on disk, with their index in <c>Stems</c>. A relative
    /// path is resolved the way the stem mixer resolves it: the project folder, then the audio's.</summary>
    private List<(int Index, SilenceSpliceFile File)> ResolveStemFiles(ChartProject project, string projectDir)
    {
        var audioDir = ProjectAudioLocator.ResolveOutputDirectory(
            projectState.CurrentProjectPath, projectState.CurrentAudioFileName);
        var found = new List<(int, SilenceSpliceFile)>();
        for (var i = 0; i < project.Stems.Count; i++)
        {
            var stem = project.Stems[i];
            // Data from the .rscp: a UNC, device or network-drive path is refused unprobed.
            if (!ProjectRelativePath.IsReadableMediaReference(stem.Path)) continue;
            var path = Path.IsPathRooted(stem.Path)
                ? stem.Path
                : new[] { projectDir, audioDir }
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Select(d => Path.Combine(d!, stem.Path))
                    .FirstOrDefault(File.Exists);
            if (path != null && File.Exists(path)) found.Add((i, new SilenceSpliceFile(stem.Name, path)));
        }

        return found;
    }

    private string? ProjectDirectory()
    {
        var projectPath = projectState.CurrentProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ? null : directory;
    }

    private string? ResolveCurrentAudioPath()
        => ProjectAudioLocator.TryResolve(projectState.CurrentProjectPath, projectState.CurrentAudioFileName);

    /// <summary>
    ///     Points the session at the project's audio file and reloads it. The stem mixer follows the
    ///     audio file name and reloads the stems the project now lists.
    /// </summary>
    private void ReloadProjectAudio()
    {
        var project = projectState.ActiveProject;
        if (project == null) return;
        projectState.CurrentAudioFileName = project.AudioFileName;
        var path = ResolveCurrentAudioPath();
        if (path == null)
        {
            logger.Warning(AppLogCategory.Audio, $"Add silence: '{project.AudioFileName}' is missing; the audio was not reloaded.");
            _pendingAudioReload = Task.CompletedTask;
            return;
        }

        _pendingAudioReload = ReloadAudioAsync(path);
    }

    private async Task ReloadAudioAsync(string path)
    {
        try
        {
            await projectSession.LoadAudioFile(path, generateBeats: false);
        }
        catch (Exception ex)
        {
            logger.Error(AppLogCategory.Audio, $"Add silence: could not load '{path}'.", ex);
        }
    }

    /// <summary>
    /// Add silence: the project snapshot plus the audio it plays. Both ways round, the
    ///     session then plays the snapshot's audio file; the files themselves are never deleted.
    /// </summary>
    private sealed class AudioSpliceAction(ProjectSilencePadder padder, IChartAction snapshot) : IChartAction, ILinkPolicyAction
    {
        /// <summary>Linked measures: a whole-chart tool; placements it makes differ are unlinked, never propagated to.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.UnlinkOnDivergence;

        public long EstimatedBytes => snapshot.EstimatedBytes;

        public void Execute()
        {
            snapshot.Execute();
            padder.ReloadProjectAudio();
        }

        public void Undo()
        {
            snapshot.Undo();
            padder.ReloadProjectAudio();
        }
    }
}
