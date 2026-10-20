using DotCharter.Localization;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.State;

namespace DotCharter.Services.Session;

/// <summary>What the New project dialog answers: the confirmed song details, the picked audio (null for an
///     empty project) and the cover art the audio carried.</summary>
public sealed record NewProjectDetails(
    string Artist,
    string Title,
    string Album,
    string AlbumYear,
    string ReleaseDate,
    string Genre,
    string JapaneseArtist,
    string JapaneseTitle,
    byte[]? CoverArtPng,
    string? AudioFilePath);

/// <summary>
///     Creates the project the New project dialog described, once it has closed: asks
///     before replacing the open project, picks the folder, bakes the configured silence
///     into the working audio off the UI thread, and lays a default 4/4 grid down where
///     the music starts. An empty project gets the same grid over a fixed timeline.
/// </summary>
public sealed class NewProjectCreator(
    IChartEditorSurface editor,
    IEditorTransport transport,
    ProjectState state,
    IProjectSessionService session,
    IFileStorageService storage,
    IConfigurationService config,
    IProjectDocumentService documents,
    ITempoService tempo,
    IExportTargetWriter files,
    IAppLogger logger,
    PrepareImportAudio prepareAudio,
    ILocalizationService localization)
{
    /// <summary>Timeline of an empty project's default grid; loading audio later re-derives the length.</summary>
    private const double EmptyProjectTimelineMs = 5 * 60_000.0;

    /// <summary>Reports every failure itself, on the status bar.</summary>
    public async Task CreateAsync(NewProjectDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        // Creating a project REPLACES the open one (ApplyProject(resetHistory: true)
        // clears the undo history), so ask once, ahead of the folder picker, for both
        // the audio-backed and the empty path.
        if (!await session.MayReplaceOpenProjectAsync())
        {
            logger.Info(AppLogCategory.Project,
                $"New project cancelled — the user kept the unsaved project. Artist='{details.Artist}', title='{details.Title}'.");
            return;
        }

        var appConfig = config.LoadAppConfig();
        var baseDir = await storage.OpenFolderPickerAsync(localization["Picker_SelectProjectFolder"], appConfig.LastProjectFolderDir);
        if (string.IsNullOrEmpty(baseDir))
        {
            logger.Info(AppLogCategory.Project,
                $"New project creation canceled at folder picker. Artist='{details.Artist}', title='{details.Title}'.");
            return;
        }

        config.RememberPickedFolder(baseDir, (c, d) => c.LastProjectFolderDir = d);

        if (string.IsNullOrEmpty(details.AudioFilePath))
        {
            CreateEmptyProject(details, baseDir, appConfig);
            return;
        }

        var audioFilePath = details.AudioFilePath;
        try
        {
            logger.Info(AppLogCategory.Project,
                $"Creating new project. Artist='{details.Artist}', title='{details.Title}', baseDir='{baseDir}', audio='{audioFilePath}'.");
            // The working audio is written into this folder at once and project.rscp
            // follows on the first save, so an existing project's folder is never reused.
            var projectDir = NewProjectFolder.Create(baseDir, FolderName(details));

            // Silence padding (EOF-exact): the leading amount is BAKED into the working
            // audio and the grid starts at that boundary. The grid offset is the same
            // whether the bake succeeded or fell back to a verbatim copy.
            var leadingSeconds = appConfig.PadSilenceOnNewProject ? Math.Max(0, appConfig.LeadingSilenceSeconds) : 0;
            var trailingSeconds = appConfig.PadSilenceOnNewProject ? Math.Max(0, appConfig.TrailingSilenceSeconds) : 0;
            var leadingMs = leadingSeconds * 1000.0;

            // Off the UI thread: this re-encodes the whole song through ffmpeg.
            var prepared = await Task.Run(() => prepareAudio(audioFilePath, projectDir, leadingSeconds, trailingSeconds));
            state.CurrentAudioFileName = prepared.WorkingAudioFileName;
            var destAudioPath = Path.Combine(projectDir, prepared.WorkingAudioFileName);
            if (appConfig.PadSilenceOnNewProject && !prepared.SilenceBaked)
            {
                logger.Warning(AppLogCategory.Project,
                    $"Silence bake unavailable; copied audio verbatim and shifted the timeline by " +
                    $"{leadingSeconds:0.###}s only. path='{destAudioPath}'.");
            }

            state.CurrentTimeMs = 0;
            var project = documents.CreateNewProject(details.Title, details.Artist, state.CurrentAudioFileName);
            if (string.IsNullOrWhiteSpace(project.Chart.Charter)) project.Chart.Charter = appConfig.DefaultCharterName;
            // The silence ADDED to the front of the main audio — only when really baked.
            if (prepared.SilenceBaked) project.Chart.MainAudioOffsetAdded = leadingMs;
            ApplyChartMetadata(project, details);
            TryWriteCoverArt(project, projectDir, details.CoverArtPng);

            editor.ApplyProject(project, resetHistory: true);
            state.CurrentProjectPath = Path.Combine(projectDir, "project.rscp");
            // DIRTY on purpose: nothing has written that path yet
            // (see IProjectSessionService.MarkProjectClean).
            session.MarkProjectDirty();

            await session.LoadAudioFile(destAudioPath, false);
            if (transport.Bpm <= 0) transport.Bpm = 120.0;
            tempo.GenerateInitialBeats(editor.Beats, state.TotalAudioLengthMs, transport.Bpm, leadingMs);
            editor.ExecuteSeek(leadingMs);
            editor.RequestRedraw();

            logger.Info(AppLogCategory.Project,
                $"New project created. ProjectDir='{projectDir}', projectPath='{state.CurrentProjectPath}', workingAudio='{destAudioPath}', silenceBaked={prepared.SilenceBaked}, original='{prepared.OriginalFileName ?? "<none>"}', arrangements={project.Arrangements.Count}, beats={editor.Beats.Count}, leadingSilenceMs={leadingMs:0.###}, coverArt='{project.Chart.CoverArtPath}'.");
            state.SetMessage(localization.Format("Status_CreatedProject", details.Title, details.Artist), StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            logger.Error(AppLogCategory.Project,
                $"Error creating new project. Artist='{details.Artist}', title='{details.Title}', baseDir='{baseDir}', audio='{audioFilePath}'.",
                ex);
            state.SetMessage(localization.Format("Status_CreateProjectFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    ///     Pure filename → (artist, title) for the "Artist - Title" convention: splits on
    ///     the FIRST " - "; with none, the whole name is the title. Never throws.
    /// </summary>
    public static (string Artist, string Title) ParseArtistTitleFromFileName(string audioFilePath)
    {
        var name = (Path.GetFileNameWithoutExtension(audioFilePath ?? "") ?? "").Trim();
        if (name.Length == 0) return ("", "");

        var idx = name.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0)
        {
            var artist = name[..idx].Trim();
            var title = name[(idx + 3)..].Trim();
            if (artist.Length > 0 && title.Length > 0) return (artist, title);
        }

        return ("", name);
    }

    /// <summary>The empty project: the default arrangement and a 4/4 grid at the current BPM. Its only caller,
    ///     <see cref="CreateAsync" />, has already asked before replacing the open project.</summary>
    private void CreateEmptyProject(NewProjectDetails details, string baseDir, AppConfig appConfig)
    {
        try
        {
            logger.Info(AppLogCategory.Project,
                $"Creating new empty project (no audio). Artist='{details.Artist}', title='{details.Title}', baseDir='{baseDir}'.");
            var projectDir = NewProjectFolder.Create(baseDir, FolderName(details));

            state.CurrentAudioFileName = "";
            state.CurrentTimeMs = 0;
            state.TotalAudioLengthMs = 0;
            state.WaveformData = null;
            session.FreeAudio();

            var project = documents.CreateNewProject(details.Title, details.Artist, "");
            if (string.IsNullOrWhiteSpace(project.Chart.Charter)) project.Chart.Charter = appConfig.DefaultCharterName;
            ApplyChartMetadata(project, details);

            editor.ApplyProject(project, resetHistory: true);
            state.CurrentProjectPath = Path.Combine(projectDir, "project.rscp");
            session.MarkProjectDirty();

            if (transport.Bpm <= 0) transport.Bpm = 120.0;
            tempo.GenerateInitialBeats(editor.Beats, EmptyProjectTimelineMs, transport.Bpm);
            editor.ExecuteSeek(0);
            editor.RequestRedraw();

            logger.Info(AppLogCategory.Project,
                $"New empty project created. ProjectDir='{projectDir}', projectPath='{state.CurrentProjectPath}', arrangements={project.Arrangements.Count}, beats={editor.Beats.Count}, bpm={transport.Bpm:0.###}.");
            state.SetMessage(localization.Format("Status_CreatedEmptyProject", details.Title, details.Artist), StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            logger.Error(AppLogCategory.Project,
                $"Error creating new empty project. Artist='{details.Artist}', title='{details.Title}', baseDir='{baseDir}'.", ex);
            state.SetMessage(localization.Format("Status_CreateProjectFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>Song name and artist always; the rest only when non-empty, so a blank keeps the model default.</summary>
    private static void ApplyChartMetadata(ChartProject project, NewProjectDetails details)
    {
        project.Chart.SongName = details.Title;
        project.Chart.Artist = details.Artist;
        project.Chart.JapaneseTitle = details.JapaneseTitle.Trim();
        project.Chart.JapaneseArtist = details.JapaneseArtist.Trim();
        if (!string.IsNullOrWhiteSpace(details.Album)) project.Chart.AlbumName = details.Album;
        if (!string.IsNullOrWhiteSpace(details.AlbumYear)) project.Chart.AlbumYear = details.AlbumYear;
        if (!string.IsNullOrWhiteSpace(details.ReleaseDate)) project.Chart.ReleaseDate = details.ReleaseDate;
        if (!string.IsNullOrWhiteSpace(details.Genre)) project.Chart.Genre = details.Genre;
    }

    /// <summary>Writes the cover as <c>cover.png</c>; best-effort, a failure never aborts the creation.</summary>
    private void TryWriteCoverArt(ChartProject project, string projectDir, byte[]? png)
    {
        if (png is not { Length: > 0 }) return;
        try
        {
            var coverPath = Path.Combine(projectDir, "cover.png");
            files.WriteAllBytes(coverPath, png);
            project.Chart.CoverArtPath = "cover.png";
            logger.Info(AppLogCategory.Project, $"Wrote cover art. path='{coverPath}', bytes={png.Length}.");
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Project, $"Failed to write cover art to project folder. {ex.Message}");
        }
    }

    private static string FolderName(NewProjectDetails details)
    {
        var folderName = $"{details.Artist} - {details.Title}";
        foreach (var c in Path.GetInvalidFileNameChars()) folderName = folderName.Replace(c, '_');
        return folderName;
    }
}
