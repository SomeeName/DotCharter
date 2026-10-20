namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     How much the app knows about the song library right now. The launch
///     screen's first chip prints this state, so it must be able to say "I have
///     not looked yet" — claiming READY over an unscanned library is the exact
///     shape of stub data this screen refuses.
/// </summary>
public enum LaunchLibraryState
{
    /// <summary>No scan has run this session. The chip shows no count.</summary>
    NotScanned,

    /// <summary>A scan is running right now.</summary>
    Scanning,

    /// <summary>The scan finished and found nothing playable.</summary>
    Empty,

    /// <summary>The scan finished and the count is real.</summary>
    Ready
}

/// <summary>
///     Whether a guitar can be heard, which is the only honest source for the
///     launch screen's second chip. There is no "guitar" concept in the engine —
///     the real signal is whether an INPUT stream is open
///     (<c>IAudioPlaybackService.InputSampleRate</c>), so "connected" here means
///     exactly that and nothing more.
/// </summary>
public enum LaunchInputState
{
    /// <summary>No audio engine at all (headless construction, engine down).</summary>
    NoDevice,

    /// <summary>A device is open but its capture stream is not — nothing to
    /// judge a note from.</summary>
    NoInput,

    /// <summary>An input capture stream is open at a real sample rate.</summary>
    Connected
}

/// <summary>
///     Every value the Play Mode LAUNCH screen prints, behind ONE interface, so
///     the screen binds a snapshot instead of eight scattered properties.
///
///     <para>
///         <b>Nothing here is a placeholder.</b> Each member names the service it
///         is computed from, and every figure carries the STATE that produced it
///         rather than a number that would look plausible either way — the
///         library chip can say "not scanned" and the input chip can say "no
///         audio device". A screen that
///         cannot express "I do not know" is a screen that lies on a fresh
///         install.
///     </para>
///     <para>
///         The mockup's <c>LEVEL 14</c> is deliberately absent: this app has no
///         XP, level or rank concept anywhere (<c>PlayerProfile</c> carries
///         id/name/accent/avatar/lefty and nothing else), so there is nothing to
///         bind and nothing was invented. See
///         <c>docs/agents/ui/fire-design-system.md</c>.
///     </para>
/// </summary>
public interface ILaunchFacts
{
    /// <summary>Playable songs the library scan found, already formatted.
    /// Meaningless unless <see cref="LibraryState" /> is
    /// <see cref="LaunchLibraryState.Ready" />.</summary>
    string LibraryCount { get; }

    /// <summary>What the app actually knows about the library right now.</summary>
    LaunchLibraryState LibraryState { get; }

    /// <summary>True only in <see cref="LaunchLibraryState.Ready" /> — the count
    /// is shown for no other state.</summary>
    bool HasLibraryCount { get; }

    /// <summary>Localized caption for <see cref="LibraryState" /> (READY /
    /// SCANNING… / NOT SCANNED YET / NO SONGS FOUND).</summary>
    string LibraryStateLabel { get; }

    /// <summary>The library chip is in its good (green) state.</summary>
    bool IsLibraryReady { get; }

    /// <summary>The library chip is in its needs-attention (amber) state —
    /// scanning, unscanned, or scanned-and-empty.</summary>
    bool IsLibraryPending { get; }

    /// <summary>Whether an input capture stream is open
    /// (<c>IAudioPlaybackService.InputSampleRate &gt; 0</c>).</summary>
    LaunchInputState InputState { get; }

    /// <summary>"Guitar connected" / "No audio input" / "No audio device".</summary>
    string InputLabel { get; }

    /// <summary>Green dot: an input stream really is open.</summary>
    bool IsInputConnected { get; }

    /// <summary>The measured round-trip figure
    /// (<c>IAudioPlaybackService.MeasuredOutputLatencyMs</c>, falling back to the
    /// driver's estimate), already formatted as "N ms". EMPTY when no engine is
    /// up — never a fabricated number.</summary>
    string LatencyLabel { get; }

    /// <inheritdoc cref="LatencyLabel" />
    bool HasLatency { get; }

    /// <summary>The running assembly's informational version, "v0.42.4".</summary>
    string AppVersion { get; }

    /// <inheritdoc cref="AppVersion" />
    bool HasAppVersion { get; }
}

/// <summary>
///     One immutable launch-screen snapshot. The view model builds a new one per
///     refresh, so a half-filled snapshot can never be observed and every value
///     on screen comes from the same instant.
/// </summary>
public sealed class LaunchFacts : ILaunchFacts
{
    /// <summary>The all-unknown snapshot: what a freshly-constructed view model
    /// holds and what a headless construction with no services keeps. The screen
    /// renders its full chrome with every figure in its "I do not know" state,
    /// which is also what the very first frame of a cold boot shows.</summary>
    public static ILaunchFacts Empty { get; } = new LaunchFacts();

    public string LibraryCount { get; init; } = "";
    public LaunchLibraryState LibraryState { get; init; } = LaunchLibraryState.NotScanned;
    public bool HasLibraryCount => LibraryState == LaunchLibraryState.Ready;
    public string LibraryStateLabel { get; init; } = "";
    public bool IsLibraryReady => LibraryState == LaunchLibraryState.Ready;
    public bool IsLibraryPending => LibraryState != LaunchLibraryState.Ready;

    public LaunchInputState InputState { get; init; } = LaunchInputState.NoDevice;
    public string InputLabel { get; init; } = "";
    public bool IsInputConnected => InputState == LaunchInputState.Connected;

    public string LatencyLabel { get; init; } = "";
    public bool HasLatency => LatencyLabel.Length > 0;

    public string AppVersion { get; init; } = "";
    public bool HasAppVersion => AppVersion.Length > 0;

    /// <summary>The library state a (count, scanning) pair means. Pure so the
    /// tri-state can be asserted without a library, an engine or a UI thread —
    /// "scanning" beats a stale count, and a finished scan that found nothing is
    /// its own state rather than a silent zero.</summary>
    public static LaunchLibraryState StateFor(int songCount, bool scanning, bool scanCompleted)
    {
        if (scanning) return LaunchLibraryState.Scanning;
        if (songCount > 0) return LaunchLibraryState.Ready;
        return scanCompleted ? LaunchLibraryState.Empty : LaunchLibraryState.NotScanned;
    }

    /// <summary>The input state a sample rate means. Pure for the same reason:
    /// zero is "nothing is capturing", which is NOT the same claim as "the
    /// guitar is unplugged", and neither may be printed as a latency.</summary>
    public static LaunchInputState InputStateFor(bool hasEngine, int inputSampleRate) =>
        !hasEngine ? LaunchInputState.NoDevice
        : inputSampleRate > 0 ? LaunchInputState.Connected
        : LaunchInputState.NoInput;
}
