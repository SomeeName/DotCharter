using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Tone.Audio.Vst;

namespace DotCharter.Tone.UI.ViewModels.Editor;

/// <summary>
///     The guitar VST3 chain as the shared plugin rack
///     (<c>Views/PlayMode/VstRackView</c>) sees it — the ONE seam both apps put
///     their chain behind.
///     <para>
///         DotCharter implements it with <c>TransportVstRack</c>, a thin adapter
///         over the transport's existing chain (seats, persistence, the engine
///         push all stay where they were); RigLab with its <c>VstRackViewModel</c>.
///         The rack view, its drag reorder (<c>VstChainDragController</c>) and
///         the add picker are therefore one piece of markup in both apps.
///     </para>
///     <para>
///         Every per-slot verb (bypass, GUI, seat, remove) is already on
///         <see cref="VstChainSlotViewModel" />; this carries only what belongs
///         to the CHAIN: its rows, what can be added, and the reorder.
///     </para>
/// </summary>
public interface IVstRack : INotifyPropertyChanged
{
    /// <summary>The slots, in signal order. The rack binds the collection itself,
    /// so a host's own mutations show without a refresh.</summary>
    ObservableCollection<VstChainSlotViewModel> Chain { get; }

    /// <summary>What the add picker offers: real plugins only, never a "None"
    /// sentinel.</summary>
    ObservableCollection<VstPluginInfo> AvailablePlugins { get; }

    /// <summary>Writing a plugin here APPENDS it to the chain (picking is the
    /// gesture); the host writes it back to null.</summary>
    VstPluginInfo? AddSelection { get; set; }

    /// <summary>True while the plugin folders are being scanned.</summary>
    bool IsScanning { get; }

    /// <summary>Re-walks the plugin folders.</summary>
    ICommand RescanCommand { get; }

    /// <summary>Moves one slot to an arbitrary position (what a drag produces),
    /// persists and re-pushes the chain. False — touching nothing — for an
    /// out-of-range or no-op move.</summary>
    bool MoveSlot(int fromIndex, int toIndex);
}
