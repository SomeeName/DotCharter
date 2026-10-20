using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp.Scenes;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     One button of the rig's scene bar (A–H): its letter, the name the player
///     gave it, whether it is playing and whether it holds values, and the verbs
///     its right-click menu offers. Every verb goes back to
///     <see cref="PedalboardViewModel" /> — the scene set lives on the chain, not
///     here — and this refreshes in place from <see cref="Update" />.
/// </summary>
public sealed partial class RigSceneSlotViewModel : ObservableObject
{
    private readonly PedalboardViewModel _rig;
    private readonly ILocalizationService _loc;

    public RigSceneSlotViewModel(PedalboardViewModel rig, int index)
    {
        _rig = rig;
        _loc = rig.Localization;
        Index = index;
        Letter = RigScenes.Letter(index);
        _isActive = index == 0;
        _isStored = index == 0;
    }

    /// <summary>0..7.</summary>
    public int Index { get; }

    /// <summary>"A".."H".</summary>
    public string Letter { get; }

    /// <summary>The key that selects it ("1".."8").</summary>
    public string Key => (Index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The player's name for it, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption), nameof(ToolTip), nameof(AccessibleName))]
    private string? _name;

    /// <summary>The scene that is playing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClear))]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    [NotifyCanExecuteChangedFor(nameof(StoreCommand))]
    private bool _isActive;

    /// <summary>The slot holds values (an unused one forks from the live rig when picked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClear))]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    private bool _isStored;

    /// <summary>The name is being typed in place (the button keeps its width).</summary>
    [ObservableProperty] private bool _isRenaming;

    /// <summary>The name being typed.</summary>
    [ObservableProperty] private string _editName = string.Empty;

    /// <summary>What the button prints beside its letter: the name, or nothing — an
    /// unused scene is its dim letter alone (a row of "empty" labels is clutter).</summary>
    public string Caption => Name ?? string.Empty;

    public string AccessibleName => string.Format(CultureInfo.CurrentCulture,
        _loc["Rig_Scene_Name"], Name is { Length: > 0 } n ? $"{Letter} · {n}" : Letter);

    public string ToolTip => string.Format(CultureInfo.CurrentCulture,
        _loc["Rig_Scene_SlotTip"], Name is { Length: > 0 } n ? $"{Letter} · {n}" : Letter, Key);

    /// <summary>Only a used scene that is not playing can be emptied.</summary>
    public bool CanClear => IsStored && !IsActive;

    internal void Update(string? name, bool stored, bool active)
    {
        Name = name;
        IsStored = stored;
        IsActive = active;
    }

    [RelayCommand]
    private void Select() => _rig.SelectSceneAt(Index);

    [RelayCommand]
    private void BeginRename()
    {
        EditName = Name ?? string.Empty;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        if (string.Equals(RigScenes.CleanName(EditName), Name, StringComparison.Ordinal)) return;
        _rig.RenameScene(Index, EditName);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand(CanExecute = nameof(CanStore))]
    private void Store() => _rig.StoreScene(Index);

    private bool CanStore() => !IsActive;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear() => _rig.ClearScene(Index);
}
