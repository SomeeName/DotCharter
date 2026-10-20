namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
/// One separated stem produced by a <see cref="IStemSeparationService"/>.
/// </summary>
public sealed class StemTrack
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
}

/// <summary>
/// Outcome of a stem-separation run.
/// </summary>
public sealed class StemSeparationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
    public IReadOnlyList<StemTrack> Stems { get; init; } = Array.Empty<StemTrack>();

    public static StemSeparationResult Failure(string message) => new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// Splits a mixed audio file into per-instrument stems (drums, bass, vocals,
/// other). Implementations delegate to an external model — the initial
/// implementation shells out to <c>demucs</c> (Facebook Research / META).
///
/// The service is intentionally CPU/GPU-agnostic: callers pass an input
/// file and an output directory; the implementation chooses how to do
/// the work and writes stem WAV files there.
/// </summary>
public interface IStemSeparationService
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<string> DescribeBackendAsync(CancellationToken ct = default);

    Task<StemSeparationResult> SeparateAsync(
        string inputAudioPath,
        string outputDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
