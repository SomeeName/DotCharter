using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Spectrum;

/// <summary>
///     Renders a buffer through a rig OFFLINE, on an engine of its own — never the
///     live chain, whose module state (delay lines, envelopes) a render would
///     disturb. The tone lab's node spectrum uses it to hear the chain cut short
///     before and after one unit. DotCharter implements it over the
///     null-device rig host the tone loudness meter already uses; a host without
///     one (the standalone rig) passes none, and the spectrum says so.
/// </summary>
public interface IOfflineToneRenderer
{
    /// <summary>Renders <paramref name="left" /> / <paramref name="right" /> through
    /// <paramref name="rig" /> IN PLACE. False when the renderer is unavailable or
    /// the render failed — never silence that reads as a real result.</summary>
    bool TryRender(DcRigPreset rig, float[] left, float[] right, int sampleRate);
}
