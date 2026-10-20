using System.Globalization;
using DotCharter.Localization;
using DotCharter.Presentation.Shared;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Library.Index;
using DotCharter.Services.Session;

namespace DotCharter.Presentation.Screens.Home;

/// <summary>
///     Reads the Home screen's facts from the services that already know them
///     and formats every one of them.
///     <para>
///         Nothing here scans, probes or opens anything: the library size comes
///         from the index the song browser already maintains, the play history
///         from the practice store, the device from the config plus the
///         engine's last reported latency. A Home screen that triggered work
///         would be a Home screen you cannot afford to open.
///     </para>
/// </summary>
public sealed class HomeSnapshotSource
{
    private const int JumpBackInCap = 6;

    private readonly IConfigurationService _config;
    private readonly IProjectSessionService _session;
    private readonly IGameplayStatsService _stats;
    private readonly IPlayerProfileService _profiles;
    private readonly ISongLibraryLookup _library;
    private readonly IAudioDeviceControl _device;
    private readonly ILocalizationService _localization;

    public HomeSnapshotSource(
        IConfigurationService config,
        IProjectSessionService session,
        IGameplayStatsService stats,
        IPlayerProfileService profiles,
        ISongLibraryLookup library,
        IAudioDeviceControl device,
        ILocalizationService localization)
    {
        _config = config;
        _session = session;
        _stats = stats;
        _profiles = profiles;
        _library = library;
        _device = device;
        _localization = localization;
    }

    /// <param name="nowUtc">Injected so "yesterday" is deterministic in a test.</param>
    public HomeSnapshot Read(DateTime nowUtc) => Read(CaptureLive(), nowUtc);

    /// <summary>
    ///     The facts only the UI thread may read: the config's live recent-project
    ///     list (mutated by every open), the chart the editor has open, the active
    ///     profile and the device. Cheap — no I/O.
    /// </summary>
    public HomeLiveFacts CaptureLive()
    {
        var config = _config.LoadAppConfig();
        var project = _session.GetRecentProjectsUnverified().FirstOrDefault();
        return new HomeLiveFacts(
            _profiles.ActiveProfile.DisplayName, DeviceSummary(config), _device.IsDeviceLost,
            new HomeProject(project?.Path ?? "", project?.DisplayName ?? ""), config.AnimatedHomeHero,
            _session.LoadedChartName);
    }

    /// <summary>
    ///     Everything else — the library index, the play history, the covers — on
    /// ANY thread (a Home return paid this on the UI thread every time).
    ///     The index opens its own read-only connection per call and the stats
    ///     store is locked.
    /// </summary>
    public HomeSnapshot Read(HomeLiveFacts live, DateTime nowUtc)
    {
        var library = _library.ReadFacts();
        var stats = _stats.GetStatistics();
        var recent = stats.RecentlyPlayed.FirstOrDefault();

        return new HomeSnapshot(
            Header: new HomeHeader(LibrarySummary(library), live.ProfileSummary, live.DeviceSummary,
                live.DeviceNeedsAttention),
            Hero: new HomeHero(
                HasSong: recent is not null,
                When: recent is null ? "" : L("Home_LastPlayed", Ago(recent.LastPlayedUtc, nowUtc)),
                Title: recent?.DisplayName ?? "",
                Meta: recent is null ? "" : RecentMeta(recent),
                Arrangements: Arrangements(stats, recent),
                PlayCaption: recent is null ? L("Home_Play") : L("Home_PlayArrangement", recent.ArrangementLabel),
                ArtPath: recent is null ? "" : CoverFor(recent.SongKey))
            {
                SongKey = recent?.SongKey ?? "",
                ArrangementName = recent?.ArrangementName ?? ""
            },
            RecentProject: live.RecentProject,
            JumpBackIn: JumpBackIn(stats),
            LibrarySongCount: library.Songs,
            AnimateHero: live.AnimateHero,
            LoadedChart: live.LoadedChart);
    }

