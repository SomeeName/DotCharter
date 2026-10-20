// PlayModeViewModel: the overlay surfaces (Amp & FX, pedalboard) and TryCloseOverlay - the one Esc walk-up that decides which overlay closes first.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The OVERLAY surfaces layered over a screen rather than replacing it - the
///     Amp &amp; FX board and the pedalboard - and <c>TryCloseOverlay</c>, the
///     single Esc walk-up that decides which of the open overlays closes first.
///     The pedalboard's own state (its open flag, the chain, the rig screen) is
///     <see cref="PlayModeToneRigViewModel" />, reached through <c>ToneRig</c>.
///     <para>
///         Part of <see cref="PlayModeViewModel" />. Injected dependencies and
///         plain private fields live in the ROOT file; see
///         <c>PlayModeViewModel.cs</c> for the full field rule and
///         why the split exists.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    // ---- Amp / FX overlay --------------------------------------------------------

    // Play Mode gets its OWN centered, scrimmed Amp/FX overlay instead of the
    // editor toolbar's Flyout: a Flyout anchored to a menu button pops out of
    // the button and is clipped by the window edge (it was designed for the
    // toolbar, where it opens downward into the chart). The overlay is
    // Views/PlayMode/AmpFxOverlayView around Views/Shell/AmpFxPanel, whose VST3
    // chain is the shared plugin rack (VstRackView) the Tools popover and
    // RigLab's Plugins tab draw too.
    [ObservableProperty] private bool _isAmpFxOpen;

    [RelayCommand]
    private void OpenAmpFx() => IsAmpFxOpen = true;

    [RelayCommand]
    private void CloseAmpFx() => IsAmpFxOpen = false;

    /// <summary>Closes the topmost open Play Mode overlay (planner, gameplay
    /// settings, pedalboard, Amp/FX, lobby), if any. Lets Esc dismiss an overlay
    /// before it falls through to pause/resume or back-navigation.
    /// Returns true when it closed one.</summary>
    public bool TryCloseOverlay()
    {
        // The launch screen's two inline panels (PLAYER PROFILES / GUEST) are
        // dismissible surfaces like any overlay, and they must be dismissed
        // FIRST: Esc on that screen QUITS the app, and the new-profile / guest
        // name fields only exist inside these panels — so closing the panel is
        // what stands between "Esc while typing a name" and the quit prompt.
        if (TryCloseLaunchPanel()) return true;
        if (Settings.IsGameplaySettingsOpen) { Settings.IsGameplaySettingsOpen = false; return true; }
        if (ToneRig.IsPedalboardOpen && ToneRig.Pedalboard?.Browser.IsDeleteConfirmOpen == true)
        {
            // The tone-delete confirmation is the other modal on the rig: Esc
            // keeps the tone and closes it, like its Cancel.
            ToneRig.Pedalboard.Browser.CancelDelete();
            return true;
        }
        if (ToneRig.IsPedalboardOpen && ToneRig.Pedalboard?.Browser.IsSaveDialogOpen == true)
        {
            // The "save as new" modal sits ON TOP of the pedalboard — Esc closes
            // that first; only a second Esc dismisses the whole overlay.
            ToneRig.Pedalboard.Browser.IsSaveDialogOpen = false;
            return true;
        }
        if (ToneRig.IsPedalboardOpen && ToneRig.Pedalboard?.IsBrowserOpen == true)
        {
            // …and the TONE BROWSER drawer is the layer between them: it opens
            // over the board, and the save modal opens over IT. Esc walks the
            // three down in the order they were stacked.
            ToneRig.Pedalboard.IsBrowserOpen = false;
            return true;
        }
        if (ToneRig.IsPedalboardOpen) { ToneRig.IsPedalboardOpen = false; return true; }
        if (IsAmpFxOpen) { IsAmpFxOpen = false; return true; }
        // A count-in is a committed action you can still take back — Esc during
        // it abandons the run before a note is judged, which is what the
        // mockup's "ESC CANCEL" hint promises.
        if (IsCountdownScreen) { CancelCountdown(); return true; }
        // The online hub is not listed here. It is a TAB of the Network
        // screen, so Esc falls through to `NavigateBack`, which sends
        // every Network tab to the Menu — the same answer, one hop later.
        return false;
    }
}
