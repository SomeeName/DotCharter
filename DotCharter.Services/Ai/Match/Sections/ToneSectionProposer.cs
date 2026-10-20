using DotCharter.Localization;
using System.Globalization;

namespace DotCharter.Services.Ai.Match.Sections;

/// <summary>The proposer's knobs. The defaults are the shipping configuration.</summary>
/// <param name="Window">How much audio one feature vector is taken over. Four seconds
/// is <see cref="ToneSection.MinDuration" />: shorter and the extractor is describing
/// a chord, longer and a boundary is smeared over the window.</param>
/// <param name="Hop">How far the window moves, and therefore the resolution of every
/// boundary this can report. One second against a ±2 s agreement window.</param>
/// <param name="PeakFactor">See <see cref="SectionNovelty.DefaultPeakFactor" />.</param>
/// <param name="MinNovelty">See <see cref="SectionNovelty.DefaultMinNovelty" />.</param>
/// <param name="ChartAgreement">How far apart a chart boundary and an audio peak may
/// sit and still be the same boundary. ±2 s: twice the hop, so a real agreement is not
/// lost to the scan's own resolution, and short enough that two DIFFERENT changes in a
/// busy song are not merged.</param>
/// <param name="LabelGain">Whether <see cref="SectionGainHeuristic" /> may append
/// "(clean)" / "(driven)" to an audio-derived label.</param>
public sealed record ToneSectionProposerOptions(
    TimeSpan? Window = null,
    TimeSpan? Hop = null,
    double PeakFactor = SectionNovelty.DefaultPeakFactor,
    double MinNovelty = SectionNovelty.DefaultMinNovelty,
    TimeSpan? ChartAgreement = null,
    bool LabelGain = true)
{
    /// <summary>The window actually used.</summary>
    public TimeSpan ResolvedWindow => Window ?? ToneSection.MinDuration;

    /// <summary>The hop actually used.</summary>
    public TimeSpan ResolvedHop => Hop ?? TimeSpan.FromSeconds(1);

    /// <summary>The agreement radius actually used.</summary>
    public TimeSpan ResolvedChartAgreement => ChartAgreement ?? TimeSpan.FromSeconds(2);
}

/// <summary>
///     Where a song's parts are: the chart's tone changes when there is a chart, and
///     timbre changes in the isolated stem always.
///     <para>
///         <b>The method is a self-similarity novelty curve, the standard music
///         structure analysis shape, run over the TONE embedding rather than over
///         chroma.</b> Two windows of the stem, four seconds each and exactly
///         adjacent, are embedded with the same <c>IToneFeatureExtractor</c> the
///         matcher scores with and compared with the same <c>IToneSimilarity</c>. The
///         distance between them is the novelty at their join. Sliding that join
///         through the song in one-second steps gives a curve whose peaks are the
///         moments the TONE changed — and because the embedding is content-invariant
///         by construction, a key change, a new riff or a different singer do not
///         move it while a pedal going on does.
///     </para>
///     <para>
///         <b>Using the matcher's own metric is the whole design, not a convenience.</b>
///         A boundary this finds is by definition a place where the thing that is
///         about to be matched is measurably different — so every section it proposes
///         is one the matcher can actually tell apart. A segmenter with its own
///         private notion of similarity would happily split a song where the matcher
///         cannot hear a difference, and the user would get two rigs that are the
///         same rig.
///     </para>
///     <para>
///         <b>The chart wins wherever it speaks.</b> A charter dropped a tone change
///         where the tone changes; that is ground truth and it comes back at
///         confidence 1. The audio's job next to a chart is to AGREE (which promotes
///         the boundary to <see cref="ToneSectionOrigins.Both" />) or to find the one
///         the charter did not place. It never moves or removes a charted boundary.
///     </para>
///     <para>
///         Deterministic: same audio and same chart in, same proposals out, on every
///         machine. Cache-first — one separation per song, ever, through
///         <see cref="IStemIsolator" />. Never called on the audio thread.
///     </para>
/// </summary>
public sealed class ToneSectionProposer : IToneSectionProposer
{
    private readonly IStemIsolator _isolator;
    private readonly IToneSimilarity _similarity;
    private readonly ILocalizationService _localization;
    private readonly ToneSectionProposerOptions _options;

    /// <summary>Builds a proposer over the isolator and the app's one metric.</summary>
    /// <param name="isolator">Cache-first stem isolation.</param>
    /// <param name="similarity">The metric the matcher scores with — the extractor
    /// comes from it, so the segmenter and the matcher cannot drift apart.</param>
    /// <param name="localization">Resolves the status and progress text the user reads.</param>
    /// <param name="options">Null for the shipping configuration.</param>
    public ToneSectionProposer(
        IStemIsolator isolator, IToneSimilarity similarity, ILocalizationService localization, ToneSectionProposerOptions? options = null)
    {
        _isolator = isolator ?? throw new ArgumentNullException(nameof(isolator));
        _similarity = similarity ?? throw new ArgumentNullException(nameof(similarity));
        _localization = localization;
        _options = options ?? new ToneSectionProposerOptions();
    }

