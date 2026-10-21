// TransportViewModel: the VST surface - the guitar FX chain's slots, the instrument VST and the engine events that mirror plugin state back.
using System.Collections.ObjectModel;
using DotCharter.Tone.UI.ViewModels.Editor;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.UI.ViewModels.Editor;

// VST surface of the transport: guitar FX chain (slot wiring, reorder,
// persist, engine sync), instrument VST selection/editor lifecycle, and the
// audio-service event handlers that mirror native plugin state back into the
// UI. Backing fields live in the root file.
public partial class TransportViewModel
{
    public ObservableCollection<VstChainSlotViewModel> GuitarVstChain { get; } = [];

    /// <summary>
    ///     This chain behind <see cref="IVstRack" /> — what the SHARED plugin rack
    ///     (<c>Views/PlayMode/VstRackView</c>, the same view RigLab's Plugins tab
    ///     draws) binds in the Tools popover and the Amp/FX panel. Created on
    ///     first use and kept, so both surfaces bind ONE adapter.
    /// </summary>
    public IVstRack VstRack => _vstRack ??= new TransportVstRack(this);

    private TransportVstRack? _vstRack;

    /// <summary>
    ///     The seats every VST slot's picker offers — ONE collection shared
    ///     by every row, and the same <c>RigSeatOption</c> model the Amp &amp; FX
    ///     rig's own seat picker uses.
    ///     <para>
    ///         Single-player holds exactly one entry, so <c>HasMultipleSeats</c>
    ///         is false and no row draws a picker: nothing about a solo chain
    ///         changed.
    ///     </para>
    /// </summary>
    public ObservableCollection<RigSeatOption> VstSeats { get; } =
        [new RigSeatOption(0, "Player 1")];

    /// <summary>
    ///     Replace the seats the VST rows offer, or collapse
    ///     back to single player with a null/one-entry list.
    ///     <para>
    ///         Meant to be pushed by the SAME call that points the Amp &amp; FX
    ///         rig at a seat, so the two surfaces cannot offer different seats.
    ///         No product code raises more than one seat (there is no couch co-op),
    ///         so the list stays single-seat. The transport is
    ///         a DI singleton with no session dependency, which is why this is a
    ///         push and not a pull.
    ///     </para>
    ///     <para>
    ///         The list is mutated in place rather than replaced: every slot holds
    ///         a reference to it, and swapping the instance would leave existing
    ///         rows bound to a dead collection.
    ///     </para>
    /// </summary>
    public void SetVstSeats(IReadOnlyList<RigSeatOption>? seats)
    {
        var wanted = (seats ?? [])
            .Where(s => s.Player >= 0)
            .OrderBy(s => s.Player)
            .ToList();
        if (wanted.Count == 0) wanted.Add(new RigSeatOption(0, "Player 1"));

        var keep = DropTunePlayer;

        VstSeats.Clear();
        foreach (var seat in wanted) VstSeats.Add(seat);

        foreach (var slot in GuitarVstChain) slot.AttachSeats(VstSeats);

        // The DROP TUNE picker rides the same list (ABI v31). Keep the seat
        // being edited selected across a roster edit when it survives — a
        // rename must not move somebody's pedal — and collapse to seat 0 when
        // the run that owned the extra seats is over.
        DropTuneSeat = VstSeats.FirstOrDefault(s => s.Player == keep) ?? VstSeats[0];
        OnPropertyChanged(nameof(HasMultipleDropTuneSeats));
    }

    private void OnAudioInstrumentVstLoadChanged(object? sender, bool loaded)
    {
        _ui.PostNormal(() =>
        {
            IsInstrumentVstReady = loaded;
            IsInstrumentVstLoading = _audioService.IsInstrumentVstLoading;
            // A load lands in-process or in the sandbox; the route toggle's
            // availability follows that mode.
            RefreshChartSynthAvailability();
        });
    }

