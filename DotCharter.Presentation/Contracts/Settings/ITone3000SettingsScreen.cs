using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ TONE3000: in-app setup for the tone3000.com NAM-capture
///     integration — a client id, and the sign-in that uses it. The id is a
///     setting like any other; before this screen existed it had to be
///     hand-edited into the config file, which the page's whole-config save
///     then clobbered.
/// </summary>
public interface ITone3000SettingsScreen : INotifyPropertyChanged
{
    bool IsGroupVisible { get; }

    bool IsClientIdRowVisible { get; }

    string ClientId { get; set; }

    /// <summary>Persists the id without leaving the page, so a freshly pasted one can sign in straight away.</summary>
    ICommand SaveClientIdCommand { get; }

    /// <summary>The loopback redirect URI the owner has to register against their client id at tone3000.com.</summary>
    string RedirectUri { get; }

    bool IsRedirectUriVisible { get; }

    bool IsAccountRowVisible { get; }

    /// <summary>"Signed in as …", empty when signed out.</summary>
    string SignedInText { get; }

    bool IsSignedIn { get; }

    bool IsSignedOut { get; }

    /// <summary>
    ///     False without a client id, and false while a sign-in is in flight: a
    ///     running flow OWNS the loopback prefix, so offering a second one is
    ///     offering a bind conflict reported as a failed sign-in.
    /// </summary>
    bool CanSignIn { get; }

    ICommand SignInCommand { get; }

    ICommand SignOutCommand { get; }

    /// <summary>True while a sign-in is waiting on the browser.</summary>
    bool IsBusy { get; }

    /// <summary>
    ///     Abandons a sign-in waiting on the browser, releasing the loopback
    ///     listener. Without it the port stays held for the full five-minute
    ///     callback timeout with no way to give up.
    /// </summary>
    ICommand CancelSignInCommand { get; }

    string StatusText { get; }

    bool HasStatus { get; }

    bool IsStatusError { get; }
}
