using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.Scenes;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The rig's SETLISTS: named, ordered lists of tones for a gig, each entry
///     optionally starting on a scene. One of them can be ACTIVE — the one the
///     scene bar's ◀ ▶, PageUp / PageDown and a MIDI program change step through
///     (program n = entry n, 0 = the first).
///     <para>
///         Persisted in <c>RigConfig.RigSetlists</c> / <c>RigActiveSetlistId</c>
///         (per install, like the user tones the entries point at) on every
///         change. Entries name presets by ID, so a tone overwritten keeps its
///         place and a deleted one reads as missing instead of silently skipping a
///         song. Which entry is playing is session state and starts at none.
///     </para>
/// </summary>
public sealed partial class RigSetlistsViewModel : ObservableObject
{
    private readonly IDspTonePresetService _presets;
    private readonly IRigConfigService? _config;
    private readonly Action<DspTonePreset, int?> _activate;
    private readonly Func<DspTonePreset?> _currentTone;
    private readonly ILocalizationService _loc;
    private bool _loading;

    /// <param name="presets">Resolves an entry's preset id.</param>
    /// <param name="config">Where the setlists persist; null = session only (tests).</param>
    /// <param name="localization">The words it and its rows print.</param>
    /// <param name="activate">Applies a tone, starting on a scene (null = its own).</param>
    /// <param name="currentTone">The tone the rig is playing, for "Add current tone".</param>
    public RigSetlistsViewModel(IDspTonePresetService presets, IRigConfigService? config,
        ILocalizationService localization, Action<DspTonePreset, int?> activate, Func<DspTonePreset?> currentTone)
    {
        _presets = presets;
        _config = config;
        _loc = localization;
        _activate = activate;
        _currentTone = currentTone;
        SceneChoices = [new RigSceneChoice(null, _loc["Rig_Setlist_SceneOwn"]),
            .. Enumerable.Range(0, RigSceneSet.Count).Select(i => new RigSceneChoice(i,
                string.Format(CultureInfo.CurrentCulture, _loc["Rig_Scene_Name"], RigScenes.Letter(i))))];
        Load();
    }

    /// <summary>The words its rows print (they are built here).</summary>
    internal ILocalizationService Localization => _loc;

    /// <summary>Every setlist, in the order they were made.</summary>
    public ObservableCollection<RigSetlistViewModel> Setlists { get; } = [];

    /// <summary>The starting-scene choices an entry offers: the tone's own, then A–H.</summary>
    public IReadOnlyList<RigSceneChoice> SceneChoices { get; }

    /// <summary>The setlist being edited in the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(IsSelectedActive), nameof(ActivateLabel))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSetlistCommand), nameof(ToggleActiveCommand), nameof(AddCurrentToneCommand))]
    private RigSetlistViewModel? _selectedSetlist;

