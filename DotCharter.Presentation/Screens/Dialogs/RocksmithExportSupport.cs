using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

internal delegate bool RocksmithVolumeMeasure(ChartData chart, string audio, string? preview, double? start, Action<string>? warn, int targetLufs);

/// <summary>What the export dialog says or remembers, kept out of its view model: no state of its own.</summary>
internal static class RocksmithExportSupport
{
    public static void FollowTargetName(RocksmithExportViewModel screen, Action updateName, Action gateChanged)
    {
        screen.Target.TargetChanged += (_, _) =>
        {
            screen.SetOutputName(ExportPathDefaults.WithPlatformSuffix(screen.OutputPath, screen.Target.IsMacOnly));
            updateName();
            gateChanged();
        };
        screen.Target.PackageChanged += (_, _) =>
        {
            updateName();
            gateChanged();
        };
    }

    public static void UpdateOutputName(RocksmithExportViewModel screen, bool psxr, Action gateChanged)
    {
        screen.SetOutputName(ExtendedRangePackageName.Apply(screen.OutputPath, psxr && !screen.Target.IsMacOnly));
        gateChanged();
    }

    public static void PrepareOutputName(RocksmithExportViewModel screen, bool tagged)
    {
        if (!FileNameTag(screen.Target, tagged)) return;
        screen.SetOutputName(CompleteOutputName(screen.OutputPath, true));
    }

    public static string CompleteOutputName(string path, bool tagged)
    {
        if (tagged && !path.EndsWith(".psarc", StringComparison.OrdinalIgnoreCase)) path += ".psarc";
        return ExtendedRangePackageName.Apply(path, tagged);
    }

    public static bool IsPsxr(ChartProject? project, IEnumerable<ExportArrangementRow> rows) =>
        project != null && ExtendedRangePackageName.IsPsxr(
            project.Arrangements.Where(a => rows.Any(row => row.IsSelected && row.Name == a.Name)));

    public static ExportPlatform BuildPlatform(ExportTargetViewModel target, RocksmithMacBlock? macBlock) =>
        target.SelectedPlatformOption.Value == ExportPlatform.PcAndMac && macBlock is not null ? ExportPlatform.Pc : target.SelectedPlatformOption.Value;

    public static ExportArrangementRow ArrangementRow(Arrangement a, IReadOnlyDictionary<string, bool>? ticks) => new()
    {
        Name = a.Name, TypeLabel = ArrangementTypeLabel.For(a.Name, a.ArrangementType.ToString()),
        IsSelected = ticks is null || !ticks.TryGetValue(a.Name, out var was) || was,
        IsExtendedString = RocksmithPackageBuilder.IsExtendedStringArrangement(a)
    };

    public static bool FileNameTag(ExportTargetViewModel target, bool tagged) =>
        tagged && !target.IsMacOnly;

    public static void RowChanged(System.ComponentModel.PropertyChangedEventArgs e, Action updateName, Action gateChanged)
    {
        if (e.PropertyName != nameof(ExportArrangementRow.IsSelected)) return;
        updateName();
        gateChanged();
    }

    public static string MacBlockText(ILocalizationService loc, RocksmithMacBlock block) => loc[block switch
    {
        RocksmithMacBlock.RsxrWindowsOnly => "Dialog_Export_RsxrWindowsOnly",
        _ => "Dialog_Export_MacNeedsEncryption"
    }];

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

