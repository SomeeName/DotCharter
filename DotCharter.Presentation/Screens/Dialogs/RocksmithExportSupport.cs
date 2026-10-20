using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What the export dialog says or remembers, kept out of its view model: no state of its own.</summary>
internal static class RocksmithExportSupport
{
    public static string MacBlockText(ILocalizationService loc, RocksmithMacBlock block) => loc[block == RocksmithMacBlock.ExtendedStrings
        ? "Dialog_Export_MacSixStringsOnly"
        : "Dialog_Export_MacNeedsEncryption"];

    /// <summary>
    ///     What the platform choice will write, under the picker: both file names for PC + Mac, and, when the Mac package
    ///     is ruled out, why. Empty for PC. <paramref name="packagePath" /> answers one platform's path (<c>mac</c>).
    /// </summary>
    public static string PlatformNote(ILocalizationService loc, ExportTargetViewModel target, RocksmithMacBlock? block,
        string outputPath, Func<bool, string> packagePath)
    {
        if (!target.IncludesMac) return "";
        if (block is { } blocked)
        {
            return target.IsMacOnly
                ? MacBlockText(loc, blocked)
                : loc.Format("Dialog_Export_PcOnlyBecause", MacBlockText(loc, blocked));
        }

        if (target.IsMacOnly || string.IsNullOrWhiteSpace(outputPath)) return "";
        return loc.Format("Dialog_Export_WritesBoth", Path.GetFileName(packagePath(false)), Path.GetFileName(packagePath(true)));
    }

    /// <summary>DLC Builder's loudness for <paramref name="path" /> off the UI thread: the volume, or why there is none.</summary>
    public static async Task<(double? Volume, string? Error)> MeasureVolumeAsync(string path, IAppLogger logger)
    {
        try
        {
            return (await Task.Run(() => RocksmithAutoVolume.Calculate(path)), null);
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Project, $"Auto volume failed for '{path}': {ex.Message}");
            return (null, ex.Message);
        }
    }

    public static void Reveal(IShellLauncher shell, IAppLogger logger, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            shell.RevealInFileManager(path);
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Project, $"Could not open export folder: {ex.Message}");
        }
    }

    /// <summary>The version typed in the dialog becomes the song's, as one undo step.</summary>
    public static void CommitVersion(IHistoryService history, ChartData? chart, string version)
    {
        if (chart is null || version == chart.Version) return;
        var before = SongMetadataFields.From(chart);
        history.Do(new SetSongMetadataAction(chart, before, before with { Version = version }));
    }

    /// <summary>Remembering the folder and the volumes must never fail an export that already landed.</summary>
    public static void Remember(ChartEditDialogServices edit, string psarcPath, bool test, string appId, string testFolder,
        double audioVolume, double previewVolume, bool includeVocals)
    {
        try
        {
            var config = edit.Config.LoadAppConfig();
            config.ExportTestFolder = testFolder.Trim();
            // A test build goes to the test folder, which is not where the next release should default to.
            if (!test && Path.GetDirectoryName(psarcPath) is { Length: > 0 } dir) config.LastExportDir = dir;
            config.LastExportAppId = string.IsNullOrWhiteSpace(appId) ? "248750" : appId.Trim();
            config.ExportAudioVolume = audioVolume;
            config.ExportPreviewVolume = previewVolume;
            config.ExportIncludeVocals = includeVocals;
            edit.Config.SaveAppConfig(config);
        }
        catch (Exception ex)
        {
            edit.Logger.Warning(AppLogCategory.Project, $"Could not remember the export preferences: {ex.Message}");
        }
    }

    /// <summary>Opens the Tone designer over the export, when there is a project.</summary>
    public static void OpenToneDesigner(IDialogs dialogs, IAppLogger logger, ChartProject? project)
    {
        if (project is null) return;
        try
        {
            dialogs.ShowAsync<IToneDesignerScreen, bool>().Observe(logger, "tone designer");
        }
        catch (InvalidOperationException ex)
        {
            logger.Error(AppLogCategory.Project, "The tone designer could not open.", ex);
        }
    }
}
