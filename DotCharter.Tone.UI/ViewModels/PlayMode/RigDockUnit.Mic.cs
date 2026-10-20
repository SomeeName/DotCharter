// RigDockUnit: the MIC PLACEMENT - the mic unit's own placement, and a cabinet's mic (the mic unit after it, or the way to add one).

using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class RigDockUnit
{
    private readonly PedalboardViewModel? _micRig;
    private RigMicPlacementViewModel? _micPlacement;
    private bool _micPlacementResolved;
    private RigMicPlacementViewModel? _cabMic;
    private bool _cabMicResolved;

    /// <summary>The native mic model's module id ("Cab Mic Position").</summary>
    public const string MicModuleId = "micpos";

    /// <summary>True for the mic unit itself: its editor IS the placement.</summary>
    public bool IsMicPlacementFamily => Family == RigEditorFamily.MicPlacement;

    /// <summary>The placement over THIS unit, when it is the mic model.</summary>
    public RigMicPlacementViewModel? MicPlacement
    {
        get
        {
            if (_micPlacementResolved) return _micPlacement;
            _micPlacementResolved = true;
            _micPlacement = IsMicPlacementFamily ? RigMicPlacementViewModel.For(Node) : null;
            return _micPlacement;
        }
    }

    /// <summary>
    ///     A cabinet's microphone: the mic unit that follows it in the chain (before
    ///     the next cabinet), which is the one the native routing feeds from this
    ///     cab. Null when the cab has none — then the console offers to add one.
    /// </summary>
    public RigMicPlacementViewModel? CabMic
    {
        get
        {
            if (_cabMicResolved) return _cabMic;
            _cabMicResolved = true;
            _cabMic = IsCabinetFamily ? RigMicPlacementViewModel.For(MicAfterCab(_micRig, Node)) : null;
            return _cabMic;
        }
    }

    public bool HasCabMic => CabMic is not null;

    /// <summary>A cabinet with no mic unit after it, in a rig whose catalogue has one.</summary>
    public bool CanAddCabMic => IsCabinetFamily && CabMic is null
                                && _micRig?.AvailableModules.Any(m => m.Id == MicModuleId) == true;

    /// <summary>The mic unit after <paramref name="cab" />: the first one before
    /// another cabinet, or null.</summary>
    internal static PedalNodeViewModel? MicAfterCab(PedalboardViewModel? rig, PedalNodeViewModel cab)
    {
        if (rig is null) return null;
        var index = rig.Nodes.IndexOf(cab);
        if (index < 0) return null;
        for (var i = index + 1; i < rig.Nodes.Count; i++)
        {
            var node = rig.Nodes[i];
            if (node.ModuleTypeId == MicModuleId) return node;
            if (RigArtwork.FamilyFor(node.ModuleTypeId) == RigEditorFamily.Cabinet) return null;
        }

        return null;
    }

    /// <summary>Puts a mic on this cabinet: a mic unit straight after it.</summary>
    [RelayCommand]
    private void AddCabMic()
    {
        if (!CanAddCabMic || _micRig is not { } rig) return;
        var type = rig.AvailableModules.First(m => m.Id == MicModuleId);
        rig.AddGearAt(type, rig.Nodes.IndexOf(Node) + 1);
        // the add selects nothing new: the cab stays open, now with its mic
        if (!ReferenceEquals(rig.SelectedNode, Node)) rig.SelectNodeCommand.Execute(Node);
        _cabMicResolved = false;
        OnPropertyChanged(nameof(CabMic));
        OnPropertyChanged(nameof(HasCabMic));
        OnPropertyChanged(nameof(CanAddCabMic));
    }

    /// <summary>Opens the cabinet's mic unit on the stage, at full size.</summary>
    [RelayCommand]
    private void OpenCabMic()
    {
        if (CabMic is { } mic && _micRig is { } rig) rig.SelectNodeCommand.Execute(mic.Node);
    }

    /// <summary>Lets go of the parameter subscriptions this unit's placements hold:
    /// the screen calls it when the selection moves on, since the mic unit's params
    /// outlive this dock.</summary>
    public void Release()
    {
        _micPlacement?.Dispose();
        _cabMic?.Dispose();
    }
}
