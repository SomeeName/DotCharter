using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Updates tab in every state the screen can be in.</summary>
public sealed class FakeUpdatesSettingsScreen : ObservableObject, IUpdatesSettingsScreen
{
    private bool _updateCheckOnStartup = true;

    private FakeUpdatesSettingsScreen(string version, FakeGitHubSignInPart gitHub)
    {
        CurrentVersionText = version;
        GitHub = gitHub;
        FinishInterruptedUpdateCommand = new RelayCommand(() => { });
        DiscardInterruptedUpdateCommand = new RelayCommand(() => { });
        ShowPendingUpdateCommand = new RelayCommand(() => { });
        CheckForUpdatesCommand = new RelayCommand(() => { });
    }

    public static IReadOnlyList<FakeVariant<IUpdatesSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new("UpdateAvailable", UpdateAvailable),
        new("Checking", Checking),
        new("Interrupted", Interrupted),
        new("Blocked", Blocked),
        new("DeviceFlow", DeviceFlow),
        new("SignedIn", SignedIn),
        new("PublicBuild", PublicBuild)
    ];

    public static FakeUpdatesSettingsScreen Default() => new("0.62.1", new FakeGitHubSignInPart());

    public static FakeUpdatesSettingsScreen Empty() =>
        new("0.0.0", new FakeGitHubSignInPart { SignedInText = "" }) { _updateCheckOnStartup = false };

    public static FakeUpdatesSettingsScreen LongText() =>
        new("0.62.1", new FakeGitHubSignInPart
        {
            SignedInText = "Angemeldet als donaudampfschifffahrtsgesellschaftskapitaen-mit-langem-namen",
            IsSignedIn = true,
            AuthStatusText = "Die Anmeldung ist fehlgeschlagen: 非常に長いエラーメッセージのテストケースです。GitHubからの応答を確認してください。"
        })
        {
            HasInterruptedUpdate = true,
            InterruptedUpdateText = "Version 0.63.0 wurde am 19.09.2026 14:02 vorbereitet, aber nie übernommen — 非常に長い説明文",
            InterruptedUpdateDetail = "Das Update-Skript konnte nicht gestartet werden: Zugriff verweigert (0x80070005).\nSTAGING: C:\\Program Files\\DotCharter\\update-staging",
            CanFinishInterruptedUpdate = true,
            CheckStatusText = "Prüfung fehlgeschlagen: Der Server hat mit 503 Service Unavailable geantwortet (nach 30 Sekunden)."
        };

    public static FakeUpdatesSettingsScreen UpdateAvailable() =>
        new("0.62.1", new FakeGitHubSignInPart())
        {
            HasPendingUpdate = true,
            CheckStatusText = "Update v0.63.0 is available."
        };

    public static FakeUpdatesSettingsScreen Checking() =>
        new("0.62.1", new FakeGitHubSignInPart()) { IsChecking = true, CheckStatusText = "Checking for updates…" };

    public static FakeUpdatesSettingsScreen Interrupted() =>
        new("0.62.1", new FakeGitHubSignInPart())
        {
            HasInterruptedUpdate = true,
            InterruptedUpdateText = "Version 0.63.0 was staged on 2026-09-19 14:02 but never applied.",
            InterruptedUpdateDetail = "[14:02:11] waiting for PID 18332 to exit\n[14:02:41] timed out; nothing copied",
            CanFinishInterruptedUpdate = true
        };

    public static FakeUpdatesSettingsScreen Blocked() =>
        new("0.62.1", new FakeGitHubSignInPart())
        {
            HasInterruptedUpdate = true,
            InterruptedUpdateText = "Version 0.63.0 was staged on 2026-09-19 14:02 but never applied.",
            InterruptedUpdateDetail = "The staged payload cannot be used: the staging folder is gone.",
            CanFinishInterruptedUpdate = false
        };

    public static FakeUpdatesSettingsScreen DeviceFlow() =>
        new("0.62.1", new FakeGitHubSignInPart
        {
            IsDeviceFlowRunning = true,
            DeviceUserCode = "WDJB-MJHT",
            DeviceVerificationUri = "https://github.com/login/device",
            AuthStatusText = "Waiting for you to authorize on GitHub…"
        });

    public static FakeUpdatesSettingsScreen SignedIn() =>
        new("0.62.1", new FakeGitHubSignInPart
        {
            IsSignedIn = true,
            SignedInText = "Signed in as octocat"
        });

    /// <summary>The public build: its feed needs no sign-in, so the GitHub group is absent.</summary>
    public static FakeUpdatesSettingsScreen PublicBuild() =>
        new("1.0.0", new FakeGitHubSignInPart()) { IsGitHubGroupVisible = false };

    public bool HasInterruptedUpdate { get; private init; }

    public string InterruptedUpdateText { get; private init; } = "";

    public string InterruptedUpdateDetail { get; private init; } = "";

    public bool HasInterruptedUpdateDetail => InterruptedUpdateDetail.Length > 0;

    public bool CanFinishInterruptedUpdate { get; private init; }

    public ICommand FinishInterruptedUpdateCommand { get; }

    public ICommand DiscardInterruptedUpdateCommand { get; }

    public bool IsUpdatesGroupVisible => true;

    public bool IsCurrentVersionRowVisible => true;

    public string CurrentVersionText { get; }

    public bool IsCheckNowRowVisible => true;

    public bool HasPendingUpdate { get; private init; }

    public ICommand ShowPendingUpdateCommand { get; }

    public ICommand CheckForUpdatesCommand { get; }

    public bool IsChecking { get; private init; }

    public bool IsCheckOnStartupRowVisible => true;

    public bool UpdateCheckOnStartup
    {
        get => _updateCheckOnStartup;
        set => SetProperty(ref _updateCheckOnStartup, value);
    }

    public string CheckStatusText { get; private init; } = "";

    public bool HasCheckStatus => CheckStatusText.Length > 0;

    public bool IsGitHubGroupVisible { get; private init; } = true;

    public IGitHubSignInPart GitHub { get; }

    private sealed class FakeGitHubSignInPart : ObservableObject, IGitHubSignInPart
    {
        private string _patInput = "";

        public FakeGitHubSignInPart()
        {
            SignOutCommand = new RelayCommand(() => { });
            StartDeviceFlowCommand = new RelayCommand(() => { });
            CancelDeviceFlowCommand = new RelayCommand(() => { });
            CopyDeviceCodeCommand = new RelayCommand(() => { });
            OpenVerificationPageCommand = new RelayCommand(() => { });
            SignInWithPatCommand = new RelayCommand(() => { });
        }

        public bool IsAccountRowVisible => true;

        public bool IsSignedIn { get; init; }

        public string SignedInText { get; init; } = "Not signed in";

        public ICommand SignOutCommand { get; }

        public bool IsDeviceFlowRowVisible => true;

        public bool IsDeviceFlowRunning { get; init; }

        public ICommand StartDeviceFlowCommand { get; }

        public ICommand CancelDeviceFlowCommand { get; }

        public bool HasDeviceCode => DeviceUserCode.Length > 0;

        public string DeviceUserCode { get; init; } = "";

        public string DeviceVerificationUri { get; init; } = "";

        public ICommand CopyDeviceCodeCommand { get; }

        public ICommand OpenVerificationPageCommand { get; }

        public bool IsPatRowVisible => true;

        public string PatInput
        {
            get => _patInput;
            set => SetProperty(ref _patInput, value);
        }

        public ICommand SignInWithPatCommand { get; }

        public string AuthStatusText { get; init; } = "";

        public bool HasAuthStatus => AuthStatusText.Length > 0;
    }
}
