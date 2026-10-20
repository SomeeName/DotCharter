using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Update;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ Updates: the recovery strip for a staged-but-unapplied
///     payload, the manual check, the startup toggle and — only when the
///     update feed needs it — the GitHub sign-in block. The startup toggle is
///     the page's one setting; everything else on the tab is status.
/// </summary>
public sealed class UpdatesSettingsViewModel : ObservableObject,
    IUpdatesSettingsScreen, ISettingsTab, ISettingsChangeSource
{
    private readonly UpdateCheckFlow _flow;
    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly GitHubSignInViewModel _gitHub;
    private readonly SettingsChangeSet _changes = new();
    private bool _hasInterruptedUpdate;
    private string _interruptedUpdateText = "";
    private string _interruptedUpdateDetail = "";
    private bool _canFinishInterruptedUpdate;
    private bool _isUpdatesGroupVisible = true;
    private bool _isCurrentVersionRowVisible = true;
    private bool _isCheckNowRowVisible = true;
    private bool _hasPendingUpdate;
    private bool _isChecking;
    private bool _isCheckOnStartupRowVisible = true;
    private bool _updateCheckOnStartup = true;
    private string _checkStatusText = "";
    private bool _isGitHubGroupVisible;

    public UpdatesSettingsViewModel(
        UpdateCheckFlow flow,
        IConfigurationService config,
        ILocalizationService localization,
        GitHubSignInViewModel gitHub)
    {
        _flow = flow;
        _config = config;
        _localization = localization;
        _gitHub = gitHub;

        _isGitHubGroupVisible = gitHub.IsAvailable;
        _changes.Track("updates.UpdateCheckOnStartup", () => UpdateCheckOnStartup);

        FinishInterruptedUpdateCommand = new RelayCommand(FinishInterruptedUpdate);
        DiscardInterruptedUpdateCommand = new RelayCommand(DiscardInterruptedUpdate);
        ShowPendingUpdateCommand = new RelayCommand(() => _flow.ShowPendingUpdate());
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
        CurrentVersionText = _flow.CurrentVersionText;
        _changes.Rebaseline();
    }

    public bool HasInterruptedUpdate { get => _hasInterruptedUpdate; private set => SetProperty(ref _hasInterruptedUpdate, value); }

    public string InterruptedUpdateText { get => _interruptedUpdateText; private set => SetProperty(ref _interruptedUpdateText, value); }

    public bool HasInterruptedUpdateDetail => _interruptedUpdateDetail.Length > 0;

    public string InterruptedUpdateDetail
    {
        get => _interruptedUpdateDetail;
        private set
        {
            if (SetProperty(ref _interruptedUpdateDetail, value)) OnPropertyChanged(nameof(HasInterruptedUpdateDetail));
        }
    }

    public bool CanFinishInterruptedUpdate { get => _canFinishInterruptedUpdate; private set => SetProperty(ref _canFinishInterruptedUpdate, value); }

    public ICommand FinishInterruptedUpdateCommand { get; }

    public ICommand DiscardInterruptedUpdateCommand { get; }

    public bool IsUpdatesGroupVisible { get => _isUpdatesGroupVisible; private set => SetProperty(ref _isUpdatesGroupVisible, value); }

    public bool IsCurrentVersionRowVisible { get => _isCurrentVersionRowVisible; private set => SetProperty(ref _isCurrentVersionRowVisible, value); }

    public string CurrentVersionText { get; }

    public bool IsCheckNowRowVisible { get => _isCheckNowRowVisible; private set => SetProperty(ref _isCheckNowRowVisible, value); }

    public bool HasPendingUpdate { get => _hasPendingUpdate; private set => SetProperty(ref _hasPendingUpdate, value); }

    public ICommand ShowPendingUpdateCommand { get; }

    public ICommand CheckForUpdatesCommand { get; }

    public bool IsChecking { get => _isChecking; private set => SetProperty(ref _isChecking, value); }

    public bool IsCheckOnStartupRowVisible { get => _isCheckOnStartupRowVisible; private set => SetProperty(ref _isCheckOnStartupRowVisible, value); }

    public bool UpdateCheckOnStartup
    {
        get => _updateCheckOnStartup;
        set
        {
            if (SetProperty(ref _updateCheckOnStartup, value)) OnPropertyChanged(nameof(UnsavedSettings));
        }
    }

    public bool HasCheckStatus => _checkStatusText.Length > 0;

    public string CheckStatusText
    {
        get => _checkStatusText;
        private set
        {
            if (SetProperty(ref _checkStatusText, value)) OnPropertyChanged(nameof(HasCheckStatus));
        }
    }

    public bool IsGitHubGroupVisible { get => _isGitHubGroupVisible; private set => SetProperty(ref _isGitHubGroupVisible, value); }

    public IGitHubSignInPart GitHub => _gitHub;

    public string Key => SettingsTabKeys.Updates;

    public Type? Screen => typeof(IUpdatesSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    public IReadOnlyList<SettingsSearchGroup> SearchGroups
    {
        get
        {
            var updates = new SettingsSearchGroup(_localization["Update_GroupTitle"], _localization["Update_GroupSubtitle"],
            [
                new("updates.currentVersion", _localization["Update_CurrentVersion"]),
                new("updates.checkNow", _localization["Update_CheckNow"], _localization["Settings_Tip_UpdateCheckNow"]),
                new("updates.checkOnStartup", _localization["Update_CheckOnStartup"], _localization["Settings_Tip_UpdateCheckOnStartup"])
            ]);
            // The public feed needs no sign-in, so the search must not find a group that is never shown.
            if (!_gitHub.IsAvailable) return [updates];
            return
            [
                updates,
                new(_localization["GitHub_GroupTitle"], _localization["GitHub_GroupSubtitle"],
                [
                    new("gitHub.account", _localization["GitHub_Account"]),
                    new("gitHub.deviceFlow", _localization["GitHub_BrowserSignInLabel"]),
                    new("gitHub.pat", _localization["GitHub_PatLabel"], _localization["GitHub_PatHint"])
                ])
            ];
        }
    }

    public void Load()
    {
        var config = _config.LoadAppConfig();
        UpdateCheckOnStartup = config.UpdateCheckOnStartup;
        CheckStatusText = "";
        HasPendingUpdate = _flow.HasPendingUpdate;
        RefreshPendingApply();
        _ = _gitHub.RefreshAuthStateAsync();
        Rebaseline();
    }

    public void Save()
    {
        var startup = UpdateCheckOnStartup;
        _config.Update(config => config.UpdateCheckOnStartup = startup);
        Rebaseline();
    }

    public void Revert()
    {
        var config = _config.LoadAppConfig();
        UpdateCheckOnStartup = config.UpdateCheckOnStartup;
        _gitHub.CancelDeviceFlow();
        Rebaseline();
    }

    public void Reset() => Load();

    public void Rebaseline()
    {
        _changes.Rebaseline();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    public void ApplySearch(string query)
    {
        var groups = SearchGroups;
        var updates = groups[0];
        var has = query.Length > 0;

        var updatesMatch = has && SettingsSearch.GroupMatches(updates, query);
        var rows = updates.Rows.Select(r => !has || updatesMatch || SettingsSearch.RowMatches(r, query)).ToList();
        IsCurrentVersionRowVisible = rows[0];
        IsCheckNowRowVisible = rows[1];
        IsCheckOnStartupRowVisible = rows[2];
        IsUpdatesGroupVisible = !has || rows.Any(v => v);

        if (groups.Count < 2)
        {
            IsGitHubGroupVisible = false;
            return;
        }

        var gitHub = groups[1];
        var gitHubMatch = has && SettingsSearch.GroupMatches(gitHub, query);
        var git = gitHub.Rows.Select(r => !has || gitHubMatch || SettingsSearch.RowMatches(r, query)).ToList();
        _gitHub.SetRowVisibility(git[0], git[1], git[2]);
        IsGitHubGroupVisible = !has || git.Any(v => v);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        CheckStatusText = _localization["Update_Checking"];
        try
        {
            var outcome = await _flow.CheckNowAsync();
            CheckStatusText = outcome.StatusText;
            HasPendingUpdate = outcome.HasPendingUpdate;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void RefreshPendingApply()
    {
        var pending = _flow.ProbePendingApply();
        if (pending is null)
        {
            HasInterruptedUpdate = false;
            InterruptedUpdateText = "";
            InterruptedUpdateDetail = "";
            CanFinishInterruptedUpdate = false;
            return;
        }

        HasInterruptedUpdate = true;
        CanFinishInterruptedUpdate = pending.CanResume;
        var version = string.IsNullOrWhiteSpace(pending.State.TargetVersion) ? "?" : pending.State.TargetVersion;
        InterruptedUpdateText = _localization.Format("Update_InterruptedDetail", version,
            pending.State.StagedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        InterruptedUpdateDetail = pending.CanResume
            ? pending.LogTail
            : _localization.Format("Update_InterruptedUnusable", pending.Problem ?? "?");
    }

    private void FinishInterruptedUpdate()
    {
        if (!CanFinishInterruptedUpdate) return;
        var failure = _flow.FinishPendingApply();
        if (failure is null) return;
        InterruptedUpdateDetail = failure;
        RefreshPendingApply();
    }

    private void DiscardInterruptedUpdate()
    {
        _flow.DiscardPendingApply();
        RefreshPendingApply();
    }
}