    /// <summary>The LIVE setlist (null = none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActive), nameof(IsSelectedActive), nameof(ActivateLabel),
        nameof(ActiveName), nameof(ActivePosition), nameof(ActiveEntryName), nameof(ActiveEntrySuffix))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    private RigSetlistViewModel? _activeSetlist;

    /// <summary>The entry of the active setlist that is playing; -1 = none yet.</summary>
    public int ActiveIndex { get; private set; } = -1;

    /// <summary>The last thing worth saying (a missing tone, a program with no song).</summary>
    [ObservableProperty] private string? _status;

    public bool HasSetlists => Setlists.Count > 0;
    public bool HasSelected => SelectedSetlist is not null;
    public bool HasActive => ActiveSetlist is not null;
    public bool IsSelectedActive => SelectedSetlist is not null && ReferenceEquals(SelectedSetlist, ActiveSetlist);
    public string ActivateLabel => _loc[IsSelectedActive ? "Rig_Setlist_Deactivate" : "Rig_Setlist_Activate"];

    /// <summary>The scene bar's setlist name ("none" without one).</summary>
    public string ActiveName => ActiveSetlist?.Name ?? _loc["Rig_Setlist_None"];

    /// <summary>"3/12", or "–/12" before the first song.</summary>
    public string ActivePosition => ActiveSetlist is not { } list
        ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, "{0}/{1}",
            ActiveIndex >= 0 ? (ActiveIndex + 1).ToString(CultureInfo.CurrentCulture) : "–", list.Entries.Count);

    /// <summary>The tone of the entry that is playing.</summary>
    public string ActiveEntryName => ActiveSetlist is { } list && ActiveIndex >= 0 && ActiveIndex < list.Entries.Count
        ? list.Entries[ActiveIndex].PresetName
        : string.Empty;

    /// <summary>" · &lt;tone&gt;" after the setlist's name on the scene bar, or nothing.</summary>
    public string ActiveEntrySuffix => ActiveEntryName is { Length: > 0 } name ? " · " + name : string.Empty;

    // ── the live half: step, jump, program change ───────────────────────────

    /// <summary>The next song of the active setlist (the first, before any). Stops at the end.</summary>
    [RelayCommand(CanExecute = nameof(HasActive))]
    private void Next()
    {
        if (ActiveSetlist is not { } list || list.Entries.Count == 0) return;
        var next = ActiveIndex < 0 ? 0 : ActiveIndex + 1;
        if (next < list.Entries.Count) ActivateEntry(list, next);
    }

    /// <summary>The previous song (the first, before any). Stops at the start.</summary>
    [RelayCommand(CanExecute = nameof(HasActive))]
    private void Previous()
    {
        if (ActiveSetlist is not { } list || list.Entries.Count == 0) return;
        var previous = ActiveIndex <= 0 ? 0 : ActiveIndex - 1;
        if (previous != ActiveIndex) ActivateEntry(list, previous);
    }

    /// <summary>Plays one entry (the panel's ▶) — its setlist becomes the active one.</summary>
    [RelayCommand]
    private void PlayEntry(RigSetlistEntryViewModel? entry)
    {
        if (entry is null) return;
        var list = Setlists.FirstOrDefault(s => s.Entries.Contains(entry));
        if (list is not null) ActivateEntry(list, list.Entries.IndexOf(entry));
    }

    /// <summary>
    ///     A MIDI program change: program n is entry n of the active setlist
    ///     (0 = the first). Returns false when no setlist is active (the caller
    ///     then falls back to the player's own tones); a program past the end of
    ///     the list is handled — ignored with a status line — so it never falls
    ///     through to another tone.
    /// </summary>
    public bool SelectProgram(int program)
    {
        if (ActiveSetlist is not { } list) return false;
        if (program >= 0 && program < list.Entries.Count)
        {
            ActivateEntry(list, program);
            return true;
        }

        Status = string.Format(CultureInfo.CurrentCulture,
            _loc["Rig_Setlist_NoSongForProgram"], program, list.Name);
        return true;
    }

    private void ActivateEntry(RigSetlistViewModel list, int index)
    {
        if (index < 0 || index >= list.Entries.Count) return;
        if (!ReferenceEquals(ActiveSetlist, list))
        {
            ActiveSetlist = list;
            Persist();
        }

        ActiveIndex = index;
        RaiseLive();
        var entry = list.Entries[index];
        var preset = _presets.FindById(entry.PresetId);
        if (preset is null)
        {
            Status = string.Format(CultureInfo.CurrentCulture,
                _loc["Rig_Setlist_Missing"], entry.PresetId);
            return;
        }

        Status = null;
        _activate(preset, entry.SelectedScene?.Scene);
    }

    private void RaiseLive()
    {
        foreach (var list in Setlists)
        {
            for (var i = 0; i < list.Entries.Count; i++)
                list.Entries[i].IsCurrent = ReferenceEquals(list, ActiveSetlist) && i == ActiveIndex;
        }

        foreach (var list in Setlists) list.IsActive = ReferenceEquals(list, ActiveSetlist);
        OnPropertyChanged(nameof(ActivePosition));
        OnPropertyChanged(nameof(ActiveEntryName));
        OnPropertyChanged(nameof(ActiveEntrySuffix));
        OnPropertyChanged(nameof(ActiveName));
    }

    partial void OnActiveSetlistChanged(RigSetlistViewModel? value)
    {
        if (!_loading) ActiveIndex = -1;
        RaiseLive();
    }

    // ── editing ───────────────────────────────────────────────────────────────

    /// <summary>A new, empty setlist, selected for editing.</summary>
    [RelayCommand]
    private void NewSetlist()
    {
        var name = string.Format(CultureInfo.CurrentCulture,
            _loc["Rig_Setlist_DefaultName"], Setlists.Count + 1);
        var list = new RigSetlistViewModel(this, Guid.NewGuid().ToString("N"), name);
        Setlists.Add(list);
        SelectedSetlist = list;
        OnPropertyChanged(nameof(HasSetlists));
        Persist();
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void DeleteSetlist()
    {
        if (SelectedSetlist is not { } list) return;
        var at = Setlists.IndexOf(list);
        Setlists.Remove(list);
        if (ReferenceEquals(ActiveSetlist, list)) ActiveSetlist = null;
        SelectedSetlist = Setlists.Count == 0 ? null : Setlists[Math.Clamp(at, 0, Setlists.Count - 1)];
        OnPropertyChanged(nameof(HasSetlists));
        Persist();
    }

    /// <summary>Makes the selected setlist the live one, or stops it being live.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void ToggleActive()
    {
        if (SelectedSetlist is not { } list) return;
        ActiveSetlist = ReferenceEquals(ActiveSetlist, list) ? null : list;
        Persist();
    }

    /// <summary>Appends the tone the rig is playing to the selected setlist.</summary>
    [RelayCommand(CanExecute = nameof(CanAddCurrentTone))]
    private void AddCurrentTone()
    {
        if (SelectedSetlist is not { } list) return;
        if (_currentTone() is not { } tone || string.IsNullOrEmpty(tone.Id))
        {
            Status = _loc["Rig_Setlist_NoCurrentTone"];
            return;
        }

        list.Entries.Add(new RigSetlistEntryViewModel(this, tone.Id, null));
        list.Renumber();
        Status = null;
        Persist();
        RaiseLive();
    }

    private bool CanAddCurrentTone() => HasSelected;

    [RelayCommand]
    private void MoveEntryUp(RigSetlistEntryViewModel? entry) => MoveEntry(entry, -1);

    [RelayCommand]
    private void MoveEntryDown(RigSetlistEntryViewModel? entry) => MoveEntry(entry, +1);

    private void MoveEntry(RigSetlistEntryViewModel? entry, int by)
    {
        if (entry is null || Setlists.FirstOrDefault(s => s.Entries.Contains(entry)) is not { } list) return;
        var from = list.Entries.IndexOf(entry);
        var to = from + by;
        if (to < 0 || to >= list.Entries.Count) return;
        var playing = ReferenceEquals(list, ActiveSetlist) && ActiveIndex >= 0 ? list.Entries[ActiveIndex] : null;
        list.Entries.Move(from, to);
        list.Renumber();
        if (playing is not null) ActiveIndex = list.Entries.IndexOf(playing);
        Persist();
        RaiseLive();
    }

    [RelayCommand]
    private void RemoveEntry(RigSetlistEntryViewModel? entry)
    {
        if (entry is null || Setlists.FirstOrDefault(s => s.Entries.Contains(entry)) is not { } list) return;
        var playing = ReferenceEquals(list, ActiveSetlist) && ActiveIndex >= 0 ? list.Entries[ActiveIndex] : null;
        list.Entries.Remove(entry);
        list.Renumber();
        if (ReferenceEquals(list, ActiveSetlist))
            ActiveIndex = playing is null || ReferenceEquals(playing, entry) ? -1 : list.Entries.IndexOf(playing);
        Persist();
        RaiseLive();
    }

    /// <summary>Re-reads every entry's tone name (a tone renamed, saved or deleted
    /// since). The panel calls it when it is shown.</summary>
    public void RefreshNames()
    {
        foreach (var list in Setlists)
        {
            foreach (var entry in list.Entries)
                entry.Resolve(_presets.FindById(entry.PresetId));
        }

        OnPropertyChanged(nameof(ActiveEntryName));
        OnPropertyChanged(nameof(ActiveEntrySuffix));
    }

    internal void Resolve(RigSetlistEntryViewModel entry) => entry.Resolve(_presets.FindById(entry.PresetId));

    // ── persistence ──────────────────────────────────────────────────────────

    private void Load()
    {
        if (_config is null) return;
        _loading = true;
        try
        {
            var config = _config.LoadRigConfig();
            foreach (var dto in config.RigSetlists ?? [])
            {
                if (dto is null || string.IsNullOrWhiteSpace(dto.Id)) continue;
                var list = new RigSetlistViewModel(this, dto.Id, dto.Name ?? string.Empty);
                foreach (var entry in dto.Entries ?? [])
                {
                    if (entry is { PresetId.Length: > 0 })
                    {
                        list.Entries.Add(new RigSetlistEntryViewModel(this, entry.PresetId,
                            entry.Scene is >= 0 and < RigSceneSet.Count ? entry.Scene : null));
                    }
                }

                list.Renumber();
                Setlists.Add(list);
            }

            ActiveSetlist = Setlists.FirstOrDefault(s => s.Id == config.RigActiveSetlistId);
            SelectedSetlist = ActiveSetlist ?? Setlists.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Writes every setlist and the active one's id — under the config's
    /// lock, because it replaces a collection.</summary>
    internal void Persist()
    {
        if (_config is null || _loading) return;
        var lists = Setlists.Select(s => s.ToDto()).ToList();
        var activeId = ActiveSetlist?.Id;
        try
        {
            _config.UpdateRigConfig(c =>
            {
                c.RigSetlists = lists;
                c.RigActiveSetlistId = activeId;
            });
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }
}

/// <summary>One setlist: a name and its songs.</summary>
public sealed partial class RigSetlistViewModel : ObservableObject
{
    private readonly RigSetlistsViewModel _owner;

    private readonly ILocalizationService _loc;

    public RigSetlistViewModel(RigSetlistsViewModel owner, string id, string name)
    {
        _owner = owner;
        _loc = owner.Localization;
        Id = id;
        _name = string.IsNullOrWhiteSpace(name) ? _loc["Rig_Setlist_Untitled"] : name.Trim();
        Entries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountLabel));
            OnPropertyChanged(nameof(HasEntries));
        };
    }

    public bool HasEntries => Entries.Count > 0;

    public string Id { get; }

    /// <summary>Edited in place; a blank name falls back to "Untitled setlist".</summary>
    [ObservableProperty] private string _name;

    /// <summary>The live one (the list draws its LIVE tag from this).</summary>
    [ObservableProperty] private bool _isActive;

    public ObservableCollection<RigSetlistEntryViewModel> Entries { get; } = [];

    public string CountLabel => string.Format(CultureInfo.CurrentCulture,
        _loc[Entries.Count == 1 ? "Rig_Setlist_CountOne" : "Rig_Setlist_Count"], Entries.Count);

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Name = _loc["Rig_Setlist_Untitled"];
            return;
        }

        _owner.Persist();
    }

    internal void Renumber()
    {
        for (var i = 0; i < Entries.Count; i++) Entries[i].Position = i + 1;
        OnPropertyChanged(nameof(CountLabel));
    }

    internal RigSetlistDto ToDto() => new()
    {
        Id = Id,
        Name = Name,
        Entries = Entries.Select(e => new RigSetlistEntryDto { PresetId = e.PresetId, Scene = e.SelectedScene?.Scene }).ToList()
    };
}

