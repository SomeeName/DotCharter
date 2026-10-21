using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.ViewModels.Editor;

/// <summary>
/// View model for one slot in the guitar VST3 effect chain. Mutations to
/// <see cref="Bypass"/>, <see cref="EditorOpen"/> and the move/remove commands
/// notify the parent <c>TransportViewModel</c> through the action
/// callbacks so it can update the audio engine + persist AppConfig.
/// </summary>
public partial class VstChainSlotViewModel : ObservableObject
{
    [ObservableProperty] private bool _bypass;
    [ObservableProperty] private bool _editorOpen;
    // True while the native engine is still loading this plugin. The chain
    // slot becomes interactive (open-editor / bypass) once the engine
    // reports the slot is attached.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isLoading = true;

    /// <summary>
    ///     True for a slot the saved chain names but the startup scan did not find in
    ///     the VST3 folders: it is kept in the chain (the entry is the user's choice and
    ///     comes back when the plug-in is installed) but never handed to the engine.
    ///     A chain entry is only ever loaded from a path the scanner reported, because
    ///     the path in a settings file is not something the app may run on trust.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isUnavailable;

    /// <summary>What the row prints beside the name while <see cref="IsUnavailable" />.</summary>
    [ObservableProperty] private string _unavailableLabel = string.Empty;

    /// <summary>The longer explanation (tooltip) for <see cref="IsUnavailable" />.</summary>
    [ObservableProperty] private string _unavailableReason = string.Empty;

    /// <summary>Whether bypass / editor can be used: the engine has the plug-in loaded.
    /// Remove stays available either way.</summary>
    public bool CanInteract => !IsLoading && !IsUnavailable;

    /// <summary>Marks the slot as not loadable and says why. Idempotent.</summary>
    public void MarkUnavailable(string label, string reason)
    {
        UnavailableLabel = label;
        UnavailableReason = reason;
        IsLoading = false;
        IsUnavailable = true;
    }

    /// <summary>
    ///     The 1-based slot number the Tools popover prints — `Play Menus.dc.html`
    ///     numbers the chain, because "signal runs top to bottom" is only
    ///     legible if the order is stated.
    /// </summary>
    /// <remarks>
    ///     Held rather than derived: an <c>ItemsControl</c> item has no index,
    ///     and binding one through <c>$parent</c> would not update when the
    ///     collection is REORDERED (a <c>Move</c> raises no property change on
    ///     the items). <c>TransportViewModel.RenumberGuitarChain</c> is the one
    ///     writer and runs after every add, remove and move.
    /// </remarks>
    [ObservableProperty] private int _displayIndex;

    public VstChainSlotViewModel(Guid id, string pluginName, string pluginPath, bool bypass, int player = 0)
    {
        Id = id;
        PluginName = pluginName;
        PluginPath = pluginPath;
        Player = player;
        _bypass = bypass;
    }

    public Guid Id { get; }
    public string PluginName { get; }
    public string PluginPath { get; }

    /// <summary>
    ///     Which SEAT's input chain this slot processes (ABI v28); 0 is the
    ///     single-player chain and everything about seat 0 is byte-for-byte what
    ///     it always was.
    ///     <para>
    ///         <b>Authored</b> — through <see cref="SelectedSeat" />,
    ///         which the row's seat picker binds to. Merely CARRYING the seat would not be
    ///         enough: the service layer takes a player and
    ///         persists it, the engine has
    ///         <c>dca_plugin_attach_to_input_for_player</c>, and without a surface that could
    ///         say which seat a plugin belongs to every slot a player added
    ///         would go on seat 0 and player 2 could not put a plugin on their own
    ///         rig at all.
    ///     </para>
    ///     <para>
    ///         Carrying it is still load-bearing on its own: without it every
    ///         load → persist → push cycle rewrote the seat to 0, which is the
    ///         failure <c>JuceAudioPlaybackService.SetGuitarVstChain</c> warns
    ///         about in its own snapshot ("dropping it here would silently
    ///         re-seat every per-player slot onto player 1 the moment anything
    ///         re-applied the chain").
    ///     </para>
    /// </summary>
    public int Player { get; private set; }

