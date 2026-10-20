using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="IStartScreen" /> for the gallery and the
///     layout smoke tests.
///     <para>
///         The commands MUTATE this object — picking a profile moves the
///         selection, the guest toggle swaps the panel — so the gallery's copy
///         of the door is interactive with no services at all. They are plain
///         <see cref="RelayCommand" /> FIELDS rather than <c>[RelayCommand]</c>
///         methods on purpose: a generated <c>…Command</c> is linted for
///         reachability against the view model that declares it, and a fake
///         bound through an INTERFACE <c>x:DataType</c> would read as ambiguous
///         there.
///     </para>
/// </summary>
public sealed class FakeStartScreen : ObservableObject, IStartScreen
{
    private readonly List<FakeProfileTile> _profiles;
    private bool _isGuestEntryOpen;
    private bool _isLefty;
    private string _guestName = "";

    private FakeStartScreen(IEnumerable<FakeProfileTile> profiles, string appVersion,
        IReadOnlyList<IFactTile> facts)
    {
        _profiles = profiles.ToList();
        foreach (var profile in _profiles) profile.Attach(this);
        AppVersion = appVersion;
        StatusFacts = facts;

        StartCommand = new RelayCommand(() => { });
        AddProfileCommand = new RelayCommand(() => { });
        ToggleGuestEntryCommand = new RelayCommand(() =>
        {
            _isGuestEntryOpen = !_isGuestEntryOpen;
            OnPropertyChanged(nameof(IsGuestEntryOpen));
        });
        StartGuestSessionCommand = new RelayCommand(() => { });
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<IStartScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Ready),
        new(FakeVariantNames.Empty, FirstRun),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>The ordinary door: four profiles, one active, everything scanned.</summary>
    public static FakeStartScreen Ready()
    {
        var screen = new FakeStartScreen(
        [
            new FakeProfileTile("Alex", "124 songs · 11 day streak", isLefty: false),
            new FakeProfileTile("Mara", "38 songs · 2 day streak", isLefty: true),
            new FakeProfileTile("Jonas", "7 songs · played yesterday", isLefty: false),
            new FakeProfileTile("Guest (Saturday)", "1 song · tonight", isLefty: false)
        ], "v0.62.1",
        [
            new FakeFactTile("Library", "2,160 songs", isReady: true),
            new FakeFactTile("Latency", "11.6 ms", isReady: true),
            new FakeFactTile("Input", "Focusrite Scarlett · in 1", isReady: true)
        ]);
        screen._profiles[0].SelectCommand.Execute(null);
        return screen;
    }

    /// <summary>
    ///     A whole household on one machine: twelve profiles, more than the rail shows
    ///     without scrolling, with the active one at the bottom.
    /// </summary>
    public static FakeStartScreen Full()
    {
        (string Name, string Detail, bool Lefty)[] people =
        [
            ("Alex", "124 songs · 11 day streak", false), ("Mara", "38 songs · 2 day streak", true),
            ("Jonas", "7 songs · played yesterday", false), ("Lena", "212 songs · 46 day streak", false),
            ("Felix", "19 songs · played last week", true), ("Sophie", "64 songs · 5 day streak", false),
            ("Paul", "3 songs · played in March", false), ("Hannah", "88 songs · 9 day streak", false),
            ("Noah", "41 songs · played on Monday", true), ("Emma", "150 songs · 30 day streak", false),
            ("Ben", "12 songs · played yesterday", false), ("Guest (Saturday)", "1 song · tonight", false)
        ];
        var screen = new FakeStartScreen(people.Select(p => new FakeProfileTile(p.Name, p.Detail, p.Lefty)), "v0.62.1",
        [
            new FakeFactTile("Library", "2,160 songs", isReady: true),
            new FakeFactTile("Latency", "11.6 ms", isReady: true),
            new FakeFactTile("Input", "Focusrite Scarlett · in 1", isReady: true)
        ]);
        screen._profiles[^1].SelectCommand.Execute(null);
        return screen;
    }