/// <summary>One song of a setlist: which tone, where in the list, which scene it starts on.</summary>
public sealed partial class RigSetlistEntryViewModel : ObservableObject
{
    private readonly RigSetlistsViewModel _owner;
    private readonly bool _resolving;
    private readonly ILocalizationService _loc;

    public RigSetlistEntryViewModel(RigSetlistsViewModel owner, string presetId, int? scene)
    {
        _owner = owner;
        _loc = owner.Localization;
        PresetId = presetId;
        _resolving = true;
        _selectedScene = owner.SceneChoices.FirstOrDefault(c => c.Scene == scene) ?? owner.SceneChoices[0];
        _resolving = false;
        owner.Resolve(this);
    }

    public string PresetId { get; }

    /// <summary>1-based, as the list prints it (and program change counts from 0).</summary>
    [ObservableProperty] private int _position;

    [ObservableProperty] private string _presetName = string.Empty;

    /// <summary>The tone is gone (deleted since it was added).</summary>
    [ObservableProperty] private bool _isMissing;

    /// <summary>The entry that is playing in the live setlist.</summary>
    [ObservableProperty] private bool _isCurrent;

    [ObservableProperty] private RigSceneChoice? _selectedScene;

    public IReadOnlyList<RigSceneChoice> SceneChoices => _owner.SceneChoices;

    partial void OnSelectedSceneChanged(RigSceneChoice? value)
    {
        if (!_resolving) _owner.Persist();
    }

    internal void Resolve(DspTonePreset? preset)
    {
        IsMissing = preset is null;
        PresetName = preset?.Name ?? string.Format(CultureInfo.CurrentCulture,
            _loc["Rig_Setlist_MissingName"], PresetId);
    }
}

/// <summary>A starting scene an entry can pick (null = the tone's own).</summary>
public sealed record RigSceneChoice(int? Scene, string Label)
{
    public override string ToString() => Label;
}
