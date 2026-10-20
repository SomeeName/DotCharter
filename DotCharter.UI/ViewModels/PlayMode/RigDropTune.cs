using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The rig footer's drop-tune pedal in DotCharter: the TRANSPORT's own
///     <see cref="TransportViewModel.InputPitchShiftSemis" />, so the footer, the
///     transport's picker and a song that sets its own drop tune all edit one value
///     (per seat, persisted, replayed on an engine rebuild — all the transport's).
/// </summary>
public static class RigDropTune
{
    /// <summary>The pedal over <paramref name="transport" />, or null without one.
    /// The rig screen lives as long as the shell, so the subscription does too.</summary>
    public static RigDropTuneViewModel? Over(TransportViewModel? transport)
    {
        if (transport is null) return null;
        var pedal = new RigDropTuneViewModel(
            () => transport.InputPitchShiftSemis,
            value => transport.InputPitchShiftSemis = value);
        transport.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TransportViewModel.InputPitchShiftSemis)) pedal.Refresh();
        };
        return pedal;
    }
}
