using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Logging;
using DotCharter.Services.Update;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The GitHub sign-in block of Settings ▸ Updates: the OAuth device flow
///     and the pasted personal access token. It exists only for an update feed
///     that <see cref="IUpdateFeed.RequiresSignIn" />, and the device flow uses
///     that feed's <see cref="IUpdateFeed.SignInClientId" /> — the product holds
///     none. The auth service raises its state change from whatever
///     thread stored the token, so the refresh is posted.
/// </summary>
public sealed class GitHubSignInViewModel : ObservableObject, IGitHubSignInPart, IDisposable
{
    private readonly IGitHubAuthService _auth;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger? _logger;
    private readonly IShellLauncher? _shell;
    private readonly ISystemClipboard? _clipboard;
    private readonly string _clientId;
    private CancellationTokenSource? _deviceFlowCts;
    private bool _isSignedIn;
    private string _signedInText = "";
    private bool _isDeviceFlowRunning;
    private string _deviceUserCode = "";
    private string _deviceVerificationUri = "";
    private string _patInput = "";
    private string _authStatusText = "";
    private bool _isAccountRowVisible = true;
    private bool _isDeviceFlowRowVisible;
    private bool _isPatRowVisible = true;

    public GitHubSignInViewModel(
        IGitHubAuthService auth,
        ILocalizationService localization,
        IUiDispatcher ui,
        IEnumerable<IUpdateFeed> registeredFeeds,
        IAppLogger? logger = null,
        IShellLauncher? shell = null,
        ISystemClipboard? clipboard = null)
    {
        _auth = auth;
        _localization = localization;
        _ui = ui;
        _logger = logger;
        _shell = shell;
        _clipboard = clipboard;
        // The feed the update check reads, by the same rule as the composition root.
        var feed = UpdateFeed.Select(registeredFeeds);
        IsAvailable = feed.RequiresSignIn;
        _clientId = IsAvailable ? feed.SignInClientId?.Trim() ?? "" : "";
        _isDeviceFlowRowVisible = _clientId.Length > 0;

        SignOutCommand = new AsyncRelayCommand(SignOutAsync);
        StartDeviceFlowCommand = new AsyncRelayCommand(StartDeviceFlowAsync);
        CancelDeviceFlowCommand = new RelayCommand(CancelDeviceFlow);
        CopyDeviceCodeCommand = new AsyncRelayCommand(CopyDeviceCodeAsync);
        OpenVerificationPageCommand = new RelayCommand(OpenVerificationPage);
        SignInWithPatCommand = new AsyncRelayCommand(SignInWithPatAsync);

        _auth.AuthStateChanged += OnAuthStateChanged;
    }

    /// <summary>Whether the build's update feed needs a sign-in at all; the public one does not.</summary>
    public bool IsAvailable { get; }

    public bool IsAccountRowVisible { get => _isAccountRowVisible; private set => SetProperty(ref _isAccountRowVisible, value); }

    public bool IsSignedIn { get => _isSignedIn; private set => SetProperty(ref _isSignedIn, value); }

    public string SignedInText { get => _signedInText; private set => SetProperty(ref _signedInText, value); }

    public ICommand SignOutCommand { get; }

    public bool IsDeviceFlowRowVisible { get => _isDeviceFlowRowVisible; private set => SetProperty(ref _isDeviceFlowRowVisible, value); }

    public bool IsDeviceFlowRunning { get => _isDeviceFlowRunning; private set => SetProperty(ref _isDeviceFlowRunning, value); }

    public ICommand StartDeviceFlowCommand { get; }

    public ICommand CancelDeviceFlowCommand { get; }

    public bool HasDeviceCode => _deviceUserCode.Length > 0;

    public string DeviceUserCode
    {
        get => _deviceUserCode;
        private set
        {
            if (SetProperty(ref _deviceUserCode, value)) OnPropertyChanged(nameof(HasDeviceCode));
        }
    }

    public string DeviceVerificationUri { get => _deviceVerificationUri; private set => SetProperty(ref _deviceVerificationUri, value); }

    public ICommand CopyDeviceCodeCommand { get; }

    public ICommand OpenVerificationPageCommand { get; }

    public bool IsPatRowVisible { get => _isPatRowVisible; private set => SetProperty(ref _isPatRowVisible, value); }

