using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.StemSeparation.Gpu;

/// <summary>
///     What the machine offers stem separation: whether the loaded ONNX Runtime has
///     DirectML, and the graphics adapters. A seam so the decision, the settings rows
///     and their tests run on an injected adapter list with no GPU present.
/// </summary>
public interface IStemGpuEnvironment
{
    /// <summary>
    ///     True when the loaded onnxruntime.dll reports the DirectML provider. The first
    ///     call initialises the native ONNX Runtime environment (~50 ms), so it is never
    ///     made at construction.
    /// </summary>
    bool IsDirectMlAvailable { get; }

    /// <summary>Every DXGI adapter in device-id order, the software one included. Re-read on every call (an eGPU can come and go); cheap, creates no device.</summary>
    IReadOnlyList<StemGpuAdapter> Adapters();
}

/// <summary>The real environment: ONNX Runtime's provider list and DXGI's adapter list, both through JuceSharp.</summary>
public sealed class OnnxStemGpuEnvironment : IStemGpuEnvironment
{
    public bool IsDirectMlAvailable => OnnxSessionFactory.AvailableNativeProviders
        .Any(p => string.Equals(p, "DmlExecutionProvider", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     <see cref="DxgiAdapterProbe" />, which also says which adapter drives a display;
    ///     JuceSharp's list (no display information) only when that probe finds nothing.
    /// </summary>
    public IReadOnlyList<StemGpuAdapter> Adapters()
    {
        var probed = DxgiAdapterProbe.Enumerate();
        return probed.Count > 0
            ? probed
            : [.. OnnxGpuAdapters.Enumerate().Select(a =>
                new StemGpuAdapter(a.DeviceId, a.Name, a.DedicatedVideoMemoryBytes, a.IsSoftware))];
    }
}
