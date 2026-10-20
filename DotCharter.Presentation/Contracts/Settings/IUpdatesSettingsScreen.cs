using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Updates: the recovery strip for an update an earlier run
///     staged but never applied, the manual check and its startup toggle, and
///     the GitHub sign-in used for private-repo checks — shown only when the
///     build's update feed needs it, never in the public build. The <c>Is…Visible</c>
///     flags are how the cross-tab search filters this tab's rows.
/// </summary>
public interface IUpdatesSettingsScreen : INotifyPropertyChanged
{
    bool HasInterruptedUpdate { get; }

    /// <summary>What was staged and when.</summary>
    string InterruptedUpdateText { get; }

    /// <summary>The tail of the updater transcript, or why it cannot be finished.</summary>
    string InterruptedUpdateDetail { get; }

    bool HasInterruptedUpdateDetail { get; }

    /// <summary>False when the payload is gone or cannot apply — the only honest offer left is Discard.</summary>
    bool CanFinishInterruptedUpdate { get; }

    ICommand FinishInterruptedUpdateCommand { get; }

    ICommand DiscardInterruptedUpdateCommand { get; }

    bool IsUpdatesGroupVisible { get; }

    bool IsCurrentVersionRowVisible { get; }

    string CurrentVersionText { get; }

    bool IsCheckNowRowVisible { get; }

    /// <summary>An update was found and is remembered; "Show pending update" re-opens it.</summary>
    bool HasPendingUpdate { get; }

    ICommand ShowPendingUpdateCommand { get; }

    ICommand CheckForUpdatesCommand { get; }

    bool IsChecking { get; }

    bool IsCheckOnStartupRowVisible { get; }

    bool UpdateCheckOnStartup { get; set; }

    /// <summary>What the last manual check said, empty before one.</summary>
    string CheckStatusText { get; }

    bool HasCheckStatus { get; }

    bool IsGitHubGroupVisible { get; }

    IGitHubSignInPart GitHub { get; }
}

/// <summary>The GitHub sign-in block: the OAuth device flow and the pasted personal access token.</summary>
public interface IGitHubSignInPart : INotifyPropertyChanged
{
    bool IsAccountRowVisible { get; }

    bool IsSignedIn { get; }

    /// <summary>"Signed in as octocat" / "Not signed in".</summary>
    string SignedInText { get; }

    ICommand SignOutCommand { get; }

    /// <summary>False when the feed supplies no client id: only the pasted token is offered.</summary>
    bool IsDeviceFlowRowVisible { get; }

    bool IsDeviceFlowRunning { get; }

    ICommand StartDeviceFlowCommand { get; }

    ICommand CancelDeviceFlowCommand { get; }

    /// <summary>A device code is waiting to be entered on GitHub.</summary>
    bool HasDeviceCode { get; }

    string DeviceUserCode { get; }

    string DeviceVerificationUri { get; }

    ICommand CopyDeviceCodeCommand { get; }

    ICommand OpenVerificationPageCommand { get; }

    bool IsPatRowVisible { get; }

    /// <summary>The token being typed; never persisted, cleared once stored.</summary>
    string PatInput { get; set; }

    ICommand SignInWithPatCommand { get; }

    string AuthStatusText { get; }

    bool HasAuthStatus { get; }
}
