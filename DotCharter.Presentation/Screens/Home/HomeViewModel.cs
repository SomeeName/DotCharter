using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Home;
using DotCharter.Presentation.Contracts.Shell;
using DotCharter.Presentation.Navigation;
using DotCharter.Services.Session;

namespace DotCharter.Presentation.Screens.Home;

/// <summary>
///     The app's landing screen over real services.
///     <para>
///         It is a projection of <see cref="HomeSnapshot" /> with the verbs
///         attached: the snapshot knows nothing about navigation and the screen
///         knows nothing about where a figure came from, which is what lets
///         each half be tested on its own.
///     </para>
/// </summary>
public sealed class HomeViewModel : ObservableObject, IHomeScreen, IScreenLifecycle
{
    private readonly HomeSnapshotSource _source;
    private readonly IShellDestinations _shell;
    private readonly IProjectSessionService _session;
    private readonly ILocalizationService _localization;
    private readonly Func<DateTime> _nowUtc;
    private readonly Func<Func<HomeSnapshot>, Task<HomeSnapshot>> _offTheUiThread;
    private int _reads;

    private HomeSnapshot _snapshot = HomeSnapshot.Empty;
    private IReadOnlyList<IArrangementChip> _arrangements = [];
    private IReadOnlyList<IProgressRow> _jumpBackIn = [];
    private IReadOnlyList<IDestinationCard> _destinations = [];

    public HomeViewModel(
        HomeSnapshotSource source,
        IShellDestinations shell,
        IProjectSessionService session,
        ILocalizationService localization,
        Func<DateTime>? nowUtc = null,
        Func<Func<HomeSnapshot>, Task<HomeSnapshot>>? offTheUiThread = null)
    {
        _source = source;
        _shell = shell;
        _session = session;
        _localization = localization;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _offTheUiThread = offTheUiThread ?? Task.Run;

        OpenCommandPaletteCommand = new RelayCommand(_shell.OpenCommandPalette);
        OpenSettingsCommand = new RelayCommand(() => _shell.Go(ShellDestination.Settings));
        PlayRecentCommand = new AsyncRelayCommand(() =>
            _shell.PlaySongAsync(_snapshot.Hero.SongKey, _snapshot.Hero.ArrangementName));
        OpenRecentChartCommand = new RelayCommand(OpenHeroChart, () => CanOpenRecentChart);

        Refresh();
    }

    /// <summary>
    ///     Re-reads every figure — the screen was shown. The read (the library
    ///     index, the play history, the covers) runs OFF the UI thread, and only a
    ///     CHANGED snapshot is published, so a return that finds nothing new
    /// rebuilds no card and raises no binding.
    /// </summary>
    public void OnShown() => _ = RefreshAsync();

    public void OnHidden()
    {
    }

    /// <summary>Re-reads every figure, here and now (the first show needs them before it paints).</summary>
    public void Refresh()
    {
        _reads++;
        Publish(_source.Read(_nowUtc()));
    }

    /// <summary>
    ///     Takes what only the UI thread may read here, reads the rest off it, and
    ///     publishes the result unless a later refresh has started since or nothing
    ///     changed.
    /// </summary>
    public async Task RefreshAsync()
    {
        var read = ++_reads;
        var live = _source.CaptureLive();
        var now = _nowUtc();
        var snapshot = await _offTheUiThread(() => _source.Read(live, now));
        if (read == _reads) Publish(snapshot);
    }

