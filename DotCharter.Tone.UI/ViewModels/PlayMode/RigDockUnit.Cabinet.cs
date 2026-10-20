// RigDockUnit: the CABINET projection - the two IR slots, the voicing row and the response plot (its mic: RigDockUnit.Mic.cs).

using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class RigDockUnit
{
    /// <summary>True for the cabinet family: the speaker with its mic, the two IR
    /// slots, the voicing row and the response guide.</summary>
    public bool IsCabinetFamily => Family == RigEditorFamily.Cabinet;

    /// <summary>The voicing row in the design's order — blend, mix, low cut,
    /// high cut, output — i.e. the blend followed by every face knob the slots
    /// do not own. The same instances the pedal projection reports, so the
    /// reachability lint's count is unchanged.</summary>
    public IReadOnlyList<PedalParamViewModel> CabVoicingKnobs
    {
        get
        {
            var list = new List<PedalParamViewModel>(PrimaryKnobs.Count + 1);
            if (HasBlend && Blend is { } blend) list.Add(blend);
            list.AddRange(PrimaryKnobs);
            return list;
        }
    }

    public PedalParamViewModel? LowCut => Node.Params.FirstOrDefault(p => p.Id == "lowCutHz");
    public PedalParamViewModel? HighCut => Node.Params.FirstOrDefault(p => p.Id == "highCutHz");

    /// <summary>The response guide needs both cuts; a cabinet catalogue without
    /// them (a test double) simply draws nothing.</summary>
    public bool HasFilterResponse => LowCut is not null && HighCut is not null;

    /// <summary>"Use built-in cabinet": clears BOTH impulse-response slots, which
    /// is what returns the cabinet to its modelled speaker.</summary>
    [RelayCommand]
    private void ClearImpulseResponses()
    {
        foreach (var slot in CabinetSlots)
        {
            if (slot.File is { HasValue: true } file)
                file.ClearCommand.Execute(null);
        }
    }

    public bool HasAnyImpulseResponse => CabinetSlots.Any(s => s.File is { HasValue: true });
}
