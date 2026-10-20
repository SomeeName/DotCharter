using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Home;

namespace DotCharter.Presentation.Fakes.Home;

/// <summary>A services-free Home in every state the screen can be in.</summary>
public sealed partial class FakeHomeScreen : ObservableObject, IHomeScreen
{
    private FakeHomeScreen()
    {
        OpenCommandPaletteCommand = new RelayCommand(() => { });
        OpenSettingsCommand = new RelayCommand(() => { });
        PlayRecentCommand = new RelayCommand(() => { });
        OpenRecentChartCommand = new RelayCommand(() => { });
    }

    public static IReadOnlyList<FakeVariant<IHomeScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("NoDevice", NoDevice),
        new("DeviceLost", DeviceLost)
    ];

    /// <summary>A returning player: a song to resume, some progress, a couple of things to fix.</summary>
    public static FakeHomeScreen Default() => new()
    {
        LibrarySummary = "312 songs in the library, 41 with a chart",
        ProfileSummary = "Alex, player 1",
        DeviceSummary = "Focusrite 2i2, 4.2 ms",
        HasRecentSong = true,
        RecentWhen = "Last played, yesterday",
        RecentTitle = "Take My Bones Away",
        RecentMeta = "Baroness, Yellow & Green, E Standard, 4:05",
        RecentArrangements = Chips(("Lead, best 96 %", true), ("Rhythm, best 71 %", false), ("Bass, not played", false)),
        PlayRecentCaption = "Play the lead path",
        Destinations = StandardDestinations(),
        JumpBackIn = Rows(("Master of Puppets", "88 %"), ("Paranoid Android", "71 %"),
            ("Everlong", "93 %"), ("Seven Nation Army", "99 %")),
    };

    /// <summary><see cref="Full" /> with the cover at <paramref name="path" />.</summary>
    public static FakeHomeScreen WithCover(string path, bool animate = false)
    {
        var full = Full();
        return new FakeHomeScreen
        {
            AnimateHero = animate,
            LibrarySummary = full.LibrarySummary,
            ProfileSummary = full.ProfileSummary,
            DeviceSummary = full.DeviceSummary,
            HasRecentSong = true,
            RecentArtPath = path,
            RecentWhen = full.RecentWhen,
            RecentTitle = full.RecentTitle,
            RecentMeta = full.RecentMeta,
            RecentArrangements = full.RecentArrangements,
            PlayRecentCaption = full.PlayRecentCaption,
            Destinations = full.Destinations,
            JumpBackIn = full.JumpBackIn,
        };
    }

    /// <summary>A fresh install: no library, nothing played, nothing to resume.</summary>
    public static FakeHomeScreen Empty() => new()
    {
        CanOpenRecentChart = false,
        LibrarySummary = "No songs yet",
        ProfileSummary = "Player 1",
        DeviceSummary = "",
        Destinations = StandardDestinations(firstSubtitle: "Add music to start playing",
            chartSubtitle: "No charts yet", rigSubtitle: "Default tone",
            librarySubtitle: "No library folder set"),
        RecentArrangements = [],
        JumpBackIn = [],
    };

    public static FakeHomeScreen LongText() => new()
    {
        LibrarySummary = "1.284 Songs in der Bibliothek, 207 davon mit einem Chart — 非常に長いライブラリの概要",
        ProfileSummary = "Donaudampfschifffahrtsgesellschaftskapitän, Spieler 1",
        DeviceSummary = "Focusrite Scarlett 18i20 3rd Gen (ASIO), 4,2 ms",
        HasRecentSong = true,
        RecentWhen = "Zuletzt gespielt, gestern",
        RecentTitle = "Ein außergewöhnlich langer Songtitel · こんにちは世界",
        RecentMeta = "Baroness, Yellow & Green, Ein außergewöhnlich langer Albumtitel, E Standard, 4:05",
        RecentArrangements = Chips(("Lead-Stimme, bester Durchgang 96 %", true),
            ("Rhythmus-Stimme, bester Durchgang 71 %", false), ("Bass, noch nicht gespielt", false)),
        PlayRecentCaption = "Die Lead-Stimme spielen",
        Destinations = StandardDestinations(
            firstSubtitle: "Einen Song auswählen und mit dem Highway spielen",
            chartSubtitle: "4 Charts in Arbeit, 3 davon mit Export-Blockern",
            rigSubtitle: "Crunch-Lead, bearbeitet, 8 Einheiten, Szene A",
            librarySubtitle: "1.284 Songs, 38 mit Audio und noch ohne Chart"),
        JumpBackIn = Rows(("Ein außergewöhnlich langer Songtitel zum Testen", "88 %"),
            ("こんにちは世界 — 非常に長い曲名", "71 %")),
    };

    /// <summary>Everything populated at once, for layout.</summary>
    public static FakeHomeScreen Full() => new()
    {
        LibrarySummary = "1,284 songs in the library, 207 with a chart",
        ProfileSummary = "Alex, player 1",
        DeviceSummary = "Focusrite 2i2, 4.2 ms",
        HasRecentSong = true,
        RecentWhen = "Last played, 20 minutes ago",
        RecentTitle = "Master of Puppets",
        RecentMeta = "Metallica, Master of Puppets, E♭ Standard, 8:35",
        RecentArrangements = Chips(("Lead, best 96 %", true), ("Rhythm, best 71 %", false),
            ("Bass, best 84 %", false), ("Vocals, not played", false)),
        PlayRecentCaption = "Play the lead path",
        Destinations = StandardDestinations(),
        JumpBackIn = Rows(("Master of Puppets", "88 %"), ("Paranoid Android", "71 %"), ("Everlong", "93 %"),
            ("Seven Nation Army", "99 %"), ("Take My Bones Away", "62 %"), ("Black Dog", "45 %")),
    };

    /// <summary>No audio device open — the chip goes rather than reading "none".</summary>
    public static FakeHomeScreen NoDevice() => new()
    {
        LibrarySummary = "312 songs in the library, 41 with a chart",
        ProfileSummary = "Alex, player 1",
        DeviceSummary = "",
        HasRecentSong = true,
        RecentWhen = "Last played, yesterday",
        RecentTitle = "Take My Bones Away",
        RecentMeta = "Baroness, Yellow & Green, E Standard, 4:05",
        RecentArrangements = Chips(("Lead, best 96 %", true), ("Bass, not played", false)),
        PlayRecentCaption = "Play the lead path",
        Destinations = StandardDestinations(),
        JumpBackIn = Rows(("Master of Puppets", "88 %")),
    };

    /// <summary>The engine lost its device: the one state this screen can call WRONG.</summary>
    public static FakeHomeScreen DeviceLost() => new()
    {
        LibrarySummary = "312 songs in the library, 41 with a chart",
        ProfileSummary = "Alex, player 1",
        DeviceSummary = "No audio device",
        DeviceNeedsAttention = true,
        HasRecentSong = true,
        RecentWhen = "Last played, yesterday",
        RecentTitle = "Take My Bones Away",
        RecentMeta = "Baroness, Yellow & Green, E Standard, 4:05",
        RecentArrangements = Chips(("Lead, best 96 %", true)),
        PlayRecentCaption = "Play the lead path",
        Destinations = StandardDestinations(),
        JumpBackIn = Rows(("Everlong", "93 %")),
    };

    public string LibrarySummary { get; private init; } = "";

    public string ProfileSummary { get; private init; } = "";

    public string DeviceSummary { get; private init; } = "";

    public bool HasDevice => DeviceSummary.Length > 0;

    public bool DeviceNeedsAttention { get; private init; }

    public ICommand OpenCommandPaletteCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public bool HasRecentSong { get; private init; }

    /// <summary>A cover on disk, for a test that needs a real one; the gallery variants have none.</summary>
    public string RecentArtPath { get; init; } = "";

    public bool HasRecentArt => RecentArtPath.Length > 0;

    /// <summary>Off, like the app's default; a test that needs the moving hero sets it.</summary>
    public bool AnimateHero { get; init; }

    public string RecentWhen { get; private init; } = "";

    public string RecentTitle { get; private init; } = "";

    public string RecentMeta { get; private init; } = "";

    public IReadOnlyList<IArrangementChip> RecentArrangements { get; private init; } = [];

    public string PlayRecentCaption { get; private init; } = "Play";

    public ICommand PlayRecentCommand { get; }

    public ICommand OpenRecentChartCommand { get; }

    public bool CanOpenRecentChart { get; private init; } = true;

    public IReadOnlyList<IDestinationCard> Destinations { get; private init; } = [];

    public bool HasJumpBackIn => JumpBackIn.Count > 0;

    public IReadOnlyList<IProgressRow> JumpBackIn { get; private init; } = [];

}
