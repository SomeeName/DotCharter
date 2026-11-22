using DotCharter.Analysis.BeatMap;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>Where a seconds-only alignment got the beat grid its spacing ran on.</summary>
public enum LyricSyncGridSource
{
    /// <summary>No grid: the fixed <see cref="LyricSyncSpacing.FallbackGapMilliseconds" /> gap.</summary>
    None,

    /// <summary>A constant tempo the caller named (the CLI's <c>--bpm</c>).</summary>
    Tempo,

    /// <summary>Tracked from the audio by <see cref="BeatMapAnalyzer" />.</summary>
    Detected
}

/// <summary>A beat grid for <see cref="LyricSyncSpacing.Apply" /> and where it came from.</summary>
/// <param name="Beats">Times in milliseconds of the audio file; empty for <see cref="LyricSyncGridSource.None" />.</param>
/// <param name="Source">How the grid was obtained.</param>
public sealed record LyricSyncBeatGrid(IList<BeatMarker> Beats, LyricSyncGridSource Source)
{
    /// <summary>No grid at all: spacing falls back to the fixed gap.</summary>
    public static LyricSyncBeatGrid None { get; } = new([], LyricSyncGridSource.None);

    /// <summary>
    ///     A constant tempo. <paramref name="offsetSeconds" /> is a DOWNBEAT; the grid
    ///     extends backwards from it to cover the start of the audio and forwards to
    ///     <paramref name="durationSeconds" />, past which the timeline extrapolates.
    /// </summary>
    public static LyricSyncBeatGrid FromTempo(double bpm, double offsetSeconds, int beatsPerMeasure, double durationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bpm);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beatsPerMeasure);
        var beatMs = 60_000.0 / bpm;
        var offsetMs = offsetSeconds * 1000.0;
        var first = -(int)Math.Ceiling(Math.Max(0.0, offsetMs) / beatMs);
        var last = (int)Math.Ceiling((Math.Max(durationSeconds, offsetSeconds) * 1000.0 - offsetMs) / beatMs) + 1;
        var beats = new List<BeatMarker>(last - first + 1);
        for (var n = first; n <= last; n++)
        {
            beats.Add(new BeatMarker
            {
                Time = offsetMs + n * beatMs,
                Bpm = bpm,
                BeatsInMeasure = beatsPerMeasure,
                IsMeasure = ((n % beatsPerMeasure) + beatsPerMeasure) % beatsPerMeasure == 0
            });
        }

        return new LyricSyncBeatGrid(beats, LyricSyncGridSource.Tempo);
    }

    /// <summary>
    ///     The tracker's beats as a grid; <see cref="None" /> when it found fewer than
    ///     two, which is no tempo at all.
    /// </summary>
    public static LyricSyncBeatGrid FromDetected(IReadOnlyList<DetectedBeat> detected, int beatsPerMeasure = 4)
    {
        ArgumentNullException.ThrowIfNull(detected);
        if (detected.Count < 2) return None;
        return new LyricSyncBeatGrid(
            [.. detected.Select(b => new BeatMarker
            {
                Time = b.TimeSeconds * 1000.0,
                Bpm = b.Bpm,
                BeatsInMeasure = beatsPerMeasure,
                IsMeasure = b.IsDownbeat
            })],
            LyricSyncGridSource.Detected);
    }

    /// <summary>
    ///     Tracks the beats of <paramref name="audioPath" /> with the app's own
    ///     <see cref="BeatMapAnalyzer" />, decoded by the same loader the aligner uses.
    ///     Throws when the audio cannot be decoded; the caller decides what that means.
    /// </summary>
    public static LyricSyncBeatGrid Detect(string audioPath, CancellationToken cancellationToken = default)
    {
        var mono = LyricAudioSource.LoadMono16k(audioPath);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new BeatMapAnalyzer().Analyze(mono, LyricAcousticModel.RequiredSampleRate,
            cancellationToken: cancellationToken);
        return FromDetected(result.Beats);
    }

    /// <summary>Average tempo over the grid, for the report; 0 without one.</summary>
    public double AverageBpm => Beats.Count < 2 ? 0.0 : 60_000.0 * (Beats.Count - 1) / (Beats[^1].Time - Beats[0].Time);
}

/// <summary>
///     The app's lyric fold for a caller with no chart: the standalone CLI.
///     <para>
///         <b>Not a second implementation of the spacing rule.</b> It runs
///         <see cref="LyricAutoSyncService.BuildPhrases" /> itself — the measured
///         ends, <see cref="Analysis.Lyrics.LyricAlignmentReview" />'s review flags and
///         <see cref="Editing.Lyrics.LyricPhraseSpacing" /> — on the caller's beat
///         grid, then reads the syllables back in seconds. With no grid the same fold
///         runs on a dense neutral grid with the gap set to a flat
///         <see cref="FallbackGapMilliseconds" />, so the fallback differs from the app
///         in the size of the gap only, never in the rule.
///     </para>
/// </summary>
public static class LyricSyncSpacing
{
    /// <summary>The documented gap when no beat grid is available.</summary>
    public const double FallbackGapMilliseconds = 30.0;

    /// <summary>One neutral beat every 10 ms: 1/192 of it is ~0.05 ms, finer than any output format.</summary>
    private const double NeutralBeatMs = 10.0;

    /// <summary>
    ///     Spaces <paramref name="result" />'s syllables and attaches the review flags.
    ///     Text, structure and confidence come from the seam's table; start, end and
    ///     the added review reasons come from the fold.
    /// </summary>
    /// <param name="result">A successful alignment.</param>
    /// <param name="grid">The beat grid, or <see cref="LyricSyncBeatGrid.None" />.</param>
    /// <param name="options">Span switches and spacing config; the config only applies with a grid.</param>
    public static IReadOnlyList<LyricSyncSyllable> Apply(
        LyricSyncResult result, LyricSyncBeatGrid grid, LyricAutoSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(options);
        if (result.Syllables.Count == 0) return [];

        var hasGrid = grid.Beats.Count >= 2;
        var beats = hasGrid ? grid.Beats : NeutralGrid(result, options);
        var foldOptions = hasGrid ? options : options with { SpacingConfig = FallbackConfig };
        var lyrics = LyricAutoSyncService.BuildPhrases(result.Sheet, result.Aligned, beats, foldOptions, result.Envelope)
            .SelectMany(p => p.Lyrics).ToList();

        return [.. result.Syllables.Select((s, i) => s with
        {
            StartSeconds = lyrics[i].Position.GetPositionMs(beats) / 1000.0,
            EndSeconds = lyrics[i].EndPosition.GetPositionMs(beats) / 1000.0,
            ReviewReasons = s.ReviewReasons | lyrics[i].ReviewReasons
        })];
    }

    private static readonly AppConfig FallbackConfig = new()
    {
        MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds,
        MinNoteDistanceMs = FallbackGapMilliseconds
    };

    private static List<BeatMarker> NeutralGrid(LyricSyncResult result, LyricAutoSyncOptions options)
    {
        var lastSeconds = Math.Max(result.AudioSeconds, result.Aligned.Count == 0 ? 0.0 : result.Aligned.Max(a => a.EndSeconds));
        var count = (int)Math.Ceiling((lastSeconds + options.MaxSyllableSeconds + 1.0) * 1000.0 / NeutralBeatMs) + 2;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker
        {
            Time = i * NeutralBeatMs,
            Bpm = 60_000.0 / NeutralBeatMs,
            IsMeasure = i % 4 == 0
        })];
    }
}
