using DotCharter.Core.Domain.Project;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The export dialog's file pickers: each answers the chosen path (and remembers its folder), or
///     null when dismissed. Also owns publication receipts and localized failure prompts; the screen keeps state.
/// </summary>
internal sealed class RocksmithExportPickers(ChartEditDialogServices edit, RocksmithExportServices io)
{
    private string? _confirmedOutput;
    private bool _followingName;
    private RocksmithTestPackageOwnership? _testOwnership;
    private string? _testOwnershipProjectPath;
    private string _testOwnershipProjectId = "";
    private string _testOwnershipFolder = "";

    public Task ShowFailureAsync(Exception exception, string outputPath)
    {
        var loc = edit.Localization;
        if (exception is PlayOnlyContentException)
        {
            edit.Logger.Info(AppLogCategory.Project, exception.Message);
            return io.Prompt.ShowAsync(loc["Dialog_Export_Title"], loc[PlayOnlyExportGuard.MessageKey],
                MessageDialogKind.Error, exception.Message);
        }
        var message = exception is RocksmithTestPackageOwnershipException
            ? loc[RocksmithTestPackageOwnershipException.MessageKey]
            : loc.Format("Status_Export_Failed", exception.Message);
        edit.Logger.Error(AppLogCategory.Project, $"Rocksmith export failed. Output='{outputPath}'.", exception);
        return io.Prompt.ShowAsync(loc["Dialog_Export_Title"], message, MessageDialogKind.Error, exception.ToString());
    }

    public TestBuild PlanTestBuild(RocksmithExportViewModel screen, ChartProject project, ChartProject snapshot, string derivedKey)
    {
        var folder = ResolveTestFolder(screen);
        var dlcKey = string.IsNullOrWhiteSpace(screen.DlcKey) ? derivedKey : screen.DlcKey.Trim();
        var plan = RocksmithTestBuild.Plan(snapshot, io.Files, io.Game, folder, dlcKey);
        return new TestBuild(plan, TestOwnership(project, folder), folder);
    }

    public sealed record TestBuild(RocksmithTestPlan Plan, RocksmithTestPackageOwnership Ownership, string Folder);

    private RocksmithTestPackageOwnership TestOwnership(ChartProject snapshot, string folder)
    {
        var projectPath = string.IsNullOrWhiteSpace(edit.State.CurrentProjectPath)
            ? null : Path.GetFullPath(edit.State.CurrentProjectPath);
        var directory = Path.GetFullPath(folder);
        if (_testOwnership == null || !string.Equals(projectPath, _testOwnershipProjectPath, StringComparison.OrdinalIgnoreCase) ||
            snapshot.ProjectId != _testOwnershipProjectId || !string.Equals(directory, _testOwnershipFolder, StringComparison.OrdinalIgnoreCase))
        {
            _testOwnership = io.TestOwnershipFactory?.Invoke(projectPath, snapshot.ProjectId, directory)
                ?? new RocksmithTestPackageOwnership(projectPath, snapshot.ProjectId, directory);
            _testOwnershipProjectPath = projectPath;
            _testOwnershipProjectId = snapshot.ProjectId;
            _testOwnershipFolder = directory;
        }
        return _testOwnership;
    }

    public void OutputEdited(Action updateName)
    {
        if (!_followingName) _confirmedOutput = null;
        updateName();
    }

    public void SetOutputName(RocksmithExportViewModel screen, string path)
    {
        var following = _followingName;
        _followingName = true;
        try
        {
            screen.OutputPath = path;
        }
        finally
        {
            _followingName = following;
        }
    }

    public async Task BrowseOutputAsync(RocksmithExportViewModel screen, bool tagged)
    {
        var path = await OutputAsync(screen.OutputPath, tagged);
        if (path == null) return;
        SetOutputName(screen, path);
        _confirmedOutput = screen.OutputPath;
    }

