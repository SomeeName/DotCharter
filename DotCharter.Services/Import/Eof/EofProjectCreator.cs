using DotCharter.Localization;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Session;
using DotCharter.Services.State;

namespace DotCharter.Services.Import.Eof;

/// <summary>What the Editor on Fire import dialog hands on: the parsed tracks and the audio found beside the file.</summary>
public sealed record EofImportSelection(string FilePath, EofImportResult Import, string AudioPath);

/// <summary>
///     The half of an Editor on Fire import that runs AFTER its dialog closed:
///     ask before replacing the open project, pick the folder, copy the audio,
///     enforce spacing, SAVE, then adopt the saved project and load its audio.
///     <para>
///         The dialog closes before the native pickers on purpose (an OS dialog
///         behind the Avalonia overlay is invisible), so every failure here goes
///         to the status bar. The project is saved BEFORE it is applied: a save
///         that throws never replaces the open project with something that
///         never reached disk.
///     </para>
/// </summary>
public sealed class EofProjectCreator(
    IChartEditorSurface editor,
    IEditorTransport transport,
    ProjectState state,
    IProjectSessionService session,
    IFileStorageService storage,
    IConfigurationService config,
    IProjectDocumentService documents,
    IExportTargetWriter files,
    IAppLogger logger,
    ILocalizationService localization)
{
    /// <summary>
    ///     The OGG/WAV beside the .eof. EOF stores audio externally — typically
    ///     <c>guitar.ogg</c> or <c>song.ogg</c> — so the metadata-recorded name
    ///     is tried first, then the common defaults. Empty when there is none.
    /// </summary>
    public static string FindCompanionAudio(IExportTargetWriter files, string eofPath, EofImportMetadata meta)
    {
        if (string.IsNullOrEmpty(eofPath)) return "";
        var dir = Path.GetDirectoryName(eofPath);
        if (string.IsNullOrEmpty(dir)) return "";

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(meta.AudioFileName)) candidates.Add(meta.AudioFileName);
        candidates.AddRange(["guitar.ogg", "song.ogg", "guitar.wav", "song.wav"]);

        foreach (var name in candidates)
        {
            var path = Path.Combine(dir, name);
            if (files.FileExists(path)) return path;
        }

        return "";
    }

    public async Task CreateAsync(EofImportSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var eofPath = selection.FilePath;

        // The import REPLACES the open project (ApplyProject(resetHistory: true)
        // clears the undo history), so ask before any of it — ahead of the folder
        // picker, while abandoning still costs nothing.
        if (!await session.MayReplaceOpenProjectAsync())
        {
            logger.Info(AppLogCategory.Import,
                $"EOF project creation cancelled — the user kept the unsaved project. File='{eofPath}'.");
            return;
        }

        // New Project and this importer ask the ONE
        // Picker_SelectProjectFolder question, and both start at and
        // write back LastProjectFolderDir.
        var baseDir = await storage.OpenFolderPickerAsync(localization["Picker_SelectProjectFolder"],
            config.LoadAppConfig().LastProjectFolderDir);
        if (string.IsNullOrEmpty(baseDir))
        {
            logger.Info(AppLogCategory.Project, $"EOF project creation canceled at folder picker. File='{eofPath}'.");
            return;
        }

        config.RememberPickedFolder(baseDir, (c, d) => c.LastProjectFolderDir = d);
        state.SetMessage(localization["Status_EofCreatingProject"]);
        try
        {
            var written = await WriteProjectAsync(selection, baseDir);
            var project = written.Project;

            state.CurrentAudioFileName = project.AudioFileName;
            state.CurrentTimeMs = 0;
            session.FreeAudio();

            editor.ApplyProject(project, resetHistory: true);
            transport.Bpm = project.Bpm > 0 ? project.Bpm : 120.0;
            if (state.ActiveArrangement == null && project.Arrangements.Count > 0)
                state.ActiveArrangement = project.Arrangements[0];

            state.CurrentProjectPath = written.ProjectPath;
            // Just written to disk, so this IS the on-disk state (required after a
            // history reset — see IProjectSessionService.MarkProjectClean).
            session.MarkProjectClean();

            await LoadAudioAndReportAsync(written);
        }
        catch (Exception ex)
        {
            logger.Error(AppLogCategory.Project,
                $"EOF project creation failed. File='{eofPath}', baseDir='{baseDir}'.", ex);
            state.SetMessage(localization.Format("Status_CreateProjectFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>Copies the audio, enforces spacing, assembles and SAVES the project — all off the UI thread.</summary>
    private async Task<WrittenProject> WriteProjectAsync(EofImportSelection selection, string baseDir)
    {
        var result = selection.Import;
        var meta = result.Metadata;
        var artist = string.IsNullOrWhiteSpace(meta.Artist) ? "Unknown Artist" : meta.Artist.Trim();
        var title = string.IsNullOrWhiteSpace(meta.Title)
            ? Path.GetFileNameWithoutExtension(selection.FilePath)
            : meta.Title.Trim();

        var folderName = $"{artist} - {title}";
        foreach (var c in Path.GetInvalidFileNameChars()) folderName = folderName.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "Imported";

        // Derived from the metadata, so a second import of the same .eof gets a
        // fresh folder instead of landing on the first one.
        var projectDir = NewProjectFolder.Create(baseDir, folderName);
        var audioFileName = $"song{Path.GetExtension(selection.AudioPath)}";
        var destAudioPath = Path.Combine(projectDir, audioFileName);
        var projectPath = Path.Combine(projectDir, "project.rscp");
        var appConfig = config.LoadAppConfig();

        // Silence padding is NOT applied: the beat map and every note are
        // already aligned to the original audio. The beats go through the
        // importer's own mapping, which carries BeatMarker.IsAnchor.
        var importedBeats = EofImporter.BuildBeatMarkers(meta);

        // Nothing here is attached to the app until the caller applies it.
        var project = await Task.Run(() =>
        {
            files.CopyFile(selection.AudioPath, destAudioPath);
            logger.Info(AppLogCategory.Project,
                $"Copied EOF companion audio. From='{selection.AudioPath}', to='{destAudioPath}'.");

            foreach (var arr in result.Arrangements)
                ChartSpacingEnforcer.EnforceArrangement(arr, importedBeats, appConfig);

            var built = BuildProject(result);
            built.AudioFileName = audioFileName;
            documents.SaveChart(projectPath, built);
            return built;
        });

        return new WrittenProject(project, projectPath, destAudioPath, title, artist);
    }

    private async Task LoadAudioAndReportAsync(WrittenProject written)
    {
        var audioError = "";
        try
        {
            await session.LoadAudioFile(written.AudioPath, false);
        }
        catch (Exception audioEx)
        {
            audioError = $"{audioEx.GetType().Name}: {audioEx.Message}";
            logger.Warning(AppLogCategory.Import,
                $"EOF project audio could not be decoded after copy. Path='{written.AudioPath}'. {audioError}");
        }

        editor.ExecuteSeek(0);
        editor.RequestRedraw();

        logger.Info(AppLogCategory.Project,
            $"EOF project created. ProjectPath='{written.ProjectPath}', audio='{written.AudioPath}', " +
            $"arrangements={written.Project.Arrangements.Count}, beats={written.Project.Beats.Count}, " +
            $"audioLoaded={string.IsNullOrEmpty(audioError)}.");

        // A project whose audio never decoded is HALF an import, and says so.
        state.SetMessage(string.IsNullOrEmpty(audioError)
                ? localization.Format("Status_CreatedProject", written.Title, written.Artist)
                : localization.Format("Status_EofCreatedProjectNoAudio", written.Title, written.Artist, audioError),
            string.IsNullOrEmpty(audioError) ? StatusSeverity.Success : StatusSeverity.Warning);
    }

    private sealed record WrittenProject(ChartProject Project, string ProjectPath, string AudioPath, string Title,
        string Artist);

    /// <summary>A brand-new project from the import, so a second import fully replaces the first.</summary>
    private static ChartProject BuildProject(EofImportResult result)
    {
        var meta = result.Metadata;
        var project = new ChartProject();
        foreach (var arr in result.Arrangements) project.Arrangements.Add(arr);

        if (meta.Beats.Count > 0)
        {
            foreach (var marker in EofImporter.BuildBeatMarkers(meta)) project.Beats.Add(marker);
            project.Bpm = meta.Beats[0].Bpm > 0 ? meta.Beats[0].Bpm : 120.0;
        }

        project.Chart.SongName = !string.IsNullOrWhiteSpace(meta.Title) ? meta.Title : "Imported";
        if (!string.IsNullOrWhiteSpace(meta.Artist))
        {
            project.Chart.Artist = meta.Artist;
            project.Chart.ArtistSort = meta.Artist;
        }

        if (!string.IsNullOrWhiteSpace(meta.Album)) project.Chart.AlbumName = meta.Album;
        if (!string.IsNullOrWhiteSpace(meta.Year)) project.Chart.AlbumYear = meta.Year;
        if (!string.IsNullOrWhiteSpace(meta.Genre)) project.Chart.Genre = meta.Genre;
        if (!string.IsNullOrWhiteSpace(meta.Charter)) project.Chart.Charter = meta.Charter;
        return project;
    }
}