    private void OnAudioGuitarVstSlotLoadChanged(object? sender, (Guid SlotId, bool Loaded) e)
    {
        _ui.PostNormal(() =>
        {
            foreach (var slot in GuitarVstChain)
            {
                if (slot.Id == e.SlotId)
                {
                    slot.IsLoading = !e.Loaded;
                    break;
                }
            }
        });
    }

    private void OnAudioGuitarVstSlotEditorClosed(object? sender, Guid slotId)
    {
        _ui.PostNormal(() =>
        {
            foreach (var slot in GuitarVstChain)
            {
                if (slot.Id == slotId)
                {
                    slot.SetEditorOpenFromEngine(false);
                    break;
                }
            }
        });
    }

    private void OnAudioInstrumentVstEditorClosed(object? sender, EventArgs e)
    {
        _ui.PostNormal(() =>
        {
            // The window is already gone; just sync the toggle without
            // echoing a redundant hide back to the engine.
            _suppressInstrumentEditorToggle = true;
            try { InstrumentVstGuiOpen = false; }
            finally { _suppressInstrumentEditorToggle = false; }
        });
    }

    /// <summary>
    /// Reads the persisted VST selections from the app config (running the
    /// legacy single-VST3 → chain migration if needed) so the scan worker can
    /// push them to the audio service before the UI-thread restore runs.
    /// Thread-safe: the config service caches in memory and the returned
    /// entries are fresh copies.
    /// </summary>
    private (List<VstChainEntry> Chain, string? InstrumentPath) LoadPersistedVstSelections()
    {
        var cfg = _configService.LoadAppConfig();

        // Migrate the legacy single-VST3 selection into a one-entry chain so
        // upgrading users keep their effect.
        if (cfg.GuitarVstChain.Count == 0 &&
            !string.IsNullOrEmpty(cfg.LastGuitarVstPath) &&
            cfg.LastGuitarVstPath.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
        {
            cfg.GuitarVstChain =
            [
                // Player 0 explicitly: the legacy single-VST3 selection predates
                // per-player chains, so it is the single-player chain by definition.
                new() { Id = Guid.NewGuid(), PluginPath = cfg.LastGuitarVstPath, Bypass = false, Player = 0 }
            ];
            cfg.LastGuitarVstPath = string.Empty;
            _configService.SaveAppConfig(cfg);
        }

        var chain = cfg.GuitarVstChain
            // Player travels with the slot on EVERY copy. Dropping it here
            // destroyed a persisted "Player": 1 on the first launch after it was
            // written, and the seat could never come back.
            .Select(e => new VstChainEntry
            { Id = e.Id, PluginPath = e.PluginPath, Bypass = e.Bypass, Player = e.Player })
            .ToList();

        // VST3-only host: restore only valid .vst3 paths (legacy .dll paths
        // are silently dropped). The previous build had this predicate
        // inverted, which meant a freshly-installed user with a saved
        // instrument VST3 never got it auto-loaded at startup — the plugin
        // would only attach after the user opened Settings and re-selected
        // it from the dropdown.
        var instrumentPath =
            !string.IsNullOrEmpty(cfg.LastInstrumentVstPath) &&
            cfg.LastInstrumentVstPath.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)
                ? cfg.LastInstrumentVstPath
                : null;

        return (chain, instrumentPath);
    }

