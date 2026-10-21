using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Plugins;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.Library.Cache;
using DotCharter.Presentation.Shared;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ Files &amp; backups: save companions, the timed project
///     backup, backup export / import (<see cref="BackupMigrateViewModel" />)
///     and the decoded Rocksmith audio cache. The cache is measured each time
///     the tab loads; the walk runs off the dispatcher and only its three
///     status writes come back to it.
/// </summary>
public sealed partial class FilesSettingsViewModel : ObservableObject,
    IFilesSettingsScreen, ISettingsTab, ISettingsChangeSource
{
    private static readonly SettingsTabRows Rows = new("files.",
    [
        new("Settings_SaveCompanions", "Settings_SaveCompanions_Subtitle",
            new("eof", "Settings_SaveCompanionEof_RowLabel", "Settings_Tip_SaveCompanionEof"),
            new("eofAudio", "Settings_SaveCompanionEofAudio", "Settings_Tip_SaveCompanionEofAudio"),
            new("psarc", "Settings_SaveCompanionPsarc_RowLabel", "Settings_Tip_SaveCompanionPsarc"),
            new("psarcCodec", "Settings_SaveCompanionPsarcCodec", "Settings_Tip_SaveCompanionPsarcCodec"),
            new("rs2dlc", "Settings_SaveCompanionRs2Dlc_RowLabel", "Settings_Tip_SaveCompanionRs2Dlc"),
            new("rsXmlSixString", "Settings_RsXmlSixString_RowLabel", "Settings_Tip_RsXmlSixString"),
            new("rsXmlMoverPhrases", "Settings_RsXmlMoverPhrases_RowLabel", "Settings_Tip_RsXmlMoverPhrases"),
            new("dlcBuilderFixes", "Settings_ExportDlcBuilderFixes_RowLabel", "Settings_Tip_ExportDlcBuilderFixes"),
            new("generateDd", "Settings_ExportGenerateDd_RowLabel", "Settings_Tip_ExportGenerateDd"),
            new("ddMlModel", "Settings_ExportDdMlModel_RowLabel", "Settings_Tip_ExportDdMlModel")),
        new("Settings_AutoBackups", "Settings_BackupDescription",
            new("autoBackup", "Settings_Backups", "Settings_Tip_Backups"),
            new("backupInterval", "Settings_Interval", "Settings_Tip_Interval"),
            new("backupKeep", "Settings_BackupKeep", "Settings_Tip_BackupKeep"),
            new("backupMigrate", "Settings_BackupMigrate_Title", "Settings_BackupMigrate_Subtitle")),
        new("Settings_PsarcAudioCache_Title", "Settings_PsarcAudioCache_Subtitle",
            new SettingsRowSpec("psarcCache", "Settings_PsarcAudioCache_UsageLabel", "Settings_PsarcAudioCache_ClearTooltip")),
        new("Settings_PsarcOpen_Title", "Settings_PsarcOpen_Subtitle",
            new SettingsRowSpec("psarcHardestOnly", "Settings_PsarcOpenHardestOnly", "Settings_Tip_PsarcOpenHardestOnly"))
    ]);

    /// <summary>The groups that only concern reading game packages: ABSENT, not greyed,
    /// when <see cref="IPackageFeatures.ReadsGamePackages" /> is off — the decoded-audio cache
    /// (its text is about Rocksmith archives) and how a library package opens in the editor.
    /// Indices into <see cref="Rows" />; the companion <c>.psarc</c> rows are the WRITER and stay.</summary>
    public static readonly IReadOnlyList<int> PackageReaderGroups = [2, 3];

    private static readonly string[] Tracked =
    [
        nameof(SaveCompanionEof), nameof(SaveCompanionEofAudio), nameof(SaveCompanionPsarc),
        nameof(SaveCompanionPsarcCodecIndex), nameof(SaveCompanionRs2Dlc), nameof(RsXmlSixStringProjection), nameof(RsXmlMoverPhrases),
        nameof(ExportGenerateDynamicDifficulty), nameof(ExportDdUseMlModel), nameof(ExportApplyDlcBuilderFixes),
        nameof(AutoBackupEnabled), nameof(AutoBackupIntervalMinutes),
        nameof(AutoBackupKeepCount), nameof(PsarcOpenHardestLevelOnly)
    ];

    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly BackupMigrateViewModel _backup;
    private readonly LibraryAudioCacheStore? _cache;
    private readonly IAppLogger? _logger;
    private readonly bool _readsGamePackages;
    private readonly SettingsChangeSet _changes = new();
    private string _psarcAudioCacheUsage = "";
    private bool _psarcAudioCacheHasEntries;
    private string _psarcAudioCacheDetail = "";

    [ObservableProperty] private bool _saveCompanionEof = true;
    [ObservableProperty] private bool _saveCompanionEofAudio = true;
    [ObservableProperty] private bool _saveCompanionPsarc = true;
    [ObservableProperty] private int _saveCompanionPsarcCodecIndex;
    [ObservableProperty] private bool _saveCompanionRs2Dlc = true;
    [ObservableProperty] private bool _rsXmlSixStringProjection;
    [ObservableProperty] private bool _rsXmlMoverPhrases;
    [ObservableProperty] private bool _exportGenerateDynamicDifficulty = true;
    [ObservableProperty] private bool _exportDdUseMlModel;
    [ObservableProperty] private bool _exportApplyDlcBuilderFixes = true;
    [ObservableProperty] private bool _autoBackupEnabled = true;
    [ObservableProperty] private int _autoBackupIntervalMinutes = 5;
    [ObservableProperty] private int _autoBackupKeepCount = 50;
    [ObservableProperty] private bool _psarcOpenHardestLevelOnly;

    public FilesSettingsViewModel(
        IConfigurationService config,
        ILocalizationService localization,
        IUiDispatcher ui,
        BackupMigrateViewModel backup,
        IPackageFeatures features,
        LibraryAudioCacheStore? cache = null,
        IAppLogger? logger = null)
    {
        _config = config;
        _localization = localization;
        _ui = ui;
        _backup = backup;
        _cache = cache;
        _logger = logger;
        _readsGamePackages = features.ReadsGamePackages;
        HiddenKeys = HiddenFor("");
        ClearLibraryAudioCacheCommand = new AsyncRelayCommand(ClearLibraryAudioCacheAsync);
        RefreshLibraryAudioCacheCommand = new AsyncRelayCommand(RefreshLibraryAudioCacheAsync);
        _changes.TrackProperties(this, Rows.Prefix, Tracked, () => OnPropertyChanged(nameof(UnsavedSettings)));
        _changes.Rebaseline();
    }

    public IBackupMigratePart Backup => _backup;

    public string LibraryAudioCacheUsage
    {
        get => _psarcAudioCacheUsage;
        private set => SetProperty(ref _psarcAudioCacheUsage, value);
    }

    public bool LibraryAudioCacheHasEntries
    {
        get => _psarcAudioCacheHasEntries;
        private set => SetProperty(ref _psarcAudioCacheHasEntries, value);
    }

    public string LibraryAudioCacheDetail
    {
        get => _psarcAudioCacheDetail;
        private set
        {
            if (SetProperty(ref _psarcAudioCacheDetail, value)) OnPropertyChanged(nameof(HasLibraryAudioCacheDetail));
        }
    }

    public bool HasLibraryAudioCacheDetail => _psarcAudioCacheDetail.Length > 0;

    public ICommand ClearLibraryAudioCacheCommand { get; }

    public ICommand RefreshLibraryAudioCacheCommand { get; }

    public IReadOnlySet<string> HiddenKeys { get; private set; } = new HashSet<string>();

    public string Key => SettingsTabKeys.Files;

    public Type? Screen => typeof(IFilesSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    /// <summary>The search catalogue; a package-reader group is an EMPTY group when packages
    /// cannot be read, so search never finds it and the view's group key is always hidden.</summary>
    public IReadOnlyList<SettingsSearchGroup> SearchGroups =>
    [
        .. Rows.Localize(_localization).Select((g, i) =>
            _readsGamePackages || !PackageReaderGroups.Contains(i) ? g : new SettingsSearchGroup("", null, []))
    ];

    /// <summary>The key a view binds a whole group's visibility to.</summary>
    public static string GroupKey(int index) => Rows.GroupKey(index);

    public void Load()
    {
        var config = _config.LoadAppConfig();
        SaveCompanionEof = config.SaveCompanionEof;
        SaveCompanionEofAudio = config.SaveCompanionEofAudio;
        SaveCompanionPsarc = config.SaveCompanionPsarc;
        // 0 = Vorbis, 1 = PCM. A stored "ImaAdpcm" shows as Vorbis,
        // which is what the build now writes for it.
        SaveCompanionPsarcCodecIndex =
            config.SaveCompanionPsarcCodec.Equals("Pcm", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        SaveCompanionRs2Dlc = config.SaveCompanionRs2Dlc;
        RsXmlSixStringProjection = config.RsXmlSixStringProjection;
        RsXmlMoverPhrases = config.RsXmlMoverPhrases;
        ExportGenerateDynamicDifficulty = config.ExportGenerateDynamicDifficulty;
        ExportDdUseMlModel = DlcBuilderSteps.ParseMode(config.ExportDdLevelCountMode) == DdLevelCountMode.MLModel;
        ExportApplyDlcBuilderFixes = config.ExportApplyDlcBuilderFixes;
        AutoBackupEnabled = config.AutoBackupEnabled;
        AutoBackupIntervalMinutes = config.AutoBackupIntervalMinutes > 0 ? config.AutoBackupIntervalMinutes : 5;
        AutoBackupKeepCount = Math.Max(0, config.AutoBackupKeepCount);
        PsarcOpenHardestLevelOnly = config.PsarcOpenHardestLevelOnly;
        Rebaseline();
        _ = RefreshLibraryAudioCacheAsync();
    }

    public void Save()
    {
        var config = _config.LoadAppConfig();
        config.SaveCompanionEof = SaveCompanionEof;
        config.SaveCompanionEofAudio = SaveCompanionEofAudio;
        config.SaveCompanionPsarc = SaveCompanionPsarc;
        config.SaveCompanionPsarcCodec = SaveCompanionPsarcCodecIndex == 1 ? "Pcm" : "Vorbis";
        config.SaveCompanionRs2Dlc = SaveCompanionRs2Dlc;
        config.RsXmlSixStringProjection = RsXmlSixStringProjection;
        config.RsXmlMoverPhrases = RsXmlMoverPhrases;
        config.ExportGenerateDynamicDifficulty = ExportGenerateDynamicDifficulty;
        config.ExportDdLevelCountMode = (ExportDdUseMlModel ? DdLevelCountMode.MLModel : DdLevelCountMode.Simple).ToString();
        config.ExportApplyDlcBuilderFixes = ExportApplyDlcBuilderFixes;
        config.AutoBackupEnabled = AutoBackupEnabled;
        config.AutoBackupIntervalMinutes = Math.Max(1, AutoBackupIntervalMinutes);
        config.AutoBackupKeepCount = Math.Max(0, AutoBackupKeepCount);
        config.PsarcOpenHardestLevelOnly = PsarcOpenHardestLevelOnly;
        _config.SaveAppConfig(config);
        _logger?.Info(AppLogCategory.Settings,
            $"Files settings saved. AutoBackup enabled={config.AutoBackupEnabled}, " +
            $"intervalMinutes={config.AutoBackupIntervalMinutes}, keep={config.AutoBackupKeepCount}, psarc={config.SaveCompanionPsarc}.");
        Rebaseline();
    }

    public void Revert() => Load();

    public void Reset() => Load();

    public void Rebaseline()
    {
        _changes.Rebaseline();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    public void ApplySearch(string query)
    {
        HiddenKeys = HiddenFor(query);
        OnPropertyChanged(nameof(HiddenKeys));
    }

    private HashSet<string> HiddenFor(string query)
    {
        var hidden = SettingsSearch.HiddenKeys(SearchGroups, query, GroupKey, out _);
        if (_readsGamePackages) return hidden;
        foreach (var group in PackageReaderGroups)
            hidden.UnionWith(Rows.Specs[group].Rows.Select(r => Rows.RowKey(r.Key)));
        return hidden;
    }

    /// <summary>Re-measures the decoded-audio cache; a store the build does not have reads as blank.</summary>
    public async Task RefreshLibraryAudioCacheAsync()
    {
        if (_cache is not { } cache || !_readsGamePackages) return;
        try
        {
            var usage = await Task.Run(cache.MeasureUsage).ConfigureAwait(true);
            var text = usage.Songs == 0
                ? _localization["Settings_PsarcAudioCache_Empty"]
                : _localization.Plural("Settings_PsarcAudioCache_UsageOne", "Settings_PsarcAudioCache_UsageFmt",
                    usage.Songs, ByteSizeText.Format(usage.Bytes), ByteSizeText.Format(cache.MaxBytes));
            await _ui.InvokeAsync(() =>
            {
                LibraryAudioCacheUsage = text;
                LibraryAudioCacheHasEntries = usage.Songs > 0;
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Settings, $"PSARC audio cache measure failed: {ex.Message}");
        }
    }

    private async Task ClearLibraryAudioCacheAsync()
    {
        if (_cache is not { } cache) return;
        string detail;
        try
        {
            var deleted = await Task.Run(cache.Clear).ConfigureAwait(true);
            detail = _localization.Plural(
                "Settings_PsarcAudioCache_ClearedOne", "Settings_PsarcAudioCache_ClearedFmt", deleted);
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Settings, $"PSARC audio cache clear failed: {ex.Message}", ex);
            detail = ex.Message;
        }

        await _ui.InvokeAsync(() => LibraryAudioCacheDetail = detail).ConfigureAwait(true);
        await RefreshLibraryAudioCacheAsync().ConfigureAwait(true);
    }
}