    private void Publish(HomeSnapshot snapshot)
    {
        if (snapshot.SameAs(_snapshot) && _destinations.Count > 0) return;
        _snapshot = snapshot;
        _arrangements = [.. _snapshot.Hero.Arrangements.Select(a => new Chip(a.Label, a.IsBest))];
        _jumpBackIn = [.. _snapshot.JumpBackIn.Select(r => new Row(r,
            new AsyncRelayCommand(() => _shell.PlaySongAsync(r.SongKey, r.ArrangementName))))];
        _destinations = BuildDestinations();
        ((RelayCommand)OpenRecentChartCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(string.Empty);
    }

    public string LibrarySummary => _snapshot.Header.LibrarySummary;

    public string ProfileSummary => _snapshot.Header.ProfileSummary;

    public string DeviceSummary => _snapshot.Header.DeviceSummary;

    public bool HasDevice => DeviceSummary.Length > 0;

    public bool DeviceNeedsAttention => _snapshot.Header.DeviceNeedsAttention;

    public ICommand OpenCommandPaletteCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public bool HasRecentSong => _snapshot.Hero.HasSong;

    public string RecentArtPath => _snapshot.Hero.ArtPath;

    public bool HasRecentArt => RecentArtPath.Length > 0;

    public bool AnimateHero => _snapshot.AnimateHero;

    public string RecentWhen => _snapshot.Hero.When;

    public string RecentTitle => _snapshot.Hero.Title;

    public string RecentMeta => _snapshot.Hero.Meta;

    public IReadOnlyList<IArrangementChip> RecentArrangements => _arrangements;

    public string PlayRecentCaption => _snapshot.Hero.PlayCaption;

    public ICommand PlayRecentCommand { get; }

    public ICommand OpenRecentChartCommand { get; }

    /// <summary>The hero's song is the chart the editor has open, so "Open the chart"
    /// goes to it. Otherwise the link is absent: it sat under one song and opened
    /// the last SAVED project, a different one.</summary>
    public bool CanOpenRecentChart =>
        _snapshot.Hero.HasSong && _snapshot.LoadedChart.Length > 0
        && string.Equals(_snapshot.LoadedChart, _snapshot.Hero.Title, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<IDestinationCard> Destinations => _destinations;

    public bool HasJumpBackIn => _jumpBackIn.Count > 0;

    public IReadOnlyList<IProgressRow> JumpBackIn => _jumpBackIn;

    private IReadOnlyList<IDestinationCard> BuildDestinations()
    {
        var hasLibrary = _snapshot.LibrarySongCount > 0;
        return
        [
            new Destination(
                L("Home_Dest_Play"),
                hasLibrary ? L("Home_Dest_Play_Sub") : L("Home_Dest_Play_Empty"),
                "IconGuitar", hasLibrary, GoTo(ShellTarget.Play)),
            ChartCard(),
            new Destination(
                L("Home_Dest_Rig"), L("Home_Dest_Rig_Sub"),
                "IconAmp", false, new RelayCommand(_shell.OpenRig)),
            new Destination(
                L("Home_Dest_Library"),
                hasLibrary ? L("Home_Dest_Library_Sub") : L("Home_Dest_Library_Empty"),
                "IconLibraryMusic", !hasLibrary, GoTo(ShellTarget.Library))
        ];
    }

    /// <summary>The card names what a click opens: the chart loaded now (a song played
    /// from the library is loaded too), else the last saved project, which the click
    /// then OPENS rather than showing an empty editor under its name.</summary>
    private Destination ChartCard()
    {
        var loaded = _snapshot.LoadedChart;
        if (loaded.Length > 0)
            return new Destination(L("Home_Dest_Chart"), loaded, "IconChartLine", false, GoTo(ShellTarget.Chart));
        if (_snapshot.RecentProject.Exists)
        {
            return new Destination(L("Home_Dest_Chart"), _snapshot.RecentProject.Name, "IconChartLine", false,
                new RelayCommand(OpenRecentProject));
        }

        return new Destination(L("Home_Dest_Chart"), L("Home_Dest_Chart_Empty"), "IconChartLine", false,
            GoTo(ShellTarget.Chart));
    }

    private RelayCommand GoTo(ShellTarget? target) =>
        new(() =>
        {
            if (target is { } t) _shell.Go(Map(t));
        }, () => target is not null);

    private static ShellDestination Map(ShellTarget target) => target switch
    {
        ShellTarget.Library => ShellDestination.Library,
        ShellTarget.Chart => ShellDestination.Chart,
        ShellTarget.Play => ShellDestination.Play,
        _ => ShellDestination.Settings
    };

    private void OpenHeroChart()
    {
        if (CanOpenRecentChart) _shell.Go(ShellDestination.Chart);
    }

    private void OpenRecentProject()
    {
        if (!_snapshot.RecentProject.Exists) return;
        var path = _snapshot.RecentProject.Path;
        _shell.Go(ShellDestination.Chart);
        _ = _session.LoadFile(path);
    }

    private string L(string key) => _localization[key];

    private sealed record Chip(string Label, bool IsBest) : IArrangementChip;

    private sealed record Row(ProgressItem Item, ICommand OpenCommand) : IProgressRow
    {
        public string Title => Item.Title;

        public string ArtPath => Item.ArtPath;

        public bool HasArt => ArtPath.Length > 0;

        public string ProgressLabel => Item.ProgressLabel;
    }

    private sealed record Destination(
        string Title, string Subtitle, string IconKey, bool IsPrimary, ICommand OpenCommand) : IDestinationCard;
}
