using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// Level-matched A/B switching: slot B plays at slot A's level, by a trim
///     on the rig's TRAILING gain module — the master gain normalized rigs already
///     end with, so the match adds no DSP. Each slot's level is learnt from the
///     rig's output meter (<see cref="ToneCompareLevelMatch" />); a slot is stored
///     UNTRIMMED (<see cref="Snapshot" />), or the trim would compound on every
///     switch. A collaborator of <see cref="PedalboardViewModel" />, which owns the
///     slots and the apply.
/// </summary>
public sealed partial class RigCompareLevelViewModel(
    Func<IDspChainService> chain,
    Func<RigNodeMetersViewModel> output,
    ToneCompareSlots slots,
    Action resync) : ObservableObject
{
    private readonly ToneCompareLevelMatch _match = new();

    /// <summary>The trim on the chain's trailing gain right now, as it LANDED.</summary>
    private double _trimDb;

    /// <summary>On by default: an unmatched A/B mostly picks the louder tone.</summary>
    [ObservableProperty] private bool _isMatched = true;

    /// <summary>The trim on the chain ("−3.2 dB"); empty at none.</summary>
    public string TrimLabel => _trimDb == 0 ? string.Empty : _trimDb.ToString("+0.0 dB;−0.0 dB", CultureInfo.CurrentCulture);

    public bool HasTrim => _trimDb != 0;

    /// <summary>One output-meter reading for the live slot, on the rig's meter clock.</summary>
    public void Sample()
    {
        var meters = output();
        if (meters.HasReading) _match.Record(slots.Active, meters.OutputRmsDb, _trimDb);
    }

    /// <summary>The live rig with the trim taken back off: what a slot stores. The
    /// scenes fold the stripped values, as <see cref="DcRigSnapshot.FromChain" /> does.</summary>
    public DcRigPreset Snapshot()
    {
        var dsp = chain();
        if (_trimDb == 0) return DcRigSnapshot.FromChain(dsp);
        var modules = CompareLevelTrim.Strip(DcRigSnapshot.Modules(dsp.GetNodes()), _trimDb);
        return new DcRigPreset(modules) { Scenes = RigScenes.Fold(dsp.Scenes, modules) };
    }

    /// <summary>The rig to apply for the slot that just became live, and the trim
    /// to put on after the apply (<see cref="SetLiveTrim" />). The incoming rig is
    /// untrimmed, so the chain's trim is 0 from here.</summary>
    public DcRigPreset Prepare(DcRigPreset incoming, out double trimDb)
    {
        trimDb = IsMatched ? _match.TrimFor(slots.Active, slots.Get(ToneCompareSlot.A), slots.Get(ToneCompareSlot.B)) : 0;
        _trimDb = 0;
        return trimDb == 0 ? incoming : CompareLevelTrim.EnsureTrailingGain(incoming);
    }

    /// <summary>Moves the trailing gain from the current trim to
    /// <paramref name="trimDb" /> with one param write. True when the chain changed.</summary>
    public bool SetLiveTrim(double trimDb)
    {
        var dsp = chain();
        var last = dsp.GetNodes().LastOrDefault();
        var changed = false;
        if (last is not null && last.ModuleTypeId == CompareLevelTrim.GainModuleId && !last.Bypassed &&
            (trimDb != 0 || _trimDb != 0))
        {
            var current = dsp.GetParam(last.NodeId, CompareLevelTrim.GainParamId) ?? 0f;
            var (gain, applied) = CompareLevelTrim.Apply(current - _trimDb, trimDb);
            changed = dsp.SetParam(last.NodeId, CompareLevelTrim.GainParamId, (float)gain);
            if (changed) _trimDb = applied;
        }
        else if (trimDb == 0)
        {
            _trimDb = 0;
        }

        RaiseTrim();
        return changed;
    }

    /// <summary>A seed, a reset or a new tone: both levels are stale and the chain
    /// the trim rode on is gone.</summary>
    public void Forget()
    {
        _match.Reset();
        _trimDb = 0;
        RaiseTrim();
    }

    /// <summary>Off takes the trim off at once; on matches the slot that is playing.</summary>
    partial void OnIsMatchedChanged(bool value)
    {
        if (SetLiveTrim(value ? _match.TrimFor(slots.Active, slots.Get(ToneCompareSlot.A), slots.Get(ToneCompareSlot.B)) : 0))
            resync();
    }

    private void RaiseTrim()
    {
        OnPropertyChanged(nameof(TrimLabel));
        OnPropertyChanged(nameof(HasTrim));
    }
}