    public string PatInput { get => _patInput; set => SetProperty(ref _patInput, value ?? ""); }

    public ICommand SignInWithPatCommand { get; }

    public bool HasAuthStatus => _authStatusText.Length > 0;

    public string AuthStatusText
    {
        get => _authStatusText;
        private set
        {
            if (SetProperty(ref _authStatusText, value)) OnPropertyChanged(nameof(HasAuthStatus));
        }
    }

    public void SetRowVisibility(bool account, bool deviceFlow, bool pat)
    {
        IsAccountRowVisible = account;
        // Browser sign-in needs the feed's client id; without one only the pasted token is offered.
        IsDeviceFlowRowVisible = deviceFlow && _clientId.Length > 0;
        IsPatRowVisible = pat;
    }

    public async Task RefreshAuthStateAsync()
    {
        IsSignedIn = _auth.IsSignedIn;
        if (!IsSignedIn)
        {
            SignedInText = _localization["GitHub_NotSignedIn"];
            return;
        }

        SignedInText = _localization["GitHub_SignedIn"];
        try
        {
            var login = await _auth.GetSignedInLoginAsync();
            if (!string.IsNullOrEmpty(login) && IsSignedIn)
                SignedInText = _localization.Format("GitHub_SignedInAs", login);
        }
        catch (Exception ex)
        {
            // Offline: the plain "signed in" text is good enough, and the log says why.
            _logger?.Trace(AppLogCategory.Application, "GitHub login lookup failed: " + ex.Message);
        }
    }

    public void CancelDeviceFlow()
    {
        _deviceFlowCts?.Cancel();
        _deviceFlowCts = null;
    }

    public void Dispose()
    {
        _auth.AuthStateChanged -= OnAuthStateChanged;
        CancelDeviceFlow();
    }

    private void OnAuthStateChanged() => _ui.Post(() => _ = RefreshAuthStateAsync());

    private async Task StartDeviceFlowAsync()
    {
        // The row is hidden without the feed's client id; a stray command does nothing.
        var clientId = _clientId;
        if (clientId.Length == 0) return;

        CancelDeviceFlow();
        var cts = _deviceFlowCts = new CancellationTokenSource();
        IsDeviceFlowRunning = true;
        try
        {
            var device = await _auth.StartDeviceFlowAsync(clientId, cts.Token);
            DeviceUserCode = device.UserCode;
            DeviceVerificationUri = device.VerificationUri;
            AuthStatusText = _localization["GitHub_DeviceFlowPending"];

            await _auth.WaitForDeviceAuthorizationAsync(clientId, device, cts.Token);
            AuthStatusText = "";
            await RefreshAuthStateAsync();
        }
        catch (OperationCanceledException)
        {
            AuthStatusText = "";
        }
        catch (Exception ex)
        {
            AuthStatusText = _localization.Format("GitHub_DeviceFlowFailed", ex.Message);
        }
        finally
        {
            IsDeviceFlowRunning = false;
            DeviceUserCode = "";
            DeviceVerificationUri = "";
            if (_deviceFlowCts == cts) _deviceFlowCts = null;
        }
    }

    private async Task CopyDeviceCodeAsync()
    {
        if (DeviceUserCode.Length == 0 || _clipboard is null) return;
        await _clipboard.SetTextAsync(DeviceUserCode);
    }

    private void OpenVerificationPage()
    {
        var url = DeviceVerificationUri.Length > 0 ? DeviceVerificationUri : "https://github.com/login/device";
        try
        {
            _shell?.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application, "Could not open the GitHub verification page.", ex);
            AuthStatusText = _localization.Format("GitHub_DeviceFlowFailed", ex.Message);
        }
    }

    private async Task SignInWithPatAsync()
    {
        var token = PatInput.Trim();
        if (token.Length == 0) return;
        try
        {
            _auth.SignInWithPersonalAccessToken(token);
            PatInput = "";
            AuthStatusText = "";
            await RefreshAuthStateAsync();
        }
        catch (Exception ex)
        {
            AuthStatusText = _localization.Format("GitHub_DeviceFlowFailed", ex.Message);
        }
    }

    private async Task SignOutAsync()
    {
        CancelDeviceFlow();
        _auth.SignOut();
        AuthStatusText = "";
        await RefreshAuthStateAsync();
    }
}