    /// <summary>
    ///     A span long enough to mean "all of it" and short enough that turning it
    ///     into a sample index cannot overflow: two hours at 192 kHz is 1.38e9
    ///     samples, well inside <see cref="int" />. The contract asks for a from/to,
    ///     and "the whole stem" has to be expressible in it.
    /// </summary>
    public static TimeSpan WholeSong { get; } = TimeSpan.FromHours(2);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToneSectionProposal>> ProposeAsync(
        string songAudioPath,
        string stem,
        IReadOnlyList<ToneSection> chartSpans,
        IProgress<string>? status,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);
        if (!ToneStems.All.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{stem}' is not a stem. Known stems: {string.Join(", ", ToneStems.All)}.", nameof(stem));
        }

        var chart = (chartSpans ?? [])
            .Where(static s => s is not null)
            .OrderBy(static s => s.From)
            .ToList();

        MonoAudio? audio = null;
        IReadOnlyList<SectionNovelty.NoveltyPeak> peaks = [];
        try
        {
            status?.Report(_localization["Progress_IsolatingStem"]);
            audio = await _isolator
                .IsolateAsync(songAudioPath, stem, TimeSpan.Zero, WholeSong, status, ct)
                .ConfigureAwait(false);

            var curve = BuildCurve(audio, status, ct);
            peaks = SectionNovelty.PickPeaks(
                curve, ToneSection.MinDuration, _options.PeakFactor, _options.MinNovelty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (chart.Count > 0)
        {
            // A chart is an answer on its own. Losing it because the separator has no
            // model installed would be the worst of both: the user has the ground
            // truth in front of them and the app claims it knows nothing.
            status?.Report(
                _localization.Format("Progress_ScanFailedChartOnly", ex.Message));
        }

        var end = EndOfSong(audio, chart);
        var boundaries = Merge(chart, peaks);
        return Build(boundaries, end, stem, audio);
    }

    /// <summary>
    ///     The novelty curve: adjacent, non-overlapping window pairs, scored with the
    ///     matcher's own metric. Empty when the stem is shorter than two windows —
    ///     which reads as "one part", not as "an error".
    /// </summary>
    private IReadOnlyList<NoveltySample> BuildCurve(
        MonoAudio audio, IProgress<string>? status, CancellationToken ct)
    {
        var rate = audio.SampleRate;
        var window = (int)Math.Round(_options.ResolvedWindow.TotalSeconds * rate);
        var hop = (int)Math.Round(_options.ResolvedHop.TotalSeconds * rate);
        if (rate <= 0 || window <= 0 || hop <= 0 || audio.Samples.Length < 2 * window) return [];

        var starts = new List<int>();
        for (var start = 0; start + window <= audio.Samples.Length; start += hop) starts.Add(start);

        // The pair compared is (i, i + lag), and lag is chosen so the two windows are
        // EXACTLY adjacent: overlapping windows blur a boundary into a ramp, and
        // leaving a gap between them measures a change that may have happened in it.
        var lag = Math.Max(1, (int)Math.Round(
            _options.ResolvedWindow.TotalSeconds / _options.ResolvedHop.TotalSeconds));
        if (starts.Count <= lag) return [];

        var extractor = _similarity.Extractor;
        var features = new ToneFeatures[starts.Count];
        for (var i = 0; i < starts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var at = TimeSpan.FromSeconds(starts[i] / (double)rate);
            status?.Report(_localization.Format("Progress_ScanningAt", SectionClock.Format(at)));
            features[i] = extractor.Extract(Slice(audio, starts[i], window));
        }

        var curve = new List<NoveltySample>(starts.Count - lag);
        for (var i = 0; i + lag < starts.Count; i++)
        {
            curve.Add(new NoveltySample(
                TimeSpan.FromSeconds((starts[i] + window) / (double)rate),
                _similarity.Distance(features[i], features[i + lag])));
        }

        return curve;
    }

    /// <summary>One boundary in the merged set: when it is, where it came from, how
    /// sure we are, and the charter's word for what starts there.</summary>
    private readonly record struct Boundary(TimeSpan Time, string Origin, double Confidence, string? Label);

    /// <summary>
    ///     Chart boundaries and audio peaks folded into one ordered set.
    ///     <para>
    ///         A chart boundary an audio peak agrees with is promoted to
    ///         <see cref="ToneSectionOrigins.Both" /> and that peak is CONSUMED — it
    ///         does not go on to open a second section a couple of seconds away from
    ///         the charted one. An audio peak too close to a kept boundary is dropped
    ///         for the same reason; a CHART boundary never is, however close it sits,
    ///         because the charter meant it.
    ///     </para>
    /// </summary>
    private List<Boundary> Merge(
        IReadOnlyList<ToneSection> chart, IReadOnlyList<SectionNovelty.NoveltyPeak> peaks)
    {
        var agreement = _options.ResolvedChartAgreement;
        var merged = new List<Boundary>(chart.Count + peaks.Count);

        foreach (var span in chart)
        {
            var agrees = peaks.Any(p => (p.Time - span.From).Duration() <= agreement);
            merged.Add(new Boundary(
                span.From,
                agrees ? ToneSectionOrigins.Both : ToneSectionOrigins.Chart,
                1.0,
                span.Label));
        }

        foreach (var peak in peaks)
        {
            if (chart.Any(s => (s.From - peak.Time).Duration() <= agreement)) continue;
            merged.Add(new Boundary(peak.Time, ToneSectionOrigins.Audio, peak.Confidence, null));
        }

        merged.Sort(static (a, b) => a.Time.CompareTo(b.Time));

        var kept = new List<Boundary>(merged.Count);
        foreach (var boundary in merged)
        {
            if (kept.Count > 0 &&
                boundary.Origin == ToneSectionOrigins.Audio &&
                boundary.Time - kept[^1].Time < ToneSection.MinDuration)
            {
                continue;
            }

            kept.Add(boundary);
        }

        return kept;
    }

    /// <summary>
    ///     Boundaries into sections.
    ///     <para>
    ///         <b>The song's first part is not opened by a detected change</b> — it is
    ///         opened by the song starting — so when nothing sits at 0 an opening
    ///         boundary is synthesised, and its evidence (origin and confidence alike)
    ///         is the change that ENDS it: that a part ends is exactly what makes it a
    ///         part, and whatever found that change is what found this one. When there is no such
    ///         change either, the whole song is one section at confidence 1, which is
    ///         not a claim about a boundary but the plain statement "this song has one
    ///         tone", and is the same answer match-from-audio gives today.
    ///     </para>
    /// </summary>
    private List<ToneSectionProposal> Build(
        List<Boundary> boundaries, TimeSpan end, string stem, MonoAudio? audio)
    {
        if (boundaries.Count == 0 || boundaries[0].Time > TimeSpan.Zero)
        {
            var opener = boundaries.Count > 0
                ? boundaries[0] with { Time = TimeSpan.Zero, Label = null }
                : new Boundary(TimeSpan.Zero, ToneSectionOrigins.Audio, 1.0, null);
            boundaries.Insert(0, opener);
        }

        var proposals = new List<ToneSectionProposal>(boundaries.Count);
        for (var i = 0; i < boundaries.Count; i++)
        {
            var from = boundaries[i].Time;
            var to = i + 1 < boundaries.Count ? boundaries[i + 1].Time : end;
            if (to <= from) continue;

            // A too-short trailing part is not a section: it is the tail of the one
            // before it. Only an AUDIO boundary may be dissolved that way.
            if (to - from < ToneSection.MinDuration &&
                boundaries[i].Origin == ToneSectionOrigins.Audio &&
                proposals.Count > 0)
            {
                continue;
            }

            var label = boundaries[i].Label ?? AudioLabel(proposals.Count + 1, audio, from, to);
            proposals.Add(new ToneSectionProposal(
                new ToneSection($"section-{proposals.Count + 1:00}", label, stem, from, to),
                boundaries[i].Confidence,
                boundaries[i].Origin));
        }

        return proposals;
    }

    private string AudioLabel(int number, MonoAudio? audio, TimeSpan from, TimeSpan to)
    {
        var label = string.Create(CultureInfo.InvariantCulture, $"Part {number}");
        if (!_options.LabelGain || audio is null) return label;

        var rate = audio.SampleRate;
        var start = Math.Clamp((int)(from.TotalSeconds * rate), 0, audio.Samples.Length);
        var stop = Math.Clamp((int)(to.TotalSeconds * rate), start, audio.Samples.Length);
        // Measure the head of the part, capped: a long section's gain does not change,
        // and a whole four-minute filter pass to choose one word is not worth it.
        var count = Math.Min(stop - start, (int)(GainWindowSeconds * rate));
        if (count <= 0) return label;

        return label + (SectionGainHeuristic.Suffix(Slice(audio, start, count)) ?? string.Empty);
    }

    /// <summary>How much of a part the gain heuristic looks at.</summary>
    private const double GainWindowSeconds = 8.0;

    private static TimeSpan EndOfSong(MonoAudio? audio, IReadOnlyList<ToneSection> chart)
    {
        var end = audio?.Duration ?? TimeSpan.Zero;
        foreach (var span in chart)
        {
            if (span.To > end)
                end = span.To;
        }

        return end;
    }

    private static MonoAudio Slice(MonoAudio audio, int start, int count) =>
        new(audio.Samples.AsSpan(start, count).ToArray(), audio.SampleRate);
}
