using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.StemSeparation.Onnx;

/// <summary>One opened ONNX stem separator, as <see cref="OnnxStemSeparationService" /> uses it.</summary>
internal interface IOnnxStemRun : IDisposable
{
    OnnxExecutionProvider ActiveExecutionProvider { get; }

    string BackendDescription { get; }

    string? FallbackReason { get; }

    Task<IReadOnlyList<OnnxStemBuffer>> SeparateAsync(
        float[] interleavedStereo, int sampleRate, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Opens a separator for one provider; the seam tests replace.</summary>
internal delegate IOnnxStemRun OnnxStemRunFactory(
    string modelPath, OnnxStemModelKind kind, OnnxExecutionProvider provider,
    Action<string> log, int intraOpThreads, int deviceId);

/// <summary>The real separator behind <see cref="IOnnxStemRun" />.</summary>
internal sealed class OnnxStemRun(OnnxStemSeparator inner) : IOnnxStemRun
{
    public OnnxExecutionProvider ActiveExecutionProvider => inner.ActiveExecutionProvider;

    public string BackendDescription => inner.BackendDescription;

    public string? FallbackReason => inner.FallbackReason;

    public Task<IReadOnlyList<OnnxStemBuffer>> SeparateAsync(
        float[] interleavedStereo, int sampleRate, IProgress<double>? progress, CancellationToken ct) =>
        inner.SeparateAsync(interleavedStereo, sampleRate, progress, ct);

    public void Dispose() => inner.Dispose();

    public static IOnnxStemRun Open(
        string modelPath, OnnxStemModelKind kind, OnnxExecutionProvider provider,
        Action<string> log, int intraOpThreads, int deviceId) =>
        new OnnxStemRun(new OnnxStemSeparator(modelPath, kind, provider, log, intraOpThreads, deviceId));
}
