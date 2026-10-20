using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ TONE3000: the client id, persisted like any other setting,
///     and the OAuth sign-in that uses it. One setting is tracked; the rest of
///     the screen is session status read from <see cref="ITone3000AuthService" />.
/// </summary>
public sealed partial class Tone3000SettingsViewModel : ObservableObject,
    ITone3000SettingsScreen, ISettingsTab, ISettingsChangeSource, IDisposable
{
    private readonly ITone3000AuthService _auth;
    private readonly IConfigurationService _config;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger? _logger;
    private readonly SettingsChangeSet _changes = new();
    private readonly Action _onAuthStateChanged;
    private CancellationTokenSource? _signInCts;

    public Tone3000SettingsViewModel(
        ITone3000AuthService auth,
        IConfigurationService config,
        IUiDispatcher dispatcher,
        ILocalizationService localization,
        IAppLogger? logger = null)
    {
        _auth = auth;
        _config = config;
        _dispatcher = dispatcher;
        _localization = localization;
        _logger = logger;

        // Sign-in completes on the loopback listener's worker thread — marshal.
        _onAuthStateChanged = () => _dispatcher.PostNormal(RaiseAuthState);
        _auth.AuthStateChanged += _onAuthStateChanged;

        _changes.Track("tone3000.ClientId", () => ClientId);
        _changes.Rebaseline();
    }

    [ObservableProperty] private string _clientId = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    [ObservableProperty] private bool _isStatusError;

    [ObservableProperty] private bool _isGroupVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRedirectUriVisible))]
    private bool _isClientIdRowVisible = true;

    [ObservableProperty] private bool _isAccountRowVisible = true;

    public string RedirectUri => Tone3000OAuth.RedirectUri;

    public bool IsRedirectUriVisible => IsClientIdRowVisible;

    public bool HasStatus => StatusText.Length > 0;

    public bool IsConfigured => _auth.IsConfigured;

    public bool IsSignedIn => _auth.IsSignedIn;

    public bool IsSignedOut => !_auth.IsSignedIn;

    public bool CanSignIn => _auth.IsConfigured && !IsBusy;

    public string SignedInText => _auth.IsSignedIn
        ? _localization.Format("Tone3000_SignedInAs", _auth.UserName)
        : "";

    ICommand ITone3000SettingsScreen.SaveClientIdCommand => SaveClientIdCommand;

    ICommand ITone3000SettingsScreen.SignInCommand => SignInCommand;

    ICommand ITone3000SettingsScreen.SignOutCommand => SignOutCommand;

    ICommand ITone3000SettingsScreen.CancelSignInCommand => CancelSignInCommand;

    public string Key => SettingsTabKeys.Tone3000;

    public Type? Screen => typeof(ITone3000SettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    public IReadOnlyList<SettingsSearchGroup> SearchGroups =>
    [
        new(_localization["Tone3000_Title"], _localization["Tone3000_Subtitle"],
        [
            new("tone3000.clientId", _localization["Tone3000_ClientIdLabel"], _localization["Tone3000_ClientIdHint"]),
            new("tone3000.account", _localization["Tone3000_Account"], _localization["Tone3000_SignIn"])
        ])
    ];

    /// <summary>Populates the field from persisted config and refreshes the auth state.</summary>
    public void Load()
    {
        ClientId = _config.LoadAppConfig().Tone3000ClientId;
        StatusText = "";
        IsStatusError = false;
        RaiseAuthState();
        Rebaseline();
    }

    /// <summary>
    ///     Persists the client id, and only when it moved: the whole-config save
    ///     is expensive and overwrites edits made outside the app. Run by the
    ///     footer's Save and before every auth action, so a freshly pasted id is
    ///     live.
    /// </summary>
    public void Save()
    {
        var config = _config.LoadAppConfig();
        var id = ClientId.Trim();
        if (config.Tone3000ClientId != id)
        {
            config.Tone3000ClientId = id;
            _config.SaveAppConfig(config);
            _logger?.Info(AppLogCategory.Settings, $"TONE3000 client id updated. Configured={_auth.IsConfigured}.");
            RaiseAuthState();
        }

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
        var group = SearchGroups[0];
        var has = query.Length > 0;
        var groupMatches = has && SettingsSearch.GroupMatches(group, query);
        var kept = group.Rows.Select(r => !has || groupMatches || SettingsSearch.RowMatches(r, query)).ToList();

        IsClientIdRowVisible = kept[0];
        IsAccountRowVisible = kept[1];
        IsGroupVisible = !has || kept.Any(v => v);
    }

    public void Dispose()
    {
        _auth.AuthStateChanged -= _onAuthStateChanged;
        CancelPendingSignIn();
    }

    partial void OnClientIdChanged(string value) => OnPropertyChanged(nameof(UnsavedSettings));

    [RelayCommand]
    private void SaveClientId()
    {
        Save();
        SetStatus(_localization["Tone3000_ClientIdSaved"], isError: false);
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        if (!IsBusy) return;
        CancelPendingSignIn();
        SetStatus(_localization["Tone3000_SignedOut"], isError: false);
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        // The flow holds a loopback listener for up to five minutes. A second
        // concurrent flow cannot bind the same prefix and throws, which the
        // catch below would report as a failed sign-in while the FIRST flow was
        // alive. `CanSignIn` says so in the markup; this guard has to hold
        // whatever the markup does.
        if (IsBusy) return;

        Save(); // make the just-entered client id live first
        if (!_auth.IsConfigured)
        {
            SetStatus(_localization["Tone3000_NeedClientId"], isError: true);
            return;
        }

        CancelPendingSignIn();
        var cts = _signInCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var result = await _auth.SignInAsync(cts.Token);
            SetStatus(
                result is { Ok: true, Value: not null }
                    ? _localization.Format("Tone3000_SignedInAs", result.Value.UserName)
                    : DescribeError(result.Error, result.Message),
                isError: result is not
                { Ok: true, Value: not null });
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_signInCts, cts)) _signInCts = null;
            cts.Dispose();
            RaiseAuthState();
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        // A pending flow would otherwise sign the user straight back in, and
        // hold the port for as long as it took.
        CancelPendingSignIn();
        _auth.SignOut();
        SetStatus(_localization["Tone3000_SignedOut"], isError: false);
        RaiseAuthState();
    }

    private void CancelPendingSignIn()
    {
        var cts = _signInCts;
        _signInCts = null;
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the flow's finally already disposed it */ }
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        IsStatusError = isError;
    }

    private string DescribeError(OnlineError error, string serverMessage)
    {
        var text = error switch
        {
            OnlineError.NotConfigured => _localization["Tone3000_NeedClientId"],
            OnlineError.NotSignedIn => _localization["Online_Error_NotSignedIn"],
            OnlineError.Network => _localization["Online_Error_Network"],
            OnlineError.Unauthorized => _localization["Online_Error_Unauthorized"],
            OnlineError.InvalidRequest => _localization["Online_Error_InvalidRequest"],
            _ => _localization["Online_Error_Server"]
        };
        return string.IsNullOrWhiteSpace(serverMessage) ? text : $"{text} ({serverMessage})";
    }

    private void RaiseAuthState()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(SignedInText));
    }
}