    /// <summary>Measures export gain for <paramref name="path" /> off the UI thread.</summary>
    public static async Task<(double? Volume, string? Error)> MeasureVolumeAsync(string path, IAppLogger logger, int targetLufs)
    {
        try
        {
            return (await Task.Run(() => RocksmithAutoVolume.Calculate(path, targetLufs)), null);
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Project, $"Auto volume failed for '{path}'", ex);
            return (null, ex.Message);
        }
    }

    public static async Task MeasureVolumeAsync(RocksmithExportViewModel screen, bool preview, ChartEditDialogServices edit)
    {
        screen.VolumeMessage = "";
        screen.IsMeasuringVolume = true;
        var targetLufs = edit.Config.LoadAppConfig().ExportAutoVolumeTargetLufs;
        var path = preview ? screen.PreviewAudioPath : screen.AudioPath;
        var shown = preview ? screen.PreviewVolume : screen.AudioVolume;
        var (volume, error) = await MeasureVolumeAsync(path.Trim(), edit.Logger, targetLufs);
        screen.IsMeasuringVolume = false;
        if (screen.IsDisposed || screen.Result.IsCompleted || edit.Config.LoadAppConfig().ExportAutoVolumeTargetLufs != targetLufs ||
            (path, shown) != (preview ? (screen.PreviewAudioPath, screen.PreviewVolume) : (screen.AudioPath, screen.AudioVolume)))
        {
            return;
        }
        if (volume is null)
        {
            screen.VolumeMessage = edit.Localization.Format("Dialog_Export_AutoVolumeFailed", error ?? "");
        }
        else if (preview)
        {
            screen.PreviewVolume = volume.Value;
            screen.PreviewAutoTargetLufs = targetLufs;
        }
        else
        {
            screen.AudioVolume = volume.Value;
            screen.AudioAutoTargetLufs = targetLufs;
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
            logger.Warning(AppLogCategory.Project, "Could not open export folder", ex);
        }
    }

    /// <summary>The version typed in the dialog becomes the song's, as one undo step.</summary>
    public static void CommitVersion(IHistoryService history, ChartData? chart, string version, string previewStartText)
    {
        CommitPreviewStart(history, chart, previewStartText);
        if (chart is null || version == chart.Version) return;
        var before = SongMetadataFields.From(chart);
        history.Do(new SetSongMetadataAction(chart, before, before with { Version = version }));
    }

    /// <summary>The preview start typed in the dialog becomes the song's, as one undo step (blank = automatic, unreadable = kept).</summary>
    public static bool CommitPreviewStart(IHistoryService history, ChartData? chart, string text, double? shownVolume = null, string previewAudio = "")
    {
        if (chart is null || !PreviewStartTime.TryParse(text, out var previewStartMs) || previewStartMs == chart.PreviewStartMs) return false;
        var before = SongMetadataFields.From(chart);
        history.Do(new SetSongMetadataAction(chart, before, before with { PreviewStartMs = previewStartMs }));
        // True when the start changed and the preview volume is the measured one, so it is measured again on the new clip.
        return shownVolume == ProjectVolumes.PreviewOrDefault(chart) && string.IsNullOrWhiteSpace(previewAudio);
    }

    public static (bool Audio, bool Preview) VolumesToKeep(RocksmithExportViewModel screen, ChartData chart,
        (double Audio, double Preview) shown) =>
        (screen.AudioAutoTargetLufs is not null || shown.Audio != ProjectVolumes.AudioOrDefault(chart),
            screen.PreviewAutoTargetLufs is not null || shown.Preview != ProjectVolumes.PreviewOrDefault(chart));

    public static Task JoinProjectVolumesAsync(RocksmithExportViewModel screen, ChartProject project,
        ChartEditDialogServices edit, bool keepAudio, bool keepPreview, RocksmithVolumeMeasure? measure,
        Task previous, RocksmithExportVolumeInput? previousInput, Action<RocksmithExportVolumeInput> setInput,
        Func<string, string>? signature = null, RocksmithExportContinuation? continuation = null)
    {
        var current = continuation ?? new RocksmithExportContinuation(screen);
        if (!current.IsCurrent) return Task.CompletedTask;
        var targetLufs = edit.Config.LoadAppConfig().ExportAutoVolumeTargetLufs;
        var input = new RocksmithExportVolumeInput(screen, project.Chart, edit.Logger, signature, targetLufs);
        var pending = RunAsync();
        pending.Observe(edit.Logger, "export volumes");
        return pending;

        async Task RunAsync()
        {
            var restart = false;
            if (!previous.IsCompleted)
            {
                var matches = await input.MatchesAsync(previousInput, current);
                if (!current.IsCurrent) return;
                if (matches)
                {
                    await previous.WaitAsync(current.Token);
                    if (!current.IsCurrent) return;
                    return;
                }
                restart = true;
            }
            if (!current.IsCurrent) return;
            previousInput?.Retire();
            current.RefreshShownWhileBuilding();
            if (restart) input = new RocksmithExportVolumeInput(screen, project.Chart, edit.Logger, signature, targetLufs);
            if (!current.IsCurrent) return;
            setInput(input);
            await EnsureProjectVolumesAsync(screen, project, edit, keepAudio, keepPreview, measure, input, current);
            if (!current.IsCurrent) return;
        }
    }

    public static async Task EnsureProjectVolumesAsync(RocksmithExportViewModel screen, ChartProject project,
        ChartEditDialogServices edit, bool keepAudio, bool keepPreview, RocksmithVolumeMeasure? measure = null,
        RocksmithExportVolumeInput? input = null, RocksmithExportContinuation? continuation = null)
    {
        var current = continuation ?? new RocksmithExportContinuation(screen);
        if (!current.IsCurrent) return;
        input ??= new RocksmithExportVolumeInput(screen, project.Chart, edit.Logger, targetLufs: edit.Config.LoadAppConfig().ExportAutoVolumeTargetLufs);
        var shown = current.Shown;
        var audioPath = input.AudioPath;
        var previewPath = input.PreviewPath;
        await input.Ready.WaitAsync(current.Token);
        if (!current.IsCurrent || input.IsRetired) return;
        var measured = await MeasureProjectVolumesAsync(input.Snapshot, audioPath, previewPath, edit.Logger, measure, input.TargetLufs).WaitAsync(current.Token);
        if (!current.IsCurrent || input.IsRetired || !ReferenceEquals(edit.State.ActiveProject, project) ||
            screen.AudioPath != audioPath || screen.PreviewAudioPath != previewPath ||
            edit.Config.LoadAppConfig().ExportAutoVolumeTargetLufs != input.TargetLufs)
        {
            return;
        }
        CommitMeasuredVolumes(edit.History, project.Chart, measured);
        if (current.IsCurrent && !keepAudio && screen.AudioVolume == shown.Audio) screen.AudioVolume = ProjectVolumes.AudioOrDefault(project.Chart);
        if (current.IsCurrent && !keepPreview && screen.PreviewVolume == shown.Preview) screen.PreviewVolume = ProjectVolumes.PreviewOrDefault(project.Chart);
    }

    /// <summary>Measures the volumes the project does not have (or has from other audio), off the UI thread; a typed one stays.</summary>
    public static async Task<(ProjectVolumeFields Before, ProjectVolumeFields After, double? PreviewStartMs)> MeasureProjectVolumesAsync(
        ChartData chart, string audioPath, string previewAudioPath, IAppLogger logger, RocksmithVolumeMeasure? measure = null,
        int targetLufs = RocksmithAutoVolume.DefaultTargetLufs)
    {
        var before = ProjectVolumeFields.From(chart);
        var snapshot = new ChartData { PreviewStartMs = chart.PreviewStartMs };
        before.ApplyTo(snapshot);
        var audio = audioPath.Trim();
        var preview = string.IsNullOrWhiteSpace(previewAudioPath) ? null : previewAudioPath.Trim();
        if (audio.Length > 0)
        {
            await Task.Run(() => (measure ?? ProjectVolumes.Measure)(snapshot, audio, preview, null,
                warning => logger.Warning(AppLogCategory.Project, warning), targetLufs));
        }
        return (before, ProjectVolumeFields.From(snapshot), snapshot.PreviewStartMs);
    }

    public static void CommitMeasuredVolumes(IHistoryService history, ChartData chart,
        (ProjectVolumeFields Before, ProjectVolumeFields After, double? PreviewStartMs) measured)
    {
        if (ProjectVolumeFields.From(chart) != measured.Before || chart.PreviewStartMs != measured.PreviewStartMs || measured.Before == measured.After) return;
        // Refreshing existing derived metadata is not a user edit. The next project write persists it.
        var refresh = (!measured.Before.AudioManual && measured.Before.Audio is not null) ||
                      (!measured.Before.PreviewManual && measured.Before.Preview is not null);
        if (refresh) measured.After.ApplyTo(chart);
        else history.Do(new SetProjectVolumesAction(chart, measured.Before, measured.After));
    }

    /// <summary>
    ///     A typed volume becomes the song's override. An explicit Auto result can refresh from the
    ///     project sources next time, with the configured target in effect then.
    /// </summary>
    public static void CommitVolumes(IHistoryService history, ChartData? chart, double audioVolume, double previewVolume,
        bool autoAudio = false, bool autoPreview = false)
    {
        if (chart is null) return;
        var before = ProjectVolumeFields.From(chart);
        var after = before;
        if (autoAudio)
        {
            if (before.AudioManual || before.Audio != audioVolume)
                after = after with { Audio = audioVolume, AudioManual = false, AudioSignature = "" };
        }
        else if (Math.Round(audioVolume, 1) != Math.Round(ProjectVolumes.AudioOrDefault(chart), 1))
        {
            after = after with { Audio = audioVolume, AudioManual = true };
        }

        if (autoPreview)
        {
            if (before.PreviewManual || before.Preview != previewVolume)
                after = after with { Preview = previewVolume, PreviewManual = false, PreviewSignature = "" };
        }
        else if (Math.Round(previewVolume, 1) != Math.Round(ProjectVolumes.PreviewOrDefault(chart), 1))
        {
            after = after with { Preview = previewVolume, PreviewManual = true };
        }
        history.Do(new SetProjectVolumesAction(chart, before, after));
    }

    /// <summary>Restores the draft's controls and identifies the volumes the automatic prefill should leave alone.</summary>
    public static (bool KeepAudio, bool KeepPreview) RestoreDraft(
        RocksmithExportViewModel screen, ChartData chart, AppConfig config, ExportDraft? draft)
    {
        screen.DlcKey = draft?.DlcKey ?? "";
        screen.Version = string.IsNullOrWhiteSpace(chart.Version) ? "1" : chart.Version;
        screen.PreviewAudioPath = draft?.PreviewAudioPath ?? "";
        screen.PreviewStartText = draft?.PreviewStartText ?? PreviewStartTime.Format(chart.PreviewStartMs);
        screen.AlbumArtPath = draft?.AlbumArtPath ?? "";
        screen.AppId = draft?.AppId ?? (string.IsNullOrWhiteSpace(config.LastExportAppId) ? "248750" : config.LastExportAppId);
        screen.IncludeVocals = draft?.IncludeVocals ?? config.ExportIncludeVocals;

        var targetLufs = config.ExportAutoVolumeTargetLufs;
        var audio = draft?.AudioAutoTargetLufs is null || draft.AudioAutoTargetLufs == targetLufs ? draft?.AudioVolume : null;
        var preview = draft?.PreviewAutoTargetLufs is null || draft.PreviewAutoTargetLufs == targetLufs ? draft?.PreviewVolume : null;
        screen.AudioVolume = audio ?? ProjectVolumes.AudioOrDefault(chart);
        screen.PreviewVolume = preview ?? ProjectVolumes.PreviewOrDefault(chart);
        screen.AudioAutoTargetLufs = audio is null ? null : draft?.AudioAutoTargetLufs;
        screen.PreviewAutoTargetLufs = preview is null ? null : draft?.PreviewAutoTargetLufs;
        return (audio is not null, preview is not null);
    }

    /// <summary>Retains the dialog's fields and arrangement choices for the next open of this project.</summary>
    public static void SaveDraft(RocksmithExportViewModel screen, ChartProject? project, DialogMemory memory, string defaultTestFolder)
    {
        if (project is null) return;
        var testFolder = screen.TestFolder?.Trim() ?? "";
        memory.ExportDraftProject = project;
        memory.ExportDraft = new ExportDraft
        {
            DlcKey = screen.DlcKey, AppId = screen.AppId, AudioPath = screen.AudioPath, PreviewAudioPath = screen.PreviewAudioPath,
            PreviewStartText = screen.PreviewStartText, AlbumArtPath = screen.AlbumArtPath, OutputPath = screen.OutputPath, DerivedPackageName = screen.DerivedPackageName,
            TestFolderOverride = testFolder == defaultTestFolder ? null : testFolder,
            AudioVolume = screen.AudioAutoTargetLufs is not null || screen.AudioVolume != ProjectVolumes.AudioOrDefault(project.Chart) ? screen.AudioVolume : null,
            PreviewVolume = screen.PreviewAutoTargetLufs is not null || screen.PreviewVolume != ProjectVolumes.PreviewOrDefault(project.Chart) ? screen.PreviewVolume : null,
            AudioAutoTargetLufs = screen.AudioAutoTargetLufs, PreviewAutoTargetLufs = screen.PreviewAutoTargetLufs,
            IncludeVocals = screen.IncludeVocals,
            Ticks = screen.Arrangements.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.Last().IsSelected, StringComparer.Ordinal)
        };
    }

    /// <summary>Remembering the folder must never fail an export that already landed.</summary>
    public static void Remember(ChartEditDialogServices edit, string psarcPath, bool test, string appId, bool includeVocals)
    {
        try
        {
            var config = edit.Config.LoadAppConfig();
            // A test build goes to the test folder, which is not where the next release should default to.
            if (!test && Path.GetDirectoryName(psarcPath) is { Length: > 0 } dir) config.LastExportDir = dir;
            config.LastExportAppId = string.IsNullOrWhiteSpace(appId) ? "248750" : appId.Trim();
            config.ExportIncludeVocals = includeVocals;
            edit.Config.SaveAppConfig(config);
        }
        catch (Exception ex)
        {
            edit.Logger.Warning(AppLogCategory.Project, "Could not remember the export preferences", ex);
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
