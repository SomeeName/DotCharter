// Spillover (ABI v37): the rig-wide "let delay and reverb tails ring through a tone switch" setting.
namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class PedalboardViewModel
{
    private bool? _spillover;

    /// <summary>
    ///     Whether a delay or reverb that a tone switch REMOVES keeps ringing
    ///     (<c>RigConfig.RigSpillover</c>, on by default). Rig-wide: written to
    ///     every seat's chain, each of which replays it onto every engine it is
    ///     attached to — so a device change never quietly turns it off.
    /// </summary>
    public bool Spillover
    {
        get => _spillover ??= ReadSpilloverSetting();
        set
        {
            if (Spillover == value) return;
            _spillover = value;
            _config.UpdateRigConfig(c => c.RigSpillover = value);
            foreach (var chain in _chains.All) chain.Spillover = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     False while no engine can honour the switch (none attached — which is
    ///     also what an engine without the v37 entry points looks like, because
    ///     the ABI check refuses it). The menu item is then DISABLED and says why
    ///     (<see cref="SpilloverTip" />) rather than toggling something inert.
    /// </summary>
    public bool CanToggleSpillover => _dsp.SupportsSpillover;

    /// <summary>What the switch does, or why it cannot be used right now.</summary>
    public string SpilloverTip => _loc[
        CanToggleSpillover ? "Rig_Spillover_Tip" : "Rig_Spillover_NoEngine"];

    private bool ReadSpilloverSetting()
    {
        try { return _config.LoadRigConfig().RigSpillover; }
        catch (Exception) { return true; }
    }

    /// <summary>The engine came or went: the switch's availability follows it.</summary>
    private void RaiseSpilloverAvailability()
    {
        OnPropertyChanged(nameof(CanToggleSpillover));
        OnPropertyChanged(nameof(SpilloverTip));
    }
}
