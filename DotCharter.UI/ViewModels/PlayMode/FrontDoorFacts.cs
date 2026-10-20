
namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>The last practice run, as the front door's floor strip prints it:
/// three pre-formatted figures over the newest stored
/// <c>SessionHistoryItem</c>.</summary>
public sealed record FrontDoorLastSession(string Minutes, string Accuracy, string Points);

/// <summary>
///     Every number the Play Mode front door shows, behind ONE interface so the
///     screen binds to a snapshot rather than to eight scattered properties.
///
///     <para>
///         <b>Nothing here is a placeholder.</b> Each member names the service
///         it is computed from; a figure that has no source is not shown at all
///         (each one carries a <c>Has*</c> companion) rather than being filled
///         with a plausible number. That is the repo's standing rule — a number
///         nothing computes is a lie that survives review.
///     </para>
/// </summary>
public interface IFrontDoorFacts
{
    /// <summary><c>SongTotalCountText</c> — the scanned library size.</summary>
    string LibraryCount { get; }

    /// <summary>False while the scan has found nothing — the front door's
    /// library button then carries its label alone. A bare "0" beside the
    /// label says "your library is empty" when the truth is "the scan has not
    /// run yet", which is the same lie the launch screen's library chip is
    /// built to avoid.</summary>
    bool HasLibraryCount { get; }

    /// <summary>The running assembly's informational version.</summary>
    string AppVersion { get; }

    // The footer carries no latency label: a bare "10 ms" reads
    // as a debug leftover. The latency is shown WITH context on the launch
    // screen's input chip (LaunchFacts.LatencyLabel) and in Session Health.

    /// <summary>
    ///     What the guitar is tuned to: the tuning the player last CONFIRMED on
    ///     the pre-play tuning check (<c>AppConfig.LastVerifiedTuning</c>),
    ///     resolved to its common-tuning name. This is the same value the
    ///     pre-play screen already trusts to skip the check for a same-tuning
    ///     song, so the front door and the library's tuning line agree with it
    ///     by construction.
    /// </summary>
    string TuningLabel { get; }

    /// <summary>False until the player has verified a tuning once — the footer
    /// then prints the version alone rather than an empty slot.</summary>
    bool HasTuning { get; }

    /// <summary>How many of the player's OWN charts the library holds — the
    /// same own-chart set <c>RebuildFromBrowser</c> assembles from the practice
    /// store and <c>AppConfig.LastProjectPath</c>. The mockup's "2 drafts, 1
    /// ready" needs a draft/ready state that does not exist; the honest figure
    /// on that row is how many charts of your own are in there.</summary>
    string OwnChartCount { get; }

    /// <inheritdoc cref="OwnChartCount" />
    bool HasOwnCharts { get; }

    /// <summary>The newest stored run across the whole session history, or null
    /// on a profile that has never played.</summary>
    FrontDoorLastSession? LastSession { get; }

    /// <inheritdoc cref="LastSession" />
    bool HasLastSession { get; }
}

/// <summary>
///     One immutable front-door snapshot. The view model builds a new one per
///     refresh, so a half-filled snapshot can never be observed and every figure
///     on screen comes from the same instant.
/// </summary>
public sealed class FrontDoorFacts : IFrontDoorFacts
{
    /// <summary>An all-empty snapshot: the value a freshly-constructed view
    /// model holds, and what a headless construction with no services keeps.
    /// Every <c>Has*</c> is false, so the screen renders its chrome with no
    /// figures rather than with invented ones.</summary>
    public static IFrontDoorFacts Empty { get; } = new FrontDoorFacts();

    public string LibraryCount { get; init; } = "0";

    /// <summary>Any figure other than a literal zero counts. The value arrives
    /// already formatted (thousands separators and all), so this asks the only
    /// question the view needs: is there a number worth printing.</summary>
    public bool HasLibraryCount => LibraryCount.Length > 0 && LibraryCount.Trim() != "0";

    public string AppVersion { get; init; } = "";

    public string TuningLabel { get; init; } = "";
    public bool HasTuning => TuningLabel.Length > 0;

    public string OwnChartCount { get; init; } = "";
    public bool HasOwnCharts { get; init; }

    public FrontDoorLastSession? LastSession { get; init; }
    public bool HasLastSession => LastSession != null;

    /// <summary>The running app's release identity, prefixed for the Home footer.</summary>
    public static string ResolveAppVersion() => "v" + DotCharter.Services.Infrastructure.AppVersionInfo.Current;
}
