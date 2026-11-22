using System.Diagnostics;
using DotCharter.Analysis.Tuning;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.Services.State;

namespace DotCharter.Services.Audio.Tuning;

/// <summary>Why a reference-pitch check has no measurement.</summary>
public enum ReferencePitchFailure
{
    None,
    NoAudio,
    DecodeFailed
}

/// <summary>
///     One reference-pitch check in plain numbers, so a screen formats it without reaching into
///     <c>DotCharter.Analysis</c>: the measured residual and its A4, the confidence, the authored
///     offset, the suggestion that keeps the authored semitones, and whether to offer it.
/// </summary>
public sealed record ReferencePitchCheck(double MeasuredCents, double ReferenceHz, double Confidence, bool IsConfident,
    double AuthoredCentOffset, double SuggestedCentOffset, int AuthoredSemitones, double AuthoredResidual, bool ShouldOffer,
    RecordingAlignmentSource Source, ReferencePitchFailure Failure)
{
    public static ReferencePitchCheck From(ReferencePitchEstimate estimate, double authoredCentOffset,
        RecordingAlignmentSource source, ReferencePitchFailure failure = ReferencePitchFailure.None)
    {
        var c = ReferencePitchComparison.Compare(authoredCentOffset, estimate);
        return new ReferencePitchCheck(estimate.Cents, estimate.ReferenceHz, estimate.Confidence,
            estimate.Confidence >= ReferencePitchEstimator.DefaultMinConfidence, authoredCentOffset, c.SuggestedCentOffset,
            (int)Math.Round(authoredCentOffset / 100.0, MidpointRounding.AwayFromZero),
            ReferencePitchEstimator.FoldToSemitone(authoredCentOffset), c.ShouldOffer, source, failure);
    }

    /// <summary>The authored offset with its residual moved onto <paramref name="measuredCents" />, its semitones kept.</summary>
    public static double Suggest(double authoredCentOffset, double measuredCents) =>
        ReferencePitchComparison.Compare(authoredCentOffset, new ReferencePitchEstimate(measuredCents, 1, 0, 0)).SuggestedCentOffset;

    public static ReferencePitchCheck Failed(ReferencePitchFailure failure, double authoredCentOffset) =>
        From(ReferencePitchEstimate.None, authoredCentOffset, RecordingAlignmentSource.Mix, failure);
}

/// <summary>Measures the open song's A440 reference offset against an authored <c>CentOffset</c>.</summary>
public interface IReferencePitchService
{
    Task<ReferencePitchCheck> MeasureAsync(bool bass, double authoredCentOffset, CancellationToken cancellationToken);
}

/// <summary>
///     Listens to the instrument's own stem when one is already on disk (the bass stem for a bass
///     arrangement, the guitar stem otherwise), else to the full mix; it never starts a separation.
///     Opus stems are accepted as they are for Align to recording: this is an offline measurement,
///     not live note detection. The last measurement per file is kept, so the post-import offer and
///     the dialog do not decode the same song twice.
/// </summary>
public sealed class ReferencePitchService(IPlaybackTransport playback, ProjectState state, IAppLogger logger) : IReferencePitchService
{
    private readonly Lock _gate = new();
    private (string Path, ReferencePitchEstimate Estimate)? _last;

    public async Task<ReferencePitchCheck> MeasureAsync(bool bass, double authoredCentOffset, CancellationToken cancellationToken)
    {
        var mixPath = playback.CurrentAudioPath;
        if (string.IsNullOrWhiteSpace(mixPath) || !File.Exists(mixPath))
            return ReferencePitchCheck.Failed(ReferencePitchFailure.NoAudio, authoredCentOffset);

        var project = state.ActiveProject;
        var declared = project?.Stems.Select(s => new Stem { Name = s.Name, Path = s.Path, Offset = s.Offset }).ToArray() ?? [];
        var projectPath = state.CurrentProjectPath;
        var audioFileName = project?.AudioFileName;
        return await Task.Run(() =>
        {
            var stems = RecordingAlignmentAudio.LocateInstrumentStems(mixPath, declared, projectPath, audioFileName, logger);
            var path = PickStem(stems, bass) ?? mixPath;
            var source = path == mixPath ? RecordingAlignmentSource.Mix : RecordingAlignmentSource.Stems;
            try
            {
                var estimate = Measure(path, cancellationToken);
                return ReferencePitchCheck.From(estimate, authoredCentOffset, source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warning(AppLogCategory.Audio, "Reference pitch check: decode failed", ex);
                return ReferencePitchCheck.Failed(ReferencePitchFailure.DecodeFailed, authoredCentOffset);
            }
        }, cancellationToken);
    }

    /// <summary>Pure: the stem named after the instrument (bass, else guitar); null when there is none.</summary>
    public static string? PickStem(IReadOnlyList<string> stems, bool bass)
    {
        var wanted = bass ? "bass" : "guitar";
        return stems.FirstOrDefault(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p).Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private ReferencePitchEstimate Measure(string path, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_last is { } last && last.Path == path) return last.Estimate;
        }

        var started = Stopwatch.GetTimestamp();
        var (mono, rate) = RecordingAlignmentAudio.DecodeMix(path);
        var estimate = ReferencePitchEstimator.Estimate(mono, rate, ct);
        logger.Info(AppLogCategory.Audio,
            $"Reference pitch check: {estimate.Cents:+0.0;-0.0} cents, confidence {estimate.Confidence:0.00}, " +
            $"{estimate.PeakCount} peaks over {estimate.AnalyzedSeconds:0} s of {Path.GetFileName(path)} " +
            $"in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
        lock (_gate) _last = (path, estimate);
        return estimate;
    }
}