    private string LibrarySummary(LibraryIndexFacts library) =>
        library.IsEmpty ? L("Home_Library_Empty") : L("Home_Library_Summary", library.Songs);

    private string DeviceSummary(AppConfig config)
    {
        if (_device.IsDeviceLost) return L("Home_Device_Lost");

        var name = string.Equals(config.AudioDriver, "ASIO", StringComparison.OrdinalIgnoreCase)
            ? config.AsioOutputDeviceName
            : config.WasapiOutputDeviceName;
        if (string.IsNullOrWhiteSpace(name)) return "";

        // A closed engine reports 0, and an unmeasured latency is left off
        // rather than printed as a confident "0.0 ms".
        var latency = DotCharter.Tone.Audio.Diagnostics.OutputLatency.Ms(_device);
        return latency > 0
            ? L("Home_Device_Summary", name, latency.ToString("0.0", CultureInfo.CurrentCulture))
            : name;
    }

    private string RecentMeta(SongPlayStats recent)
    {
        var sessions = _localization.Plural(
            "Home_SessionCount_One", "Home_SessionCount_Many", recent.SessionCount);
        return recent.HasBestAccuracy
            ? $"{sessions} · {L("Home_Best", Percent(recent.BestAccuracy))} {recent.GradeLabel}"
            : sessions;
    }

    private IReadOnlyList<ArrangementItem> Arrangements(ProfileStatistics stats, SongPlayStats? recent)
    {
        if (recent is null) return [];

        // Every stored arrangement of the SAME song, best first — the hero's
        // chips are "how has each path gone", not "what does the archive
        // contain", which is a question only a psarc read can answer.
        return
        [
            .. stats.RecentlyPlayed
                .Concat(stats.MostPlayed)
                .Where(s => string.Equals(s.SongKey, recent.SongKey, StringComparison.Ordinal))
                .DistinctBy(s => s.ArrangementName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(s => s.BestAccuracy)
                .Select(s => new ArrangementItem(
                    s.HasBestAccuracy
                        ? L("Home_Arrangement_Best", s.ArrangementLabel, Percent(s.BestAccuracy))
                        : L("Home_Arrangement_NotPlayed", s.ArrangementLabel),
                    string.Equals(s.ArrangementName, recent.ArrangementName, StringComparison.OrdinalIgnoreCase)))
        ];
    }

    private IReadOnlyList<ProgressItem> JumpBackIn(ProfileStatistics stats) =>
    [
        .. stats.RecentlyPlayed
            .Where(s => s.MasteryPercent > 0)
            .DistinctBy(s => s.SongKey, StringComparer.OrdinalIgnoreCase)
            .Take(JumpBackInCap)
            .Select(s => new ProgressItem(s.DisplayName, Percent(s.MasteryPercent / 100.0), CoverFor(s.SongKey),
                s.SongKey, s.ArrangementName))
    ];

    private string CoverFor(string statsKey) => _library.FindCover(statsKey);

    private static string Percent(double fraction) => PercentText.Format(fraction, 0);

    private string Ago(DateTime pastUtc, DateTime nowUtc)
    {
        var age = RelativeTime.Between(pastUtc, nowUtc);
        return age.Unit switch
        {
            RelativeTimeUnit.JustNow => L("PlayMode_History_JustNow"),
            RelativeTimeUnit.Minutes => L("PlayMode_History_MinutesAgo", age.Value),
            RelativeTimeUnit.Hours => L("PlayMode_History_HoursAgo", age.Value),
            RelativeTimeUnit.Days => L("PlayMode_History_DaysAgo", age.Value),
            RelativeTimeUnit.Weeks => L("PlayMode_History_WeeksAgo", age.Value),
            RelativeTimeUnit.Months => L("PlayMode_History_MonthsAgo", age.Value),
            _ => L("PlayMode_History_YearsAgo", age.Value)
        };
    }

    private string L(string key) => _localization[key];

    private string L(string key, params object[] args) => _localization.Format(key, args);
}
