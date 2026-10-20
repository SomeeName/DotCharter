namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
/// Minimal MIDI output sink for metronome ticks and chart-driven instrument
/// playback. Phase-2 contract only — implementation arrives in Phase 6.
/// </summary>
public interface IMidiOutput : IDisposable
{
    bool IsOpen { get; }

    void Open();
    void Close();
    void NoteOn(int channel, int note, int velocity);
    void NoteOff(int channel, int note);
    void ProgramChange(int channel, int program);

    /// <summary>
    /// Send a Channel Volume controller (CC 7) message. Lets the host
    /// scale the per-channel synth output between 0 (silent) and 127
    /// (full) without touching the global Windows mixer. For making
    /// the in-app master / instrument sliders affect the WinMM /
    /// Microsoft GS Wavetable fallback path the same way they affect a
    /// pipeline-routed VSTi.
    /// </summary>
    void ChannelVolume(int channel, int level);

    /// <summary>
    /// Hard-silence all sounding voices on the given channel without
    /// honouring release tails. Used when toggling instruments,
    /// scrubbing, or muting via the mixer.
    /// </summary>
    void AllSoundOff(int channel);
}
