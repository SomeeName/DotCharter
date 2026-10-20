using DotCharter.Localization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     Measures how close a KNOWN rig gets to a real record — the "rig ceiling".
///     <para>
///         It is the one measurement the whole match feature is conditional on. If
///         the converted Rocksmith tone (a professional's approximation of the record,
///         on gear the app can actually play) already scores no better than an
///         arbitrary rig does, then no amount of search or training can do better
///         either: the gap would be the separation bleed, the mix, the mastering and
///         the reference DI, not the rig. Everything the search, data and model lanes
///         build is graded against the number this produces.
///     </para>
///     <para>
///         The shape is deliberately the SAME one a live "match from audio" will use:
///         isolate the guitar from the record, render each candidate rig over a
///         reference DI, and rank the candidates by
///         <see cref="IToneSimilarity" />. A baseline is just another candidate. That
///         is why this lives in product code rather than in the test project — the
///         eval drives it, and so will the UI.
///     </para>
/// </summary>
public sealed class RigCeilingProbe
{
    private readonly IStemIsolator _stems;
    private readonly IRigRenderer _renderer;
    private readonly IToneSimilarity _similarity;
    private readonly ILocalizationService _localization;

    /// <summary>Builds a probe over the three lanes' seams.</summary>
    public RigCeilingProbe(IStemIsolator stems, IRigRenderer renderer, IToneSimilarity similarity, ILocalizationService localization)
    {
        _stems = stems ?? throw new ArgumentNullException(nameof(stems));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _similarity = similarity ?? throw new ArgumentNullException(nameof(similarity));
        _localization = localization;
    }

    /// <summary>One named rig to score.</summary>
    public sealed record Candidate(string Label, DcRigPreset Rig);

    /// <summary>One rig's score, with the per-block breakdown behind it.</summary>
    public sealed record CandidateScore(
        string Label, double Distance, IReadOnlyList<(string Block, double Contribution)> Blocks, string? Error);

    /// <summary>The target and every candidate's distance to it.</summary>
    public sealed record Measurement(
        string SongAudioPath, TimeSpan From, TimeSpan To, double TargetSeconds,
        IReadOnlyList<CandidateScore> Scores);

    /// <summary>
    ///     Isolates the guitar over <paramref name="from" />..<paramref name="to" />,
    ///     renders every candidate over <paramref name="referenceDi" />, and returns
    ///     each one's distance to the record.
    ///     <para>
    ///         A candidate that throws is REPORTED, not skipped: a rig the renderer
    ///         refuses is a finding, and silently dropping it would quietly improve
    ///         whatever aggregate the row belonged to.
    ///     </para>
    /// </summary>
    public async Task<Measurement> MeasureAsync(
        string songAudioPath, TimeSpan from, TimeSpan to, MonoAudio referenceDi,
        IReadOnlyList<Candidate> candidates, IProgress<string>? status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(referenceDi);
        ArgumentNullException.ThrowIfNull(candidates);

        var target = await _stems.IsolateGuitarAsync(songAudioPath, from, to, status, ct).ConfigureAwait(false);
        var targetFeatures = _similarity.Extractor.Extract(target);

        var scores = new List<CandidateScore>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report(_localization.Format("Progress_Rendering", candidate.Label));
            try
            {
                var wet = await _renderer.RenderAsync(referenceDi, candidate.Rig, ct).ConfigureAwait(false);
                var wetFeatures = _similarity.Extractor.Extract(wet);
                var distance = _similarity.Distance(targetFeatures, wetFeatures);
                var blocks = _similarity is ToneSimilarity concrete
                    ? concrete.Explain(targetFeatures, wetFeatures)
                    : [];
                scores.Add(new CandidateScore(candidate.Label, distance, blocks, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scores.Add(new CandidateScore(candidate.Label, double.NaN, [], ex.Message));
            }
        }

        return new Measurement(songAudioPath, from, to, target.Duration.TotalSeconds, scores);
    }
}
