using JuceSharp;

namespace DotCharter.Tone.Audio.ChartSynth;

/// <summary>The chart-source writes, independently injectable from device creation.</summary>
internal interface IChartSynthBackend
{
    object Identity { get; }

    /// <summary>The engine still has its native handle: a refused write on a torn-down
    /// engine is the teardown, not a refusal worth a warning.</summary>
    bool IsLive { get; }
    void SetChartNotes(int player, JuceChartNote[] notes, JuceChartBendPoint[] bends);
    bool TrySetChartVoice(int player, JuceChartVoice voice);
    bool TrySetChartInstrument(int player, JuceChartInstrument instrument);
    void SetChartSourceParams(int player, float gain, double offset, JuceChartSourceMode mode);
    void SetChartSourceEnabled(int player, bool enabled);
    bool TrySetInstrumentRouteMask(uint seatMask);
    bool TrySetChartRouteMask(int player, uint seatMask);
    bool TryGetChartSourceStatus(int player, out JuceChartSourceStatus status);
}

internal sealed class JuceChartSynthBackend(JuceAudioEngine engine) : IChartSynthBackend
{
    public object Identity => engine;
    public bool IsLive => engine.NativeHandle != nint.Zero;
    public void SetChartNotes(int player, JuceChartNote[] notes, JuceChartBendPoint[] bends) => engine.SetChartNotes(player, notes, bends);
    public bool TrySetChartVoice(int player, JuceChartVoice voice) => engine.TrySetChartVoice(player, voice);
    public bool TrySetChartInstrument(int player, JuceChartInstrument instrument) => engine.TrySetChartInstrument(player, instrument);
    public void SetChartSourceParams(int player, float gain, double offset, JuceChartSourceMode mode) => engine.SetChartSourceParams(player, gain, offset, mode);
    public void SetChartSourceEnabled(int player, bool enabled) => engine.SetChartSourceEnabled(player, enabled);
    public bool TrySetInstrumentRouteMask(uint seatMask) => engine.TrySetInstrumentRouteMask(seatMask);
    public bool TrySetChartRouteMask(int player, uint seatMask) => engine.TrySetChartRouteMask(player, seatMask);
    public bool TryGetChartSourceStatus(int player, out JuceChartSourceStatus status) => engine.TryGetChartSourceStatus(player, out status);
}
