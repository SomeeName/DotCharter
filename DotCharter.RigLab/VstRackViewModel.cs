using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Logging;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.UI.ViewModels.Editor;

namespace DotCharter.RigLab;

/// <summary>
///     RigLab's Plugins tab: the VST3 effects on the guitar input, in signal
///     order. The same engine calls DotCharter's transport makes
///     (<see cref="IVstChainHost.SetGuitarVstChain" />, per-slot bypass and
///     editor) over the same slot view model, persisted to
///     <see cref="RigLabConfig.GuitarVstChain" /> in the same shape. The tab
///     draws it with the SHARED rack (<c>DotCharter.Tone.UI.Views.PlayMode.VstRackView</c>,
///     through <see cref="IVstRack" />) that DotCharter's Tools popover and
///     Amp/FX panel draw the transport's chain with.
///     <para>
///         <b>Every change pushes the WHOLE chain</b>: the engine diffs it against
///         what is loaded, so a reorder or a remove is one call, and a slot's id
///         is what lets it keep its loaded plugin across the push.
///     </para>
/// </summary>
public sealed partial class VstRackViewModel : ObservableObject, IVstRack, IDisposable
{
    private readonly IVstChainHost _host;
    private readonly RigLabConfigService _config;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger? _logger;
    private readonly Func<Func<Task>, Task> _worker;
    private bool _restored;

    public VstRackViewModel(
        IVstChainHost host, RigLabConfigService config, IUiDispatcher ui, IAppLogger? logger = null,
        Func<Func<Task>, Task>? worker = null)
    {
        _host = host;
        _config = config;
        _ui = ui;
        _logger = logger;
        _worker = worker ?? (static work => Task.Run(work));
        _host.GuitarVstSlotLoadChanged += OnSlotLoadChanged;
        _host.GuitarVstSlotEditorClosed += OnSlotEditorClosed;
    }

    public ObservableCollection<VstChainSlotViewModel> Chain { get; } = [];

    /// <summary>The effects the last scan found (instruments are not offered:
    /// this is an input chain).</summary>
    public ObservableCollection<VstPluginInfo> AvailablePlugins { get; } = [];

    [ObservableProperty] private VstPluginInfo? _addSelection;
    [ObservableProperty] private bool _isScanning;

    public bool IsEmpty => Chain.Count == 0;

    /// <summary>Scans the plugin folders and restores the saved chain onto the
    /// engine. Called once at startup, off the UI thread.</summary>
    public async Task RestoreAsync()
    {
        var saved = _config.Load().GuitarVstChain
            .Where(e => e.PluginPath.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
            .Select(Copy)
            .ToList();

        _host.IsVstEffectEnabled = true;
        if (saved.Count > 0) _host.SetGuitarVstChain(saved);

        var plugins = Scan();
        await PostAsync(() =>
        {
            ReplacePlugins(plugins);
            foreach (var entry in saved)
            {
                var slot = NewSlot(entry);
                // The push above raced the UI; a plugin that already finished
                // loading raised its event before this row existed.
                slot.IsLoading = !_host.IsGuitarVstSlotLoaded(entry.Id);
                Chain.Add(slot);
            }
            Renumber();
            _restored = true;
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task RescanAsync()
    {
        if (IsScanning) return Task.CompletedTask;
        IsScanning = true;
        return _worker(async () =>
        {
            var plugins = Scan();
            await PostAsync(() =>
            {
                ReplacePlugins(plugins);
                IsScanning = false;
            }).ConfigureAwait(false);
        });
    }

    partial void OnAddSelectionChanged(VstPluginInfo? value)
    {
        if (value is null || string.IsNullOrEmpty(value.PluginPath)) return;
        var slot = NewSlot(new VstChainEntry { Id = Guid.NewGuid(), PluginPath = value.PluginPath, Player = 0 });
        Chain.Add(slot);
        Renumber();
        Commit();
        AddSelection = null;
    }

    ICommand IVstRack.RescanCommand => RescanCommand;

    /// <summary>Moves one slot to an arbitrary position — what the rack's drag
    /// produces. False, touching nothing, for an out-of-range or no-op move, so
    /// a drag that ends where it started neither saves nor re-pushes.</summary>
    public bool MoveSlot(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Chain.Count) return false;
        if (toIndex < 0 || toIndex >= Chain.Count) return false;
        if (fromIndex == toIndex) return false;
        Chain.Move(fromIndex, toIndex);
        Renumber();
        Commit();
        return true;
    }

    private void Remove(VstChainSlotViewModel slot)
    {
        if (!Chain.Remove(slot)) return;
        // Close the editor first so the plugin's window goes before its slot.
        try { slot.EditorOpen = false; }
        catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, "Closing a VST editor failed.", ex); }
        Renumber();
        Commit();
    }

    private VstChainSlotViewModel NewSlot(VstChainEntry entry)
    {
        var slot = new VstChainSlotViewModel(entry.Id, DisplayName(entry.PluginPath), entry.PluginPath,
            entry.Bypass, entry.Player)
        {
            RemoveRequested = Remove,
            BypassChanged = (s, bypass) =>
            {
                _host.SetGuitarVstSlotBypass(s.Id, bypass);
                Persist();
            },
            EditorChanged = (s, open) => _host.ToggleGuitarVstSlotEditor(s.Id, open)
        };
        return slot;
    }

    private void Commit()
    {
        Persist();
        _host.SetGuitarVstChain([.. Chain.Select(Entry)]);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Persist()
    {
        if (!_restored) return;
        var chain = Chain.Select(Entry).ToList();
        _config.Update(c => c.GuitarVstChain = chain);
    }

    private void Renumber()
    {
        for (var i = 0; i < Chain.Count; i++) Chain[i].DisplayIndex = i + 1;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private List<VstPluginInfo> Scan()
    {
        try
        {
            return [.. _host.ScanVstPlugins()
                .Where(p => p.IsEffect || !p.IsInstrument)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "VST3 scan failed.", ex);
            return [];
        }
    }

    private void ReplacePlugins(List<VstPluginInfo> plugins)
    {
        AvailablePlugins.Clear();
        foreach (var p in plugins) AvailablePlugins.Add(p);
    }

    private string DisplayName(string path) =>
        AvailablePlugins.FirstOrDefault(p => string.Equals(p.PluginPath, path, StringComparison.OrdinalIgnoreCase))?.Name
        ?? Path.GetFileNameWithoutExtension(path);

    private void OnSlotLoadChanged(object? sender, (Guid SlotId, bool Loaded) e) =>
        _ui.PostNormal(() =>
        {
            foreach (var slot in Chain)
                if (slot.Id == e.SlotId) slot.IsLoading = !e.Loaded;
        });

    private void OnSlotEditorClosed(object? sender, Guid slotId) =>
        _ui.PostNormal(() =>
        {
            foreach (var slot in Chain)
                if (slot.Id == slotId) slot.SetEditorOpenFromEngine(false);
        });

    private Task PostAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    private static VstChainEntry Entry(VstChainSlotViewModel s) =>
        new() { Id = s.Id, PluginPath = s.PluginPath, Bypass = s.Bypass, Player = s.Player };

    private static VstChainEntry Copy(VstChainEntry e) =>
        new() { Id = e.Id, PluginPath = e.PluginPath, Bypass = e.Bypass, Player = e.Player };

    public void Dispose()
    {
        _host.GuitarVstSlotLoadChanged -= OnSlotLoadChanged;
        _host.GuitarVstSlotEditorClosed -= OnSlotEditorClosed;
    }
}
