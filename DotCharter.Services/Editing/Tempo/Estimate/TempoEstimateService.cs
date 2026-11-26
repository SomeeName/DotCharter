using System.Diagnostics;
using DotCharter.Analysis.BeatMap;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;

namespace DotCharter.Services.Editing.Tempo.Estimate;

/// <summary>Tempo map ▸ Estimate BPM: reads the song audio off the calling thread and never touches the project.</summary>
public interface ITempoEstimateService
{
    /// <summary>
    ///     Estimates the tempo of the whole song (<paramref name="startMs" /> and <paramref name="endMs" /> both
    ///     null) or of the part between two times. Cancellable; throws <see cref="OperationCanceledException" />.
    /// </summary>
    Task<TempoEstimateOutcome> EstimateAsync(string? audioPath, double? startMs, double? endMs, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITempoEstimateService" />
/// <remarks>A whole-song estimate is also handed to <paramref name="store" />, where validation reads it.</remarks>
public sealed class TempoEstimateService(IAudioTempoEstimateStore store, IAppLogger? logger = null) : ITempoEstimateService
{
    /// <summary>A part shorter than this holds too few beats to tell a tempo from noise.</summary>
    public const double MinSeconds = 4.0;

    public Task<TempoEstimateOutcome> EstimateAsync(string? audioPath, double? startMs, double? endMs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return Task.FromResult(new TempoEstimateOutcome(null, TempoEstimateFailure.NoAudio));
        return Task.Run(() => Core(audioPath, startMs, endMs, cancellationToken), cancellationToken);
    }

    private TempoEstimateOutcome Core(string audioPath, double? startMs, double? endMs, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        float[] mono;
        int rate;
        try
        {
            // The mix, not the stems: the drums are what carry a tempo.
            (mono, rate) = RecordingAlignmentAudio.DecodeMix(audioPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.Warning(AppLogCategory.Audio, "Estimate BPM: decode failed", ex);
            return new TempoEstimateOutcome(null, TempoEstimateFailure.DecodeFailed);
        }

        ct.ThrowIfCancellationRequested();
        var estimate = EstimateSamples(mono, rate, startMs, endMs, ct);
        logger?.Info(AppLogCategory.Audio,
            $"Estimate BPM: {(estimate == null ? "none" : $"{estimate.Bpm:0.00}")} from {startMs ?? 0:0}-{endMs?.ToString("0") ?? "end"} ms, " +
            $"took={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms.");
        if (estimate != null && startMs == null && endMs == null) store.Remember(audioPath, estimate);
        return estimate == null
            ? new TempoEstimateOutcome(null, TempoEstimateFailure.NoTempoFound)
            : new TempoEstimateOutcome(estimate, TempoEstimateFailure.None);
    }

    /// <summary>The pure core: cut the part out of the samples and take the MEDIAN beat tempo (one mistracked beat cannot move it).</summary>
    public static TempoEstimate? EstimateSamples(float[] mono, int sampleRate, double? startMs, double? endMs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var from = Math.Clamp((int)((startMs ?? 0) / 1000.0 * sampleRate), 0, mono.Length);
        var to = endMs == null ? mono.Length : Math.Clamp((int)(endMs.Value / 1000.0 * sampleRate), from, mono.Length);
        if (to - from < MinSeconds * sampleRate) return null;

        ct.ThrowIfCancellationRequested();
        var result = new BeatMapAnalyzer().Analyze(mono.AsMemory(from, to - from), sampleRate, cancellationToken: ct);
        ct.ThrowIfCancellationRequested();
        if (result.Beats.Count < 4) return null;

        var tempos = result.Beats.Select(b => b.Bpm).Order().ToArray();
        var median = tempos.Length % 2 == 1 ? tempos[tempos.Length / 2] : (tempos[tempos.Length / 2 - 1] + tempos[tempos.Length / 2]) / 2;
        return TempoEstimate.From(median, startMs ?? 0, result.Beats.Count);
    }
}
