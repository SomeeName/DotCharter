namespace DotCharter.Presentation.Screens.Home;

/// <summary>
///     Everything the Home screen shows, already formatted, with no commands in
///     it. <c>HomeSnapshotSource</c> builds it and <c>HomeViewModel</c> projects
///     it onto the contract, which keeps the data gathering testable without a
///     view model at all.
/// </summary>
public sealed record HomeSnapshot(
    HomeHeader Header,
    HomeHero Hero,
    HomeProject RecentProject,
    IReadOnlyList<ProgressItem> JumpBackIn,
    int LibrarySongCount,
    bool AnimateHero = false,
    string LoadedChart = "")
{
    public static HomeSnapshot Empty { get; } = new(
        HomeHeader.Empty, HomeHero.Empty, HomeProject.Empty, [], 0);

    /// <summary>Value equality including the two lists, which a record compares by reference.</summary>
    public bool SameAs(HomeSnapshot other) =>
        WithoutLists() == other.WithoutLists()
        && JumpBackIn.SequenceEqual(other.JumpBackIn)
        && Hero.Arrangements.SequenceEqual(other.Hero.Arrangements);

    private HomeSnapshot WithoutLists() => this with { JumpBackIn = [], Hero = Hero with { Arrangements = [] } };
}

/// <summary>The part of a <see cref="HomeSnapshot" /> read on the UI thread.</summary>
public sealed record HomeLiveFacts(
    string ProfileSummary,
    string DeviceSummary,
    bool DeviceNeedsAttention,
    HomeProject RecentProject,
    bool AnimateHero,
    string LoadedChart);

/// <summary>The line across the top: the library, the player, the device.</summary>
public sealed record HomeHeader(
    string LibrarySummary, string ProfileSummary, string DeviceSummary, bool DeviceNeedsAttention)
{
    public static HomeHeader Empty { get; } = new("", "", "", false);
}

/// <summary>The song the player last played, already captioned.</summary>
public sealed record HomeHero(
    bool HasSong,
    string When,
    string Title,
    string Meta,
    IReadOnlyList<ArrangementItem> Arrangements,
    string PlayCaption,
    string ArtPath = "")
{
    public string SongKey { get; init; } = "";
    public string ArrangementName { get; init; } = "";
    public static HomeHero Empty { get; } = new(false, "", "", "", [], "");
}

/// <summary>The most recently opened project — the chart half of "resume".</summary>
public sealed record HomeProject(string Path, string Name)
{
    public static HomeProject Empty { get; } = new("", "");

    public bool Exists => Path.Length > 0;
}

public sealed record ArrangementItem(string Label, bool IsBest);

public sealed record ProgressItem(string Title, string ProgressLabel, string ArtPath = "",
    string SongKey = "", string ArrangementName = "");

public enum ShellTarget
{
    Library,
    Chart,
    Play,
    Settings
}
