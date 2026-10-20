using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free TONE3000 tab in every state the screen can be in.</summary>
public sealed class FakeTone3000SettingsScreen : ObservableObject, ITone3000SettingsScreen
{
    private string _clientId = "";

    private FakeTone3000SettingsScreen()
    {
        SaveClientIdCommand = new RelayCommand(() => { });
        SignInCommand = new RelayCommand(() => { });
        SignOutCommand = new RelayCommand(() => { });
        CancelSignInCommand = new RelayCommand(() => { });
    }

    public static IReadOnlyList<FakeVariant<ITone3000SettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new("SignedIn", SignedIn),
        new(FakeVariantNames.Busy, Busy),
        new("Error", Error)
    ];

    /// <summary>A client id is set and the user is signed out — the state a sign-in starts from.</summary>
    public static FakeTone3000SettingsScreen Default() =>
        new() { _clientId = "t3k_8a61f9b3a7aba766", CanSignIn = true };

    /// <summary>No client id yet: the sign-in cannot be offered.</summary>
    public static FakeTone3000SettingsScreen Empty() => new();

    public static FakeTone3000SettingsScreen LongText() =>
        new()
        {
            _clientId = "t3k_donaudampfschifffahrtsgesellschaftskapitaen_8a61f9b3a7aba766ffffffff",
            IsSignedIn = true,
            SignedInText = "Angemeldet als donaudampfschifffahrtsgesellschaftskapitaen-mit-langem-namen",
            StatusText = "Die Anmeldung ist fehlgeschlagen: 非常に長いエラーメッセージのテストケースです。tone3000.com からの応答を確認してください。",
            IsStatusError = true
        };

    public static FakeTone3000SettingsScreen SignedIn() =>
        new()
        {
            _clientId = "t3k_8a61f9b3a7aba766",
            IsSignedIn = true,
            SignedInText = "Signed in as octocat",
            CanSignIn = true
        };

    /// <summary>Waiting on the browser: the sign-in is withdrawn and Cancel is the way out.</summary>
    public static FakeTone3000SettingsScreen Busy() =>
        new() { _clientId = "t3k_8a61f9b3a7aba766", IsBusy = true };

    public static FakeTone3000SettingsScreen Error() =>
        new()
        {
            _clientId = "t3k_wrong",
            CanSignIn = true,
            StatusText = "The sign-in was refused (401 Unauthorized) — check the client id.",
            IsStatusError = true
        };

    public bool IsGroupVisible => true;

    public bool IsClientIdRowVisible => true;

    public string ClientId
    {
        get => _clientId;
        set => SetProperty(ref _clientId, value);
    }

    public ICommand SaveClientIdCommand { get; }

    public string RedirectUri => "http://localhost:47821/tone3000/callback/";

    public bool IsRedirectUriVisible => true;

    public bool IsAccountRowVisible => true;

    public string SignedInText { get; private init; } = "";

    public bool IsSignedIn { get; private init; }

    public bool IsSignedOut => !IsSignedIn;

    public bool CanSignIn { get; private init; }

    public ICommand SignInCommand { get; }

    public ICommand SignOutCommand { get; }

    public bool IsBusy { get; private init; }

    public ICommand CancelSignInCommand { get; }

    public string StatusText { get; private init; } = "";

    public bool HasStatus => StatusText.Length > 0;

    public bool IsStatusError { get; private init; }
}
