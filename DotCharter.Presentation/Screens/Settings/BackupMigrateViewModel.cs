using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Infrastructure.Backup;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The Files &amp; backups tab's export / import block. An import replaces
///     files behind caching services, so it drops their caches (again in a
///     <c>finally</c>: a part-way import still replaced files), re-applies the
///     language and the log filter, and asks the page to reload every tab.
/// </summary>
public sealed class BackupMigrateViewModel : ObservableObject, IBackupMigratePart
{
    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly IFileStorageService _files;
    private readonly IConfigBackupService _backup;
    private readonly ImportedConfigStores _stores;
    private readonly SettingsSession _session;
    private readonly IAppLogger? _logger;
    private string _statusText = "";
    private bool _isBusy;

    public BackupMigrateViewModel(
        IConfigurationService config,
        ILocalizationService localization,
        IFileStorageService files,
        IConfigBackupService backup,
        ImportedConfigStores stores,
        SettingsSession session,
        IAppLogger? logger = null)
    {
        _config = config;
        _localization = localization;
        _files = files;
        _backup = backup;
        _stores = stores;
        _session = session;
        _logger = logger;
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
    }

    public ICommand ExportCommand { get; }

    public ICommand ImportCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ((AsyncRelayCommand)ExportCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)ImportCommand).NotifyCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _statusText.Length > 0;

    private IReadOnlyList<FilePickerFilter> ZipFilter =>
        [new FilePickerFilter(_localization["Settings_BackupMigrate_ZipFilter"], ["*.zip"])];

    public async Task ExportAsync()
    {
        var path = await _files.SaveFilePickerAsync(
            _localization["Settings_BackupMigrate_ExportTitle"],
            $"DotCharter-backup-{DateTime.Now:yyyy-MM-dd}.zip", ZipFilter).ConfigureAwait(true);
        if (path == null) return;

        IsBusy = true;
        try
        {
            // The archive copies the config FILES, and config saves are coalesced.
            _config.Flush();
            var result = await Task.Run(() => _backup.Export(path)).ConfigureAwait(true);
            StatusText = _localization.Plural(
                "Settings_BackupMigrate_ExportedOne", "Settings_BackupMigrate_Exported", result.FileCount, path);
            _logger?.Info(AppLogCategory.Settings, $"Backup exported: {result.FileCount} file(s) -> '{path}'.");
        }
        catch (Exception ex)
        {
            StatusText = _localization.Format("Settings_BackupMigrate_Failed", ex.Message);
            _logger?.Error(AppLogCategory.Settings, $"Backup export to '{path}' failed.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportAsync()
    {
        var path = await _files.OpenFilePickerAsync(
            _localization["Settings_BackupMigrate_ImportTitle"], ZipFilter).ConfigureAwait(true);
        if (path == null) return;

        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _backup.Import(path)).ConfigureAwait(true);
            _stores.Reload();
            var config = _config.LoadAppConfig();
            _localization.SetConfiguredCulture(config.LanguageCulture);
            _logger?.ReloadSettings();
            _session.RequestReload();
            StatusText = _localization.Plural(
                "Settings_BackupMigrate_ImportedOne", "Settings_BackupMigrate_Imported", result.FileCount);
            _logger?.Info(AppLogCategory.Settings,
                $"Backup imported: {result.FileCount} file(s) from '{path}', {result.SkippedEntries.Count} skipped.");
        }
        catch (Exception ex)
        {
            StatusText = _localization.Format("Settings_BackupMigrate_Failed", ex.Message);
            _logger?.Error(AppLogCategory.Settings, $"Backup import from '{path}' failed.", ex);
        }
        finally
        {
            _stores.DropCaches();
            IsBusy = false;
        }
    }
}