    public async Task ConfirmOutputAsync(string path, CancellationToken cancellationToken)
    {
        if (_confirmedOutput == null || string.Equals(path, _confirmedOutput, StringComparison.OrdinalIgnoreCase) || !io.Files.FileExists(path)) return;
        var replace = await io.Prompt.ShowAsync(edit.Localization["Dialog_Export_Title"],
            edit.Localization.Format("Dialog_Export_OverwritePackage", path), MessageDialogKind.Warning, confirmation: true)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!replace) throw new OperationCanceledException(cancellationToken);
    }

    public Func<string, CancellationToken, Task>? BeforePublish(bool test) => test ? null : ConfirmOutputAsync;

    public void PublishedOutput(string path, bool test, ExportPlatform platform)
    {
        if (!test && platform != ExportPlatform.Mac && _confirmedOutput != null) _confirmedOutput = path;
    }

    public async Task<string?> OutputAsync(string current, bool tagged)
    {
        var config = edit.Config.LoadAppConfig();
        var suggested = string.IsNullOrWhiteSpace(current) ? "package_p.psarc" : Path.GetFileName(current);
        suggested = ExtendedRangePackageName.Apply(suggested, tagged);
        var path = await io.Storage.SaveFilePickerAsync(edit.Localization["Picker_SaveExportPackage"], suggested,
            [new FilePickerFilter("Rocksmith Package", ["*.psarc"])],
            !string.IsNullOrWhiteSpace(config.LastExportDir) && io.Files.DirectoryExists(config.LastExportDir)
                ? config.LastExportDir : current);
        if (string.IsNullOrEmpty(path)) return null;
        var final = RocksmithExportSupport.CompleteOutputName(path, tagged);
        if (final != path && io.Files.FileExists(final))
        {
            var replace = await io.Prompt.ShowAsync(edit.Localization["Dialog_Export_Title"],
                edit.Localization.Format("Dialog_Export_OverwritePackage", final), MessageDialogKind.Warning, confirmation: true);
            if (!replace) return null;
        }
        edit.Config.RememberDirectory(final, (c, d) => c.LastExportDir = d);
        return final;
    }

    public async Task<string?> AudioAsync(string current)
    {
        var config = edit.Config.LoadAppConfig();
        var path = await io.Storage.OpenFilePickerAsync(edit.Localization.Format("Picker_SelectExportAudio", ExportAudioFormats.DisplayText),
            [new FilePickerFilter(edit.Localization["Picker_AudioFiles"], ExportAudioFormats.PickerPatterns), FilePickerFilters.AllFiles],
            string.IsNullOrWhiteSpace(current) ? config.LastAudioDir : current);
        if (string.IsNullOrEmpty(path)) return null;
        edit.Config.RememberDirectory(path, (c, d) => c.LastAudioDir = d);
        return path;
    }

    public async Task<string?> AlbumArtAsync(string current)
    {
        var config = edit.Config.LoadAppConfig();
        var path = await io.Storage.OpenFilePickerAsync(edit.Localization["Picker_SelectAlbumArt"],
            [new FilePickerFilter("Images", ["*.png", "*.jpg", "*.jpeg"]), FilePickerFilters.AllFiles],
            string.IsNullOrWhiteSpace(current) ? config.LastAlbumArtDir : current);
        if (string.IsNullOrEmpty(path)) return null;
        edit.Config.RememberDirectory(path, (c, d) => c.LastAlbumArtDir = d);
        return path;
    }

    public Task<string?> FolderAsync(string start, string titleKey = "Picker_SelectTestFolder") =>
        io.Storage.OpenFolderPickerAsync(edit.Localization[titleKey], start);

    public async Task BrowseTestFolderAsync(RocksmithExportViewModel screen) =>
        screen.TestFolder = await FolderAsync(ResolveTestFolder(screen)) ?? screen.TestFolder;

    private static string ResolveTestFolder(RocksmithExportViewModel screen) =>
        string.IsNullOrWhiteSpace(screen.TestFolder) ? screen.TestFolderWatermark : screen.TestFolder.Trim();
}

/// <summary>Where a test export goes and what it changes on the private copy of the chart it builds.</summary>
internal static class RocksmithTestBuild
{
    /// <summary>Where a blank test folder points: the folder of the normal output.</summary>
    public static string DefaultFolder(string outputPath, string documents)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return documents;
        return Path.GetDirectoryName(outputPath) is { Length: > 0 } dir ? dir : Path.GetFullPath(".");
    }

    /// <summary>What a test export writes and removes, from the folder and the game as they are now.</summary>
    public static RocksmithTestPlan Plan(ChartProject snapshot, IExportTargetWriter files, IRocksmithGameProbe game, string folder, string dlcKey)
    {
        var root = folder.TrimEnd('\\', '/');
        string[] inFolder = files.DirectoryExists(folder)
            ? [.. files.FindFiles(folder, "*").Where(f => Path.GetDirectoryName(f)?.TrimEnd('\\', '/') == root)]
            : [];
        return RocksmithTestExport.Plan(snapshot, dlcKey, inFolder, game.IsRunning);
    }

    /// <summary>Versions the title of <paramref name="clone" /> when the plan does, and answers the package path.</summary>
    public static string Prepare(ChartProject clone, RocksmithTestPlan plan, string folder, bool mac, bool tagged = false)
    {
        var path = Path.Combine(folder, RocksmithPackageName.For(clone, RocksmithTestExport.Version,
            mac, tagged, test: true, testVersionTag: plan.VersionTag));
        if (plan.TitleSuffix.Length > 0)
        {
            clone.Chart.SongName += plan.TitleSuffix;
            if (!string.IsNullOrWhiteSpace(clone.Chart.JapaneseTitle)) clone.Chart.JapaneseTitle += plan.TitleSuffix;
        }

        return path;
    }
}
