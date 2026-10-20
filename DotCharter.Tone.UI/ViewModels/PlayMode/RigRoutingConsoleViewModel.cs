using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The routing console the splitter and merger open (tone-rig design:
///     "Routing / Splitter" and "Routing / Merger" share one editor): the
///     Series / Dual / Triple choice and one mix strip per live lane.
///
///     <para>
///         It owns nothing. The mode commands are the rig's own routing
///         commands, and the strips are the board's <see cref="RigBoardLane" />s
///         whose <see cref="RigLaneMixerViewModel" /> already projects the
///         merger's <c>levelN</c> / <c>panN</c> / <c>muteN</c> / <c>soloN</c>
///         — the lane mixers ARE the merger (design 2.5), so the console and
///         the board can never disagree.
///     </para>
/// </summary>
public sealed class RigRoutingConsoleViewModel : ObservableObject, IDisposable
{
    private readonly PedalboardViewModel _rig;
    private readonly ILocalizationService _loc;

    public RigRoutingConsoleViewModel(PedalboardViewModel rig, ObservableCollection<RigBoardLane>? lanes)
    {
        _rig = rig;
        _loc = rig.Localization;
        Lanes = lanes ?? [];
        _rig.PropertyChanged += OnRigChanged;
        Lanes.CollectionChanged += (_, _) => NotifyMode();
    }

    /// <summary>The live lanes, in branch order — the same instances the board draws.</summary>
    public ObservableCollection<RigBoardLane> Lanes { get; }

    public ICommand RoutingSerialCommand => _rig.RoutingSerialCommand;
    public ICommand RoutingSplit2Command => _rig.RoutingSplit2Command;
    public ICommand RoutingSplit3Command => _rig.RoutingSplit3Command;

    public bool IsSerial => _rig.IsRoutingSerial;
    public bool IsDual => _rig.IsRoutingSplit2;
    public bool IsTriple => _rig.IsRoutingSplit3;

    public int LaneCount => Lanes.Count;

    /// <summary>"Triple" / "Dual" / "Series" for the console's big word.</summary>
    public string ModeName => _loc[_rig.RoutingBranches switch
    {
        >= 3 => "Rig_Routing_Triple",
        2 => "Rig_Routing_Dual",
        _ => "Rig_Routing_Series"
    }];

    /// <summary>"parallel" beside the mode word, or "chain" when serial.</summary>
    public string ModeKind => _loc[_rig.RoutingBranches > 1 ? "Rig_Editor_Parallel" : "Rig_Editor_Chain"];

    /// <summary>"3 parallel lanes" / "1 lane" for the strip header.</summary>
    public string LaneSummary => LaneCount == 1
        ? _loc["Rig_Editor_OneLane"]
        : _loc.Format("Rig_Editor_ParallelLanes", LaneCount);

    private void OnRigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PedalboardViewModel.RoutingBranches) or nameof(PedalboardViewModel.IsRoutingSerial)
            or nameof(PedalboardViewModel.IsRoutingSplit2) or nameof(PedalboardViewModel.IsRoutingSplit3) or null)
        {
            NotifyMode();
        }
    }

    private void NotifyMode()
    {
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsDual));
        OnPropertyChanged(nameof(IsTriple));
        OnPropertyChanged(nameof(LaneCount));
        OnPropertyChanged(nameof(ModeName));
        OnPropertyChanged(nameof(ModeKind));
        OnPropertyChanged(nameof(LaneSummary));
    }

    public void Dispose() => _rig.PropertyChanged -= OnRigChanged;
}
