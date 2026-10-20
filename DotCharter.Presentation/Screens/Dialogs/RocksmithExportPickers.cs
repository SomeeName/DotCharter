using DotCharter.Core.Domain.Project;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The export dialog's file pickers: each answers the chosen path (and remembers its folder), or
///     null when the picker was dismissed. The dialog keeps the choice of which field takes it.
/// </summary>
internal sealed class RocksmithExportPickers(ChartEditDialogServices edit, RocksmithExportServices io)
{
    public async Task<string?> OutputAsync(string current)
    {
        var config = edit.Config.LoadAppConfig();
        var suggested = string.IsNullOrWhiteSpace(current) ? "package_p.psarc" : Path.GetFileName(current);
        var path = await io.Storage.SaveFilePickerAsync(edit.Localization["Picker_SaveExportPackage"], suggested,
            [new FilePickerFilter("Rocksmith Package", ["*.psarc"])],
            string.IsNullOrWhiteSpace(current) ? config.LastExportDir : current);
        if (string.IsNullOrEmpty(path)) return null;
        edit.Config.RememberDirectory(path, (c, d) => c.LastExportDir = d);
        return path;
    }

    public async Task<string?> AudioAsync(string current)
    {
        var config = edit.Config.LoadAppConfig();
        var path = await io.Storage.OpenFilePickerAsync(edit.Localization["Picker_SelectExportAudio"],
            [new FilePickerFilter("Wwise / Wave Audio", ["*.wem", "*.wav"]), FilePickerFilters.AllFiles],
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

    public Task<string?> FolderAsync(string start) =>
        io.Storage.OpenFolderPickerAsync(edit.Localization["Picker_SelectTestFolder"], start);
}

/// <summary>Where a test export goes and what it changes on the private copy of the chart it builds.</summary>
internal static class RocksmithTestBuild
{
    /// <summary>Where a blank test folder points: a "test" folder beside the output file.</summary>
    public static string DefaultFolder(string outputPath, string documents) =>
        Path.Combine(Path.GetDirectoryName(outputPath) is { Length: > 0 } dir ? dir : documents, "test");

    /// <summary>What a test export writes and removes, from the folder and the game as they are now.</summary>
    public static RocksmithTestPlan Plan(IExportTargetWriter files, IRocksmithGameProbe game, string folder, string dlcKey)
    {
        var root = folder.TrimEnd('\\', '/');
        string[] inFolder = files.DirectoryExists(folder)
            ? [.. files.FindFiles(folder, "*").Where(f => Path.GetDirectoryName(f)?.TrimEnd('\\', '/') == root)]
            : [];
        return RocksmithTestExport.Plan(dlcKey, inFolder, game.IsRunning);
    }

    /// <summary>Versions the title of <paramref name="clone" /> when the plan does, and answers the package path.</summary>
    public static string Prepare(ChartProject clone, RocksmithTestPlan plan, string folder, bool mac)
    {
        if (plan.TitleSuffix.Length > 0)
        {
            clone.Chart.SongName += plan.TitleSuffix;
            if (!string.IsNullOrWhiteSpace(clone.Chart.JapaneseTitle)) clone.Chart.JapaneseTitle += plan.TitleSuffix;
        }

        return Path.Combine(folder, plan.FileStem + (mac ? "_m.psarc" : "_p.psarc"));
    }
}
