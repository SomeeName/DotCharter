namespace DotCharter.Tone.Audio.Dsp;

/// <summary>Real per-node amplitudes, linear and potentially greater than one.</summary>
public readonly record struct DspNodeMeter(float InputPeak, float InputRms, float OutputPeak, float OutputRms);

/// <summary>Optional polling capability. Each visible host owns one lease; dispose
/// it when hidden. Multiple hosts never disable one another's meters.</summary>
public interface IDspMeterSource
{
    IDisposable AcquireMetering();
    bool IsMeteringActive { get; }
    bool TryGetNodeMeter(int nodeId, out DspNodeMeter meter);
}

internal interface IDspMeterNativeInterop
{
    bool SetMetering(bool enabled);
    bool GetMetering();
    bool TryGetNodeMeter(int nodeId, out DspNodeMeter meter);
}