    /// <summary>
    /// Mirrors the already-pushed VST selections into the UI-bound
    /// collections. The engine push happened on the scan worker (see the
    /// constructor); re-pushing here would be idempotent but would queue a
    /// redundant chain-apply behind the multi-second native plugin loads.
    /// </summary>
    private void RestoreLastVstSelections(
        List<VstChainEntry> chain, List<VstChainEntry> loadable, string? instrumentPath)
    {
        foreach (var entry in chain)
        {
            AddGuitarChainSlot(entry, persist: false, pushToEngine: false);
            if (!loadable.Contains(entry))
            {
                // Not in the scanner's list, so it was never pushed to the engine: show
                // the slot as not loaded, with the reason, instead of a spinner that
                // never ends or a control that looks live.
                GuitarVstChain[^1].MarkUnavailable(
                    _localization["Toolbar_FxChain_NotLoaded"], _localization["Toolbar_FxChain_NotLoadedTip"]);
                continue;
            }

            // The engine push raced ahead on the scan worker; if this slot
            // already finished loading, its GuitarVstSlotLoadChanged event
            // fired before the slot VM existed. Sync the flag directly so the
            // slot doesn't stay stuck in the disabled "loading" state.
            if (_audioService.IsGuitarVstSlotLoaded(entry.Id))
                GuitarVstChain[^1].IsLoading = false;
        }

        if (instrumentPath != null)
        {
            var match = InstrumentVstPlugins.FirstOrDefault(p =>
                string.Equals(p.PluginPath, instrumentPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) SelectedInstrumentVst = match;
        }
    }

    private void PersistLastVst(bool guitar, string path)
    {
        if (!_vstRestoreComplete) return;

        var cfg = _configService.LoadAppConfig();
        if (guitar) cfg.LastGuitarVstPath = path;
        else cfg.LastInstrumentVstPath = path;
        _configService.SaveAppConfig(cfg);
    }

    private void PersistGuitarChain()
    {
        if (!_vstRestoreComplete) return;
        var cfg = _configService.LoadAppConfig();
        cfg.GuitarVstChain = GuitarVstChain
            .Select(s => new VstChainEntry
            { Id = s.Id, PluginPath = s.PluginPath, Bypass = s.Bypass, Player = s.Player })
            .ToList();
        _configService.SaveAppConfig(cfg);
    }

    private void PushGuitarChainToEngine()
    {
        var snapshot = GuitarVstChain
            // A slot the scan did not find stays in the saved chain but never reaches the engine.
            .Where(s => !s.IsUnavailable)
            .Select(s => new VstChainEntry
            { Id = s.Id, PluginPath = s.PluginPath, Bypass = s.Bypass, Player = s.Player })
            .ToList();
        _audioService.SetGuitarVstChain(snapshot);
    }

    private string PluginDisplayName(string path)
    {
        var match = AvailableVstPlugins.FirstOrDefault(p =>
            string.Equals(p.PluginPath, path, StringComparison.OrdinalIgnoreCase));
        return match?.Name ?? Path.GetFileNameWithoutExtension(path);
    }

    private void AddGuitarChainSlot(VstChainEntry entry, bool persist, bool pushToEngine)
    {
        var slot = new VstChainSlotViewModel(entry.Id, PluginDisplayName(entry.PluginPath),
            entry.PluginPath, entry.Bypass, entry.Player);
        WireGuitarSlot(slot);
        GuitarVstChain.Add(slot);
        RenumberGuitarChain();
        if (persist) PersistGuitarChain();
        if (pushToEngine) PushGuitarChainToEngine();
    }

    /// <summary>
    ///     Restates each slot's printed 1-based number after the collection
    ///     changes. It has to be a WRITE: an <c>ObservableCollection.Move</c>
    ///     raises no property change on the items it moved, so a number derived
    ///     through <c>$parent</c> would keep the order the chain had before the
    ///     drag.
    /// </summary>
    private void RenumberGuitarChain()
    {
        for (var i = 0; i < GuitarVstChain.Count; i++)
            GuitarVstChain[i].DisplayIndex = i + 1;
    }

    private void WireGuitarSlot(VstChainSlotViewModel slot)
    {
        slot.RemoveRequested = RemoveGuitarSlot;
        slot.BypassChanged = (s, bypass) =>
        {
            _audioService.SetGuitarVstSlotBypass(s.Id, bypass);
            PersistGuitarChain();
        };
        slot.EditorChanged = (s, open) => _audioService.ToggleGuitarVstSlotEditor(s.Id, open);
        // Moving a slot to another SEAT is an engine reseat, not a UI flag:
        // ApplyGuitarVstChainCore detaches the plugin and re-attaches it to the
        // new player's input chain. Persist too — a seat that only lived until
        // the next launch is not an assignment.
        slot.PlayerChanged = (_, _) =>
        {
            PersistGuitarChain();
            PushGuitarChainToEngine();
        };
        slot.AttachSeats(VstSeats);
    }

    // MoveGuitarSlotUp/Down are gone (0.54) — see `MoveGuitarSlot(from, to)` in
    // TransportViewModel.Popover.cs, which the Tools popover's drag drives. It
    // is the same three statements generalized to an arbitrary destination.

    private void RemoveGuitarSlot(VstChainSlotViewModel slot)
    {
        if (!GuitarVstChain.Remove(slot)) return;
        RenumberGuitarChain();
        // Close the editor first so the native VST window goes away cleanly
        // before the engine tears down the slot below.
        try { slot.EditorOpen = false; } catch { /* best-effort */ }
        PersistGuitarChain();
        PushGuitarChainToEngine();
    }

    /// <summary>
    ///     Appends the plugin the picker just selected. Reached ONLY through
    ///     <c>OnAddGuitarVstSelectionChanged</c> — the shared rack
    ///     (<c>VstRackView</c>, in the Tools popover and the Amp/FX panel) binds
    ///     <c>SelectedItem</c> through <see cref="VstRack" />, never a command,
    ///     so picking IS the gesture and
    ///     there is no button to press. It carried a <c>[RelayCommand]</c> until
    ///     0.58 and the generated property was bound by nothing.
    /// </summary>
    private void AddGuitarVstFromSelection()
    {
        var selection = AddGuitarVstSelection;
        if (selection == null || string.IsNullOrEmpty(selection.PluginPath)) return;
        AddGuitarChainSlot(
            // Player 0 explicitly: a slot added from the picker is the
            // single-player chain, and there is no seat picker to say otherwise.
            new VstChainEntry
            { Id = Guid.NewGuid(), PluginPath = selection.PluginPath, Bypass = false, Player = 0 },
            persist: true, pushToEngine: true);
        AddGuitarVstSelection = null;
    }

    partial void OnAddGuitarVstSelectionChanged(VstPluginInfo? value)
    {
        if (value != null && !string.IsNullOrEmpty(value.PluginPath))
            AddGuitarVstFromSelection();
    }

    partial void OnVstEnabledChanged(bool value)
    {
        _audioService.IsVstEffectEnabled = value;
    }

    partial void OnSelectedInstrumentVstChanged(VstPluginInfo? value)
    {
        if (value != null && !string.IsNullOrEmpty(value.PluginPath))
        {
            var firstSelection = !_hasEverLoadedInstrumentVst;
            _hasEverLoadedInstrumentVst = true;

            if (firstSelection)
            {
                MidiEnabled = true;
                if (_vstRestoreComplete) InstrumentVstGuiOpen = true;
            }

            // Optimistically reflect "loading" while the native side scans
            // sample libraries; the audio service will fire
            // InstrumentVstLoadChanged once attach completes (or fails).
            IsInstrumentVstReady = false;
            IsInstrumentVstLoading = true;

            var wantEditor = _vstRestoreComplete && InstrumentVstGuiOpen;
            _audioService.LoadInstrumentVst(value.PluginPath, openEditorAfterLoad: wantEditor);
        }
        else
        {
            // MIDI Out stays as the user left it: without a plugin it plays the
            // built-in synth.
            _audioService.LoadInstrumentVst("");
            InstrumentVstGuiOpen = false;
            _hasEverLoadedInstrumentVst = false;
            IsInstrumentVstReady = false;
            IsInstrumentVstLoading = false;
        }

        ApplyMidiOutRouting();
        PersistLastVst(false, value?.PluginPath ?? string.Empty);
    }

    partial void OnInstrumentVstGuiOpenChanged(bool value)
    {
        if (_suppressInstrumentEditorToggle) return;
        _audioService.ToggleInstrumentVstEditor(value);
    }

    partial void OnMidiEnabledChanged(bool value) => ApplyMidiOutRouting();
}