    /// <summary>
    ///     The seats this slot can be put on — the SAME list the Amp &amp; FX
    ///     rig's own picker offers, pushed in by
    ///     <c>TransportViewModel.SetVstSeats</c>.
    ///     <para>
    ///         One shared collection instance for every slot, deliberately: a
    ///         second seat model would be a second answer to "which seats exist",
    ///         and the two would disagree the first time a roster changed.
    ///     </para>
    /// </summary>
    public ObservableCollection<RigSeatOption> Seats { get; private set; } =
        [new RigSeatOption(0, "Player 1")];

    /// <summary>Whether to offer the picker at all. One seat is not a choice, and
    /// a dropdown over it is a live-looking control over nothing.</summary>
    public bool HasMultipleSeats => Seats.Count > 1;

    /// <summary>The seat this slot processes. Two-way bound by the row's picker;
    /// writing it moves <see cref="Player" /> and raises
    /// <see cref="PlayerChanged" /> so the parent re-pushes AND re-persists the
    /// chain — the engine reseat is what actually moves the plugin.</summary>
    [ObservableProperty] private RigSeatOption? _selectedSeat;

    public Action<VstChainSlotViewModel, bool>? BypassChanged { get; set; }
    public Action<VstChainSlotViewModel, bool>? EditorChanged { get; set; }
    public Action<VstChainSlotViewModel>? RemoveRequested { get; set; }

    /// <summary>Raised when the player MOVED this slot to another seat. Never
    /// raised by <see cref="AttachSeats" /> — a roster arriving is not an edit,
    /// and echoing one would push the whole chain at the engine on every
    /// seat publish.</summary>
    public Action<VstChainSlotViewModel, int>? PlayerChanged { get; set; }

    private bool _suppressPlayerChanged;

    /// <summary>
    ///     Point this slot's picker at the seats that were published, and
    ///     re-select the one it is already on.
    ///     <para>
    ///         A slot whose seat has left the list KEEPS its
    ///         <see cref="Player" /> rather than being silently moved to seat 0:
    ///         the run that owns those seats is over, the persisted chain still
    ///         says player 2, and rewriting it here would destroy that on the
    ///         next save — exactly the bug carrying the seat exists to prevent.
    ///     </para>
    /// </summary>
    public void AttachSeats(ObservableCollection<RigSeatOption> seats)
    {
        Seats = seats;
        OnPropertyChanged(nameof(Seats));
        OnPropertyChanged(nameof(HasMultipleSeats));

        var match = seats.FirstOrDefault(s => s.Player == Player);
        _suppressPlayerChanged = true;
        try { SelectedSeat = match; }
        finally { _suppressPlayerChanged = false; }
    }

    partial void OnSelectedSeatChanged(RigSeatOption? value)
    {
        if (value is null) return; // the seat list does not offer this slot's seat
        if (value.Player == Player) return;

        Player = value.Player;
        if (!_suppressPlayerChanged) PlayerChanged?.Invoke(this, value.Player);
    }

    // MoveUp/MoveDown are GONE (0.54). The mockup gives a slot a drag GRIP and
    // says "Drag to reorder", and the two arrow buttons cost the row 52 px of
    // its 400 — which is why it had no room for the index and the state pill.
    // The reorder verb is `TransportViewModel.MoveGuitarSlot(from, to)`, driven
    // by `Views/Shell/Popovers/VstChainDragController`, and it takes an
    // ARBITRARY destination where the arrows could only ever do ±1.

    [RelayCommand] private void Remove() => RemoveRequested?.Invoke(this);

    partial void OnBypassChanged(bool value) => BypassChanged?.Invoke(this, value);

    partial void OnEditorOpenChanged(bool value)
    {
        if (!_suppressEditorChanged) EditorChanged?.Invoke(this, value);
    }

    private bool _suppressEditorChanged;

    /// <summary>
    /// Sync <see cref="EditorOpen"/> to a state change the audio engine
    /// already performed (e.g. the user closed the editor window with its
    /// native X button) without echoing a redundant toggle back to it.
    /// </summary>
    public void SetEditorOpenFromEngine(bool value)
    {
        _suppressEditorChanged = true;
        try { EditorOpen = value; }
        finally { _suppressEditorChanged = false; }
    }
}