    /// <summary>
    ///     First run: no profile exists yet, nothing is configured and the guest
    ///     panel is the only way in. This is the state the current door boots in.
    /// </summary>
    public static FakeStartScreen FirstRun()
    {
        var screen = new FakeStartScreen([], "v0.62.1",
        [
            new FakeFactTile("Library", "Not scanned yet", isWarning: true),
            new FakeFactTile("Latency", "Unknown", isWarning: true),
            new FakeFactTile("Input", "No guitar input", isWarning: true)
        ]);
        screen.ToggleGuestEntryCommand.Execute(null);
        return screen;
    }

    /// <summary>
    ///     The stress case: names at sentence length in three scripts, an
    ///     over-long device string and a guest name already typed.
    /// </summary>
    public static FakeStartScreen LongText()
    {
        var screen = new FakeStartScreen(
        [
            new FakeProfileTile(FakeFlowText.LongProfileName,
                "2.160 Songs · 428 Tage Übungsserie · zuletzt gespielt vor elf Monaten", isLefty: true),
            new FakeProfileTile("非常に長いプロフィール名のテストケースです",
                "1,204 曲 · 87 日連続 · 昨日プレイしました", isLefty: false),
            new FakeProfileTile("The Extraordinarily Long Profile Name Appreciation Society",
                "12,345 songs · 999 day streak · last played eleven and a half months ago", isLefty: false)
        ], "v0.62.1-rc4+build.20260917.1142",
        [
            new FakeFactTile("Library", "12.345 Songs in vier Ordnern", isReady: true),
            new FakeFactTile("Latency", "128.75 ms — ASIO buffer 2048", isWarning: true),
            new FakeFactTile("Input", "Universal Audio Apollo Twin X DUO Heritage Edition · input 3",
                isReady: true)
        ]);
        screen._profiles[0].SelectCommand.Execute(null);
        screen.GuestName = "Ein sehr langer Gastname für den Samstagabend";
        return screen;
    }

    // ── IStartScreen ──────────────────────────────────────────────────────

    public string AppVersion { get; }

    public IReadOnlyList<IProfileTile> Profiles => _profiles;

    public bool HasProfiles => _profiles.Count > 0;

    public string ActiveProfileName => _profiles.FirstOrDefault(p => p.IsActive)?.Name ?? "";

    public bool IsLefty
    {
        get => _isLefty;
        set => SetProperty(ref _isLefty, value);
    }

    public IReadOnlyList<IFactTile> StatusFacts { get; }

    public ICommand StartCommand { get; }

    public bool CanStart => HasProfiles && ActiveProfileName.Length > 0;

    public ICommand AddProfileCommand { get; }

    public bool IsGuestEntryOpen => _isGuestEntryOpen;

    public ICommand ToggleGuestEntryCommand { get; }

    public string GuestName
    {
        get => _guestName;
        set
        {
            if (!SetProperty(ref _guestName, value)) return;
            OnPropertyChanged(nameof(CanStartGuestSession));
        }
    }

    public ICommand StartGuestSessionCommand { get; }

    public bool CanStartGuestSession => _guestName.Trim().Length > 0;

    // ── the fake's own machinery ──────────────────────────────────────────

    internal void Pick(FakeProfileTile tile)
    {
        foreach (var profile in _profiles) profile.SetActive(ReferenceEquals(profile, tile));
        IsLefty = tile.IsLefty;
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(CanStart));
    }
}

/// <summary>One profile in the switcher. Only the selection moves.</summary>
public sealed class FakeProfileTile : ObservableObject, IProfileTile
{
    private FakeStartScreen? _owner;
    private bool _isActive;

    public FakeProfileTile(string name, string detailLabel, bool isLefty)
    {
        Name = name;
        Initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        DetailLabel = detailLabel;
        IsLefty = isLefty;
        SelectCommand = new RelayCommand(() => _owner?.Pick(this));
    }

    public string Name { get; }

    public string Initial { get; }

    public string DetailLabel { get; }

    public bool IsActive => _isActive;

    public bool IsLefty { get; }

    public ICommand SelectCommand { get; }

    internal void Attach(FakeStartScreen owner) => _owner = owner;

    internal void SetActive(bool value) => SetProperty(ref _isActive, value, nameof(IsActive));
}
