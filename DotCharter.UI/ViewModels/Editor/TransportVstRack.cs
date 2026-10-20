using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The transport's guitar VST3 chain behind <see cref="IVstRack" />, so the
///     SHARED plugin rack (<c>Views/PlayMode/VstRackView</c> in the rig assembly)
///     draws it in the Tools popover and the Amp/FX panel.
/// </summary>
/// <remarks>
///     <para>
///         <b>An adapter, not a second chain.</b> Every verb forwards to the
///         transport, which keeps what it always owned: the slots and their seat
///         routing, persistence to <c>AppConfig</c>, the whole-chain engine push,
///         the restore. Rewriting that logic behind a new type is how the
///         per-seat carry (<c>VstChainSlotViewModel.Player</c>) got dropped once
///         before.
///     </para>
///     <para>
///         Two things are translated rather than forwarded. The transport's
///         property names are its own (<c>AddGuitarVstSelection</c>,
///         <c>IsRescanningVstPlugins</c>), so their change notifications are
///         re-raised under the rack's names — a compiled binding listens for the
///         name it bound. And the add list drops the "None" sentinel the
///         transport keeps at the head of <c>AvailableVstPlugins</c> for the
///         INSTRUMENT picker: an "Add VST3…" list offering "None" is a row that
///         does nothing.
///     </para>
/// </remarks>
public sealed class TransportVstRack : ObservableObject, IVstRack
{
    private readonly TransportViewModel _transport;

    public TransportVstRack(TransportViewModel transport)
    {
        _transport = transport;
        // The transport and this adapter live and die together (the transport
        // owns it), so neither subscription outlives its publisher.
        _transport.PropertyChanged += OnTransportPropertyChanged;
        _transport.AvailableVstPlugins.CollectionChanged += OnTransportPluginsChanged;
        SyncPlugins();
    }

    public ObservableCollection<VstChainSlotViewModel> Chain => _transport.GuitarVstChain;

    public ObservableCollection<VstPluginInfo> AvailablePlugins { get; } = [];

    public VstPluginInfo? AddSelection
    {
        get => _transport.AddGuitarVstSelection;
        set => _transport.AddGuitarVstSelection = value;
    }

    public bool IsScanning => _transport.IsRescanningVstPlugins;

    public ICommand RescanCommand => _transport.RescanVstPluginsCommand;

    public bool MoveSlot(int fromIndex, int toIndex) => _transport.MoveGuitarSlot(fromIndex, toIndex);

    private void OnTransportPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TransportViewModel.AddGuitarVstSelection):
                OnPropertyChanged(nameof(AddSelection));
                break;
            case nameof(TransportViewModel.IsRescanningVstPlugins):
                OnPropertyChanged(nameof(IsScanning));
                break;
        }
    }

    private void OnTransportPluginsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncPlugins();

    /// <summary>Rebuilds the add list from the transport's. The SAME
    /// <see cref="VstPluginInfo" /> instances, so a pick writes back an item the
    /// transport recognises.</summary>
    private void SyncPlugins()
    {
        AvailablePlugins.Clear();
        foreach (var plugin in _transport.AvailableVstPlugins)
        {
            if (!string.IsNullOrEmpty(plugin.PluginPath))
                AvailablePlugins.Add(plugin);
        }
    }
}
