// PlayModeViewModel: the Launch screen (the boot surface) - its ILaunchFacts snapshot, the deferred first refresh and the player-profiles panel.
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The LAUNCH screen — the app's very first surface with
///     <c>AppConfig.StartInPlayMode</c> (the default), because
///     <c>CurrentScreen</c> starts at <see cref="PlayModeScreen.Start" />.
///
///     <para>
///         Seventh partial of <see cref="PlayModeViewModel" />; the field rule is
///         in <c>PlayModeViewModel.cs</c>.
///     </para>
///     <para>
///         <b>Every value on this screen is real</b> and travels as ONE immutable
///         <see cref="ILaunchFacts" /> snapshot. The chips each carry a
///         genuine "I do not know" state, so a fresh install shows NOT SCANNED /
///         NO AUDIO DEVICE rather than a plausible-looking number.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    // ---- The launch screen's figures ----------------------------------------

    /// <summary>Every value the launch screen prints, as ONE immutable snapshot.
    /// Starts all-unknown so the app's FIRST FRAME renders the chrome with no
    /// figures; <see cref="RefreshLaunchScreen" /> swaps in the live one after
    /// the first paint.</summary>
    [ObservableProperty] private ILaunchFacts _launch = LaunchFacts.Empty;

    /// <summary>A library scan has RUN this session (finished, whatever it
    /// found). Without it "0 songs" and "never looked" are indistinguishable, and
    /// the chip would claim an empty library on a machine that simply has not
    /// scanned yet.</summary>
    private bool _launchLibraryScanCompleted;

    /// <summary>The deferred first refresh has been scheduled. The launch screen
    /// is the BOOT surface, so its work must run once, after the first frame —
    /// never again on every re-entry from the Menu's Esc.</summary>
    private bool _launchWarmupKicked;

    /// <summary>The PLAYER PROFILES panel is expanded under the "playing as"
    /// row.</summary>
    [ObservableProperty] private bool _isLaunchProfilesOpen;

    /// <summary>Neither panel is open — the segmented pair reads as "nothing
    /// picked", which is what draws the un-tinted state of both items.</summary>
    public bool IsLaunchPanelClosed => !IsLaunchProfilesOpen && !IsGuestEntryOpen;

    partial void OnIsLaunchProfilesOpenChanged(bool value)
    {
        // The pair is mutually exclusive: two open panels would stack two
        // different answers to one question under the same row.
        if (value) IsGuestEntryOpen = false;
        OnPropertyChanged(nameof(IsLaunchPanelClosed));
    }

    partial void OnIsGuestEntryOpenChanged(bool value)
    {
        if (value) IsLaunchProfilesOpen = false;
        OnPropertyChanged(nameof(IsLaunchPanelClosed));
    }

    /// <summary>Expands / collapses the PLAYER PROFILES panel.</summary>
    [RelayCommand]
    private void ToggleLaunchProfiles() => IsLaunchProfilesOpen = !IsLaunchProfilesOpen;

    /// <summary>Closes whichever launch panel is open. Esc reaches this through
    /// <c>TryCloseOverlay</c> BEFORE the shell's quit branch, which is also what
    /// makes "Esc quits" safe while the player is typing a new profile name —
    /// that field only exists inside the open panel.</summary>
    internal bool TryCloseLaunchPanel()
    {
        if (!IsStartScreen) return false;
        if (IsLaunchProfilesOpen) { IsLaunchProfilesOpen = false; return true; }
        if (IsGuestEntryOpen) { IsGuestEntryOpen = false; return true; }
        return false;
    }

    /// <summary>
    ///     Entry to the launch screen, from either of the two ways it is reached:
    ///     the view attaching on a cold boot (<c>CurrentScreen</c> STARTS at
    ///     <see cref="PlayModeScreen.Start" />, so no screen change ever fires
    ///     for it) and Esc walking back from the Menu.
    ///     <para>
    ///         The FIRST entry defers everything to
    ///         <see cref="DispatcherPriority.Background" /> — it lands on the
    ///         pre-first-paint path and nothing here may sit in front of the
    ///         first frame. Later entries refresh inline: the snapshot is already
    ///         warm and every read is in memory or a small local store.
    ///     </para>
    /// </summary>
    internal void EnterLaunchScreen()
    {
        if (_launchWarmupKicked)
        {
            RefreshLaunchScreen();
            return;
        }

        _launchWarmupKicked = true;
        Dispatcher.UIThread.Post(() => _ = WarmLaunchScreenAsync(), DispatcherPriority.Background);
    }

    /// <summary>
    ///     The launch screen's deferred half: the library scan behind the song
    ///     count. Started AFTER
    ///     the first frame at Background priority for the same reason the Menu's
    ///     plan card defers its own scan — the scan is seconds long and marshals
    ///     a progress callback per batch onto the UI thread. The chip shows
    ///     SCANNING while it runs and the real count when it lands, so the state
    ///     on screen is never ahead of the work.
    /// </summary>
    private async Task WarmLaunchScreenAsync()
    {
        // Everything that does NOT need the scan lands first: version, input
        // device, latency.
        RefreshLaunchScreen();
        try
        {
            await SongSelect.EnsureLibrary();
        }
        catch (Exception ex)
        {
            // The launch screen is the app's boot surface — a library that
            // cannot be scanned must leave it standing, chip and all.
            _logger.Warning(AppLogCategory.Application, $"Launch-screen library warm-up failed: {ex.Message}");
        }
        finally
        {
            _launchLibraryScanCompleted = true;
            RefreshLaunchScreen();
        }
    }

    /// <summary>Rebuilds <see cref="Launch" />. Every read here is in-memory or a
    /// small local store; the scan that FILLS the library is deferred by
    /// <see cref="WarmLaunchScreenAsync" />.</summary>
    private void RefreshLaunchScreen()
    {
        var loc = _localization;

        // ---- Library: count + the state that produced it --------------------
        var libraryState = LaunchFacts.StateFor(SongSelect.AllSongs.Count, SongSelect.IsLibraryLoading, _launchLibraryScanCompleted);
        var libraryLabel = libraryState switch
        {
            LaunchLibraryState.Ready => loc["PlayMode_Launch_LibraryReady"],
            LaunchLibraryState.Scanning => loc["PlayMode_Launch_LibraryScanning"],
            LaunchLibraryState.Empty => loc["PlayMode_Launch_LibraryEmpty"],
            _ => loc["PlayMode_Launch_LibraryPending"]
        };

        // ---- Input: an OPEN capture stream, and the measured latency --------
        var inputState = LaunchFacts.InputStateFor(hasEngine: true, _audio.InputSampleRate);
        var inputLabel = inputState switch
        {
            LaunchInputState.Connected => loc["PlayMode_Launch_InputConnected"],
            LaunchInputState.NoInput => loc["PlayMode_Launch_InputNone"],
            _ => loc["PlayMode_Launch_NoDevice"]
        };

        // The calibrated round trip when the player has measured one, else the
        // driver's own estimate. With no engine there is no number at all —
        // the chip prints the state instead of a plausible "12 ms".
        var latency = "";
        // The same figure and format Home prints (OutputLatency).
        var ms = DotCharter.Tone.Audio.Diagnostics.OutputLatency.Ms(_audio);
        if (ms > 0) latency = loc.Format("PlayMode_Menu_LatencyFmt", ms.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));

        Launch = new LaunchFacts
        {
            // The LIBRARY's count, as Home and the Library print it: own charts are
            // playable here but are not library songs.
            LibraryCount = loc.Plural("PlayMode_Launch_SongOne", "PlayMode_Launch_SongsFmt",
                SongSelect.AllSongs.Count(s => !s.IsOwnChart)),
            LibraryState = libraryState,
            LibraryStateLabel = libraryLabel,
            InputState = inputState,
            InputLabel = inputLabel,
            LatencyLabel = latency,
            AppVersion = FrontDoorFacts.ResolveAppVersion()
        };
    }
}
