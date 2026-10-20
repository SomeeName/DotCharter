using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotCharter.Analysis.Tone;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Audio.StemSeparation.Onnx;
using DotCharter.Tests.Ai.Eval;
using DotCharter.Tone.Audio.Dsp.Tones;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     <b>The rig ceiling.</b> For every corpus song: the guitar stem over the tone's
///     charted span, against the song's own Rocksmith tone — converted by the app's
///     own <see cref="RsToneConverter" /> and rendered over a reference DI.
///     <para>
///         That number is the one the whole "match a tone from audio" feature is
///         conditional on. It is the distance a KNOWN-CORRECT rig achieves. No search,
///         no trained estimator and no amount of knob refinement can do better than
///         the rig that a professional chose for this song on gear the app can play —
///         so if the ceiling is not clearly better than an information-free rig, the
///         remaining distance is separation bleed, the mix, mastering and the
///         reference DI, and the honest answer is that the feature cannot work as
///         specified.
///     </para>
///     <para>
///         The existing audio-to-tone baseline (<see cref="ToneFromAudioBuilder" />,
///         which the app already ships) is scored on the same rows, because "better
///         than nothing" is not the bar — "better than what is already there" is.
///     </para>
///     <para>
///         <b>Gated by <c>DOTCHARTER_AUDIO_EVAL=1</c> and it skips otherwise.</b> It
///         separates stems, which is minutes per song the first time; the repo's rule
///         is that a research harness is opt-in or it is part of the gate, and this
///         one is decidedly not part of the gate. The offline half of this lane — the
///         extractor, the metric, the renderer's determinism, the isolator's cache
///         discipline — runs in the default gate as ordinary unit tests.
///     </para>
/// </summary>
/// <remarks>
///     In the <c>NativeEngine</c> collection because it opens a real engine through
///     <see cref="NullDeviceRigHost" />. <b>Note that
///     <c>NativeEngineCollectionLintTests</c> would NOT have caught this class</b>:
///     it greps test sources for <c>new JuceAudioEngine(</c> and
///     <c>DCA_ENABLE_NULL_DEVICE</c>, and a class that reaches an engine through a
///     product-side host contains neither string.
/// </remarks>
[Collection("NativeEngine")]
public sealed class AudioEvalTests
{
    /// <summary>The master gate. Must be exactly "1".</summary>
    public const string EnableVariable = "DOTCHARTER_AUDIO_EVAL";

    /// <summary>Caps the number of songs, for a quick look.</summary>
    public const string MaxVariable = "DOTCHARTER_AUDIO_EVAL_MAX";

    /// <summary>Points the eval at a REAL dry DI WAV instead of the generated one.
    /// The two IDMT-SMT-Guitar clips in the local corpus are CC BY-NC-ND: fine for an
    /// in-house measurement, never for anything that ships.</summary>
    public const string ReferenceDiVariable = "DOTCHARTER_AUDIO_EVAL_DI";

    /// <summary>Adds the END-TO-END candidates: what <see cref="IToneMatcher" />
    /// actually finds from the stem alone. Off by default because it multiplies the
    /// lane's cost by the matcher's render budget.</summary>
    public const string MatchVariable = "DOTCHARTER_MATCH_EVAL";

    /// <summary>K for the retrieval library. The shipping default is 4,000; on this
    /// box a render of the 20 s medley measures ~2.1 s, so 4,000 is ≈2.3 hours of
    /// one-off build and a smaller library is the only way the lane runs at all.</summary>
    public const string MatchLibraryVariable = "DOTCHARTER_MATCH_EVAL_K";

    /// <summary>The matcher's per-song render budget.</summary>
    public const string MatchRendersVariable = "DOTCHARTER_MATCH_EVAL_RENDERS";

    /// <summary>The matcher's per-song wall budget, in seconds.</summary>
    public const string MatchWallVariable = "DOTCHARTER_MATCH_EVAL_WALL_SECONDS";

    /// <summary>Skip the matcher on <c>head-of-song</c> rows (default: 1). Those rows
    /// score the intro against whichever rig the elected tone happens to be, so a
    /// matcher number there grades the SPAN and not the search.</summary>
    public const string MatchChartedOnlyVariable = "DOTCHARTER_MATCH_EVAL_CHARTED_ONLY";

    /// <summary>How many songs also get the NO-CHART treatment: the same song with the
    /// chart ignored and a naive <see cref="NaiveSpanFrom" />..+30 s span, which is
    /// what a song with no chart gets today. 0 = off.</summary>
    public const string MatchNoChartVariable = "DOTCHARTER_MATCH_EVAL_NOCHART";

    /// <summary>Where the naive span starts: past the intro, before the song is over.</summary>
    public static readonly TimeSpan NaiveSpanFrom = TimeSpan.FromSeconds(30);

    /// <summary>A comma-separated list of substrings; only rows whose song id or title
    /// matches one of them are scored at all. For re-running the handful of songs a
    /// previous run lost on, without paying for the whole corpus again.</summary>
    public const string OnlyVariable = "DOTCHARTER_AUDIO_EVAL_ONLY";

    /// <summary>The <see cref="OnlyVariable" /> filter, or "every row".</summary>
    private static Func<AudioEvalCorpus.AudioEvalRow, bool> OnlyFilter()
    {
        var raw = Environment.GetEnvironmentVariable(OnlyVariable);
        if (string.IsNullOrWhiteSpace(raw)) return static _ => true;
        var needles = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return row => needles.Any(n =>
            row.Entry.SongId.Contains(n, StringComparison.OrdinalIgnoreCase) ||
            row.Entry.SongTitle.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RigCeiling_AndTheExistingBaseline_OverTheLocalCorpus()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Assert.Skip($"{EnableVariable} is not 1 — the audio eval separates stems and takes minutes " +
                        "per song on the first run, so it is opt-in.");
        }

        var ct = TestContext.Current.CancellationToken;
        var repoRoot = TestPaths.RepoRoot();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var outputRoot = Path.Combine(repoRoot, "artifacts", "audio-eval", stamp);
        Directory.CreateDirectory(outputRoot);

        var limit = int.TryParse(Environment.GetEnvironmentVariable(MaxVariable), out var max) ? max : 60;
        var corpus = AudioEvalCorpus.LoadDetailed(limit);
        var rows = corpus.Rows.Where(OnlyFilter()).ToList();
        Assert.SkipWhen(rows.Count == 0 && corpus.Problems.Count == 0, ToneEvalCorpus.MissingCorpus);

        // Printed ONCE per run, and printed even when it is boring. A row count is not
        // a sample size: the resolver this replaced matched an entry to a folder by its
        // tone JSON, so twelve charted rows scored about five distinct recordings and
        // every published aggregate from this lane was pseudo-replicated without a
        // single line of the report saying so.
        var distinctAudio = rows
            .Select(r => r.SongAudioPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var distinctSpans = rows
            .Select(r => r.SongAudioPath.ToLowerInvariant() + "|" +
                         r.From.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "|" +
                         r.To.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var chartedRows = rows
            .Where(r => !r.SpanSource.StartsWith("head", StringComparison.Ordinal))
            .ToList();
        var chartedDistinct = chartedRows
            .Select(r => r.SongAudioPath.ToLowerInvariant() + "|" +
                         r.From.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var corpusLines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"rows {rows.Count} over **{distinctAudio} DISTINCT audio files** ({distinctSpans} distinct (audio, span) pairs)"),
            string.Create(CultureInfo.InvariantCulture,
                $"charted rows {chartedRows.Count} over **{chartedDistinct} distinct (audio, span) pairs** — this is the charted population's real N"),
            "resolved by title + artist, uniqueness REQUIRED " +
            "(`ToneEvalCorpus.TryReadPackageSongId`)"
        };
        foreach (var problem in corpus.Problems)
        {
            corpusLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"**DROPPED** {problem.Song} / {problem.ToneName}: {problem.Reason}"));
        }

        using var host = new NullDeviceRigHost();
        Assert.SkipUnless(host.IsAvailable, "native DSP ABI not loadable: " + host.Describe());

        var renderer = new RigRenderer(host);
        var similarity = new ToneSimilarity();
        // The same two backends the app composes, in the same order: managed ONNX
        // Runtime first (it is the one that works on a machine whose juce_sharp.dll
        // was built without DOTCHARTER_STEMS_DEMUCS_CPP, which is every prebuilt copy
        // in an agent worktree), native demucs.cpp behind it.
        var models = new StemModelManager(new EvalConfiguration());
        var separation = new CompositeStemSeparationService(
            [new OnnxStemSeparationService(models, null), new JuceStemSeparationService(null, models)],
            null, new InFlightStemSeparationTracker());
        var isolator = new StemIsolator(separation, new StemCacheStore(), FakeLocalization.English());
        Assert.SkipUnless(await separation.IsAvailableAsync(ct) || CacheIsWarm(),
            "no stem-separation backend is available and the cache is cold: " +
            await separation.DescribeBackendAsync(ct));
        var probe = new RigCeilingProbe(isolator, renderer, similarity, FakeLocalization.English());

        var matchEnabled = Environment.GetEnvironmentVariable(MatchVariable) == "1";
        var (di, diSource, diIsMedley) = LoadReferenceDi(matchEnabled);
        var excerpts = Path.Combine(repoRoot, "artifacts", "audio-eval", "excerpts");

        // The matcher's arm. It is built here and not in a field because building the
        // retrieval library is the expensive half and must not happen unless the run
        // asked for it.
        ToneMatcher? matcher = null;
        var matchNotes = new List<string>();
        var matchOptions = MatchOptions();
        if (matchEnabled)
        {
            if (!diIsMedley)
            {
                // The library cache key is (extractor, K, seed) and does NOT name the
                // DI, so building one over an external DI would poison the cache for
                // every later run with a library whose features describe a different
                // dry take. Refuse rather than silently mislead.
                matchNotes.Add(
                    $"matcher DISABLED: {ReferenceDiVariable} is set, and the library cache key does not " +
                    "name the DI — building over an external take would poison the cache.");
            }
            else
            {
                var library = await RigLibrary.LoadOrBuildAsync(
                    di, new RigSampler(), renderer, similarity.Extractor,
                    new RigLibraryOptions(MatchLibraryCount(), 20260912), FakeLocalization.English(), null, ct);
                matchNotes.Add(
                    $"library: K={library.Count}, seed={library.Seed}, extractor={library.ExtractorId}, " +
                    $"built this run with {library.RenderCount} renders");
                matchNotes.Add(
                    $"budget: {matchOptions.MaxRenders} renders / {matchOptions.Budget?.TotalSeconds ?? 60:F0} s " +
                    $"wall, topK={matchOptions.RetrievalTopK}, refine={matchOptions.RefineCandidates}");
                matcher = new ToneMatcher(renderer, similarity, new FixedRigLibraryProvider(library), FakeLocalization.English(), null, matchOptions);
            }
        }

        var chartedOnly = Environment.GetEnvironmentVariable(MatchChartedOnlyVariable) != "0";
        var results = new List<RowResult>();
        var matchRows = new List<MatchEvalReport.MatchRow>();
        var naiveRows = new List<MatchEvalReport.MatchRow>();
        var stopwatch = Stopwatch.StartNew();

        // The report is rewritten after EVERY row, and that is not a nicety.
        // Measured 2026-09-12: the test host was terminated twice mid-run with no
        // Windows error record and no managed exception — once 16 minutes into a
        // 35-row pass — and because the report was only written at the end, both runs
        // lost every row they had already scored. A partial report is a result; a
        // crash that discards 15 songs of stem separation and 240 renders is not.
        var reportPath = Path.Combine(outputRoot, "report.md");

        async Task FlushAsync()
        {
            var partial = BuildReport(results, diSource, host.Describe(), stopwatch.Elapsed, corpusLines) +
                          BuildMatchReport(matchRows, naiveRows, matchNotes);
            await File.WriteAllTextAsync(reportPath, partial, ct);
        }

        foreach (var row in rows.Take(limit))
        {
            ct.ThrowIfCancellationRequested();
            var runMatch = matcher is not null &&
                           (!chartedOnly || !row.SpanSource.StartsWith("head", StringComparison.Ordinal));
            var result = await ScoreRowAsync(
                row, excerpts, isolator, probe, di, runMatch ? matcher : null, ct);
            results.Add(result.Row);
            if (result.Match is { } match) matchRows.Add(match);
            await FlushAsync();
        }

        // The NO-CHART path: the same songs with the chart thrown away and a naive
        // fixed span, which is what a song nobody has charted gets today. It is the
        // question the feature exists to answer, and the charted rows above cannot.
        var noChart = int.TryParse(Environment.GetEnvironmentVariable(MatchNoChartVariable), out var nc) ? nc : 0;
        if (matcher is not null && noChart > 0)
        {
            foreach (var row in rows
                         .Where(r => !r.SpanSource.StartsWith("head", StringComparison.Ordinal))
                         .Take(noChart))
            {
                ct.ThrowIfCancellationRequested();
                var naive = row with
                {
                    From = NaiveSpanFrom,
                    To = NaiveSpanFrom + TimeSpan.FromSeconds(AudioEvalCorpus.MaxSpanSeconds),
                    SpanSource = "naive:30-60"
                };
                var result = await ScoreRowAsync(naive, excerpts, isolator, probe, di, matcher, ct);
                if (result.Match is { } match) naiveRows.Add(match);
                await FlushAsync();
            }
        }

        stopwatch.Stop();
        var report = BuildReport(results, diSource, host.Describe(), stopwatch.Elapsed, corpusLines) +
                     BuildMatchReport(matchRows, naiveRows, matchNotes);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "report.md"), report, ct);

        // The keeper. A run that scored nothing wrote a beautifully formatted report
        // of nothing, and this is the only place that would notice.
        var scored = results.Count(r => r.Scores.Any(s => double.IsFinite(s.Distance)));
        Assert.True(scored > 0,
            "the audio eval scored no rows at all. Report at " + outputRoot + "\n" + report);
    }

    /// <summary>
    ///     One row, scored: isolate the stem, optionally ask the matcher what it finds
    ///     from that stem ALONE, then render and score every candidate over the one
    ///     reference DI.
    ///     <para>
    ///         <b>The matcher's own distance is not the reported one.</b> It ranks with
    ///         <c>ToneSimilarity + 0.15 · MultiScaleSpectralDistance</c>, and the second
    ///         term is only meaningful between two renders of the SAME dry take — here
    ///         the target is a separated stem, so it is not. Every candidate the matcher
    ///         returns is therefore re-rendered and re-scored with the plain
    ///         <see cref="IToneSimilarity" /> that scores <c>rs-truth</c>, which is the
    ///         only way the two columns mean the same thing.
    ///     </para>
    /// </summary>
    private static async Task<(RowResult Row, MatchEvalReport.MatchRow? Match)> ScoreRowAsync(
        AudioEvalCorpus.AudioEvalRow row,
        string excerpts,
        StemIsolator isolator,
        RigCeilingProbe probe,
        MonoAudio di,
        ToneMatcher? matcher,
        CancellationToken ct)
    {
        var excerpt = AudioEvalCorpus.EnsureExcerpt(row, excerpts);
        var span = TimeSpan.FromSeconds(AudioEvalCorpus.MaxSpanSeconds);

        MonoAudio stem;
        try
        {
            stem = await isolator.IsolateGuitarAsync(excerpt, TimeSpan.Zero, span, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (new RowResult(row, [], ex.Message), null);
        }

        var candidates = new List<RigCeilingProbe.Candidate>
        {
            new("rs-truth", row.Entry.Truth),
            new("floor-generic", GenericRig()),
            new("floor-dry", new DcRigPreset([]))
        };

        var baseline = BaselineRig(stem);
        if (baseline is not null) candidates.Add(new RigCeilingProbe.Candidate("baseline-tonefromaudio", baseline));

        ToneMatchResult? match = null;
        var matchSeconds = 0.0;
        if (matcher is not null)
        {
            var sw = Stopwatch.StartNew();
            match = await matcher.MatchAsync(
                new ToneMatchRequest(stem, di, null, Candidates: MatchCandidates), null, ct);
            matchSeconds = sw.Elapsed.TotalSeconds;
            for (var i = 0; i < match.Candidates.Count; i++)
            {
                candidates.Add(new RigCeilingProbe.Candidate(
                    i == 0 ? MatchTop1Label : $"matcher-cand{i + 1}", match.Candidates[i].Rig));
            }
        }

        var measurement = await probe.MeasureAsync(excerpt, TimeSpan.Zero, span, di, candidates, null, ct);
        var scores = measurement.Scores.ToList();

        if (match is null) return (new RowResult(row, scores, null), null);

        // best-of-N, synthesised so it lands in the aggregate next to the others.
        var matcherScores = scores
            .Where(s => s.Label.StartsWith("matcher-", StringComparison.Ordinal) && double.IsFinite(s.Distance))
            .ToList();
        var bestN = matcherScores.Count == 0 ? double.NaN : matcherScores.Min(s => s.Distance);
        scores.Add(new RigCeilingProbe.CandidateScore(MatchBestLabel, bestN, [], null));

        var top1 = scores.FirstOrDefault(s => s.Label == MatchTop1Label)?.Distance ?? double.NaN;
        var truth = scores.FirstOrDefault(s => s.Label == "rs-truth")?.Distance ?? double.NaN;
        var renders = RenderCount(match.Notes);

        return (new RowResult(row, scores, null), new MatchEvalReport.MatchRow(
            $"{row.Entry.Artist} — {row.Entry.SongTitle}",
            row.Entry.ToneName,
            string.Create(CultureInfo.InvariantCulture, $"{row.From.TotalSeconds:F0}–{row.To.TotalSeconds:F0}s"),
            row.SpanSource,
            truth, top1, bestN, match.TargetSelfDistanceFloor,
            renders, matchSeconds,
            match.Candidates.Count > 0 ? match.Candidates[0].Origin : "none",
            $"`{Chain(match.Candidates.Count > 0 ? match.Candidates[0].Rig : null)}` / `{Chain(row.Entry.Truth)}`",
            TopBlocks(scores.FirstOrDefault(s => s.Label == MatchTop1Label))));
    }

    /// <summary>The three feature blocks a candidate's distance is mostly in.</summary>
    private static string TopBlocks(RigCeilingProbe.CandidateScore? score) =>
        score is null || score.Blocks.Count == 0
            ? string.Empty
            : string.Join(", ", score.Blocks
                .OrderByDescending(b => b.Contribution)
                .Take(3)
                .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Block} {b.Contribution:F3}")));

    /// <summary>A rig as its module ids in chain order — the diagnosis column.</summary>
    private static string Chain(DcRigPreset? rig) =>
        rig is null || rig.Modules.Count == 0 ? "(empty)" : string.Join("→", rig.Modules.Select(m => m.ModuleTypeId));

    /// <summary>The label the first returned candidate is scored under.</summary>
    private const string MatchTop1Label = "matcher-top1";

    /// <summary>The label the best of every returned candidate is scored under.</summary>
    private const string MatchBestLabel = "matcher-bestN";

    /// <summary>How many candidates the matcher is asked for. Every one of them costs
    /// an extra render to RE-SCORE, so this is a budget knob and not a display one.</summary>
    private const int MatchCandidates = 3;

    /// <summary>K for the retrieval library, from the environment.</summary>
    private static int MatchLibraryCount() =>
        int.TryParse(Environment.GetEnvironmentVariable(MatchLibraryVariable), out var k) && k > 0 ? k : 96;

    /// <summary>The matcher's budget for ONE song.</summary>
    private static ToneMatcherOptions MatchOptions()
    {
        var renders = int.TryParse(Environment.GetEnvironmentVariable(MatchRendersVariable), out var r) && r > 0
            ? r
            : 16;
        var wall = double.TryParse(
            Environment.GetEnvironmentVariable(MatchWallVariable),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0
            ? w
            : 60.0;
        // RetrievalTopK 3 and RefineCandidates 1, not the shipping 4/3: with 16
        // renders, 4 seeds leave 12 to share between 3 refinements — 4 each, below
        // ToneMatcher's own 8-render floor for a refinement, so NOTHING would be
        // refined and the lane would measure retrieval alone without saying so.
        return new ToneMatcherOptions(
            RetrievalTopK: 3,
            RefineCandidates: 1,
            MaxRenders: renders,
            Budget: TimeSpan.FromSeconds(wall));
    }

    /// <summary>Digs the render count out of the matcher's notes ("N renders in X s").</summary>
    private static int RenderCount(IReadOnlyList<string> notes)
    {
        foreach (var note in notes)
        {
            var cut = note.IndexOf(" renders in ", StringComparison.Ordinal);
            if (cut > 0 && int.TryParse(note.AsSpan(0, cut), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }
        }

        return 0;
    }

    /// <summary>The head-to-head section: the per-song rows, the tally, the worst
    /// losses, and the no-chart delta.</summary>
    private static string BuildMatchReport(
        IReadOnlyList<MatchEvalReport.MatchRow> charted,
        IReadOnlyList<MatchEvalReport.MatchRow> naive,
        IReadOnlyList<string> notes)
    {
        if (charted.Count == 0 && naive.Count == 0 && notes.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Match from audio — end to end");
        sb.AppendLine();
        foreach (var note in notes) sb.AppendLine(CultureInfo.InvariantCulture, $"- {note}");
        sb.AppendLine();

        if (charted.Count > 0)
        {
            var tally = MatchEvalReport.Tally(charted);
            sb.AppendLine(CultureInfo.InvariantCulture, $"**{tally}**");
            sb.AppendLine();
            sb.Append(MatchEvalReport.RenderTable(charted));
            sb.AppendLine();
            sb.AppendLine("### The five worst losses");
            sb.AppendLine();
            var worst = MatchEvalReport.Worst(charted, 5);
            sb.Append(MatchEvalReport.RenderTable(worst));
            sb.AppendLine();
            sb.Append(MatchEvalReport.RenderBlocks(worst));
        }

        if (naive.Count == 0) return sb.ToString();

        sb.AppendLine();
        sb.AppendLine("### No chart: the same songs on a naive 30–60 s span");
        sb.AppendLine();
        sb.Append(MatchEvalReport.RenderTable(naive));
        sb.AppendLine();
        sb.AppendLine("| song | charted matcher-top1 | naive matcher-top1 | delta |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var row in naive)
        {
            var pair = charted.FirstOrDefault(c => c.Song == row.Song && c.Tone == row.Tone);
            var delta = pair is null ? double.NaN : row.MatcherTop1 - pair.MatcherTop1;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Song} | {(pair is null ? "—" : pair.MatcherTop1.ToString("F4", CultureInfo.InvariantCulture))} | " +
                $"{row.MatcherTop1:F4} | {(double.IsFinite(delta) ? delta.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture) : "—")} |");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     An in-memory <see cref="IConfigurationService" /> that names the ONNX model
    ///     and nothing else.
    ///     <para>
    ///         <b>Deliberately not <c>JsonConfigurationService</c>.</b> The ONNX
    ///         backend only engages when <c>StemModelManager.ActiveModelId</c> is set,
    ///         and that setter PERSISTS through the configuration service — so a real
    ///         one would reach into the owner's <c>appconfig.json</c> and change which
    ///         stem model their app uses, from a test run. An eval must not rewrite the
    ///         machine it measures.
    ///     </para>
    /// </summary>
    private sealed class EvalConfiguration : IConfigurationService
    {
        private readonly AppConfig _config = new() { HtDemucsActiveModelId = "htdemucs-6s-onnx" };

        public AppConfig LoadAppConfig() => _config;

        public void SaveAppConfig(AppConfig config) { }

        public void Update(Action<AppConfig> mutate) => mutate(_config);

        public void Flush() { }

        public KeybindConfig LoadKeybinds() => new();

        public void SaveKeybinds(KeybindConfig config) { }

        public void InvalidateCache() { }
    }

    /// <summary>
    ///     A run over an already-separated corpus needs no backend at all — which is
    ///     the state the machine is in after the first run, and the reason the skip
    ///     above is not simply "is a backend available".
    /// </summary>
    private static bool CacheIsWarm()
    {
        var store = new StemCacheStore();
        return Directory.Exists(store.Root) &&
               Directory.EnumerateFiles(store.Root, "*.dcstems", SearchOption.AllDirectories).Any();
    }

    private sealed record RowResult(
        AudioEvalCorpus.AudioEvalRow Row, IReadOnlyList<RigCeilingProbe.CandidateScore> Scores, string? Error);

    /// <summary>
    ///     The information-free rig: the shape the symbolic eval already uses as its
    ///     floor. It knows nothing about the song, so a ceiling that does not beat it
    ///     is not a ceiling.
    /// </summary>
    private static DcRigPreset GenericRig() =>
        new([
            new DcRigModule("overdrive", new Dictionary<string, float>()),
            new DcRigModule("amp", new Dictionary<string, float>()),
            new DcRigModule("cab", new Dictionary<string, float>())
        ]);

    /// <summary>
    ///     The app's EXISTING audio-to-tone path, scored on the same row: profile the
    ///     stem, pick an archetype, emit Rocksmith gear, convert it the same way the
    ///     truth was converted. Null for a span the analyzer calls silent.
    /// </summary>
    private static DcRigPreset? BaselineRig(MonoAudio stem)
    {
        var profile = ToneAudioAnalyzer.Analyze(stem.Samples, stem.SampleRate);
        var suggestion = ToneFromAudioBuilder.Build(profile, RsGearCatalog.Shared, null, null);
        if (suggestion is null) return null;
        return new RsToneConverter().ConvertFilled(suggestion.Tone, out _);
    }

    /// <summary>
    ///     The one dry take every candidate in a row is rendered over.
    ///     <para>
    ///         <b>The match lane moves it from the 12 s phrase to the 20 s MEDLEY</b>,
    ///         because the retrieval library's features are renders of the medley: a
    ///         run that retrieved over the medley and then scored over a different
    ///         phrase would measure the DI change as well as the rig. Every candidate
    ///         in a row moves with it, so the columns stay comparable to each other —
    ///         but a medley ceiling is NOT comparable to a phrase ceiling from an
    ///         earlier run, and the report header says which was used.
    ///     </para>
    /// </summary>
    private static (MonoAudio Di, string Source, bool IsMedley) LoadReferenceDi(bool preferMedley)
    {
        var configured = Environment.GetEnvironmentVariable(ReferenceDiVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var buffer = WavIo.Read(configured);
            return (new MonoAudio(buffer.ToMono(), buffer.SampleRate),
                Path.GetFileName(configured) + " (external)", false);
        }

        return preferMedley
            ? (ReferenceDi.Medley(), ReferenceDi.Id + " MEDLEY (20 s, 4 variants — the library's own DI)", true)
            : (ReferenceDi.Generate(), ReferenceDi.Id + " (generated, no licence encumbrance)", false);
    }

    private static string BuildReport(
        IReadOnlyList<RowResult> results, string diSource, string backend, TimeSpan elapsed,
        IReadOnlyList<string> corpusLines)
    {
        var labels = results
            .SelectMany(r => r.Scores.Select(s => s.Label))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Audio eval — the rig ceiling");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- extractor: `{ToneFeatureExtractor.ExtractorId}` ({ToneFeatureLayout.Length} features)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- reference DI: {diSource}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- renderer: {backend}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- rows: {results.Count}, wall time {elapsed:hh\\:mm\\:ss}");
        foreach (var line in corpusLines) sb.AppendLine(CultureInfo.InvariantCulture, $"- corpus: {line}");
        sb.AppendLine();
        sb.AppendLine($"Distance is `ToneSimilarity` over `{ToneFeatureExtractor.ExtractorId}`; " +
                      "**lower is better**, 0 is identical.");
        sb.AppendLine("`rs-truth` is the CEILING (the song's own Rocksmith tone, converted).");
        sb.AppendLine("`floor-generic` is the information-free overdrive→amp→cab.");
        sb.AppendLine("`floor-dry` is the reference DI with no rig at all.");
        sb.AppendLine();

        // Split by span source. A row that fell back to the head of the song is
        // scoring the intro's tone against whatever rig the song's ELECTED tone
        // happens to be, and averaging those into a "ceiling" is the one mistake this
        // report could make silently: measured, the two populations do not merely
        // differ, their ORDERING inverts.
        AppendAggregate(sb, "Aggregate — charted spans (the valid population)", results, labels,
            r => !r.Row.SpanSource.StartsWith("head", StringComparison.Ordinal));
        AppendAggregate(sb, "Aggregate — head-of-song fallbacks (no matching tone marker)", results, labels,
            r => r.Row.SpanSource.StartsWith("head", StringComparison.Ordinal));
        AppendAggregate(sb, "Aggregate — all rows", results, labels, _ => true);

        sb.AppendLine();
        sb.AppendLine("## Per song");
        sb.AppendLine();
        sb.Append("| song | tone | span | ");
        foreach (var label in labels) sb.Append(CultureInfo.InvariantCulture, $"{label} | ");
        sb.AppendLine();
        sb.Append("|---|---|---|");
        foreach (var _ in labels) sb.Append("---:|");
        sb.AppendLine();

        foreach (var result in results.OrderBy(r => r.Row.Entry.SongId, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {result.Row.Entry.Artist} — {result.Row.Entry.SongTitle} | {result.Row.Entry.ToneName} | " +
                $"{result.Row.From.TotalSeconds:F0}–{result.Row.To.TotalSeconds:F0}s ({result.Row.SpanSource}) | ");
            if (result.Error is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{result.Error} |");
                continue;
            }

            foreach (var label in labels)
            {
                var score = result.Scores.FirstOrDefault(s => s.Label == label);
                sb.Append(score is null
                    ? " — | "
                    : string.Create(CultureInfo.InvariantCulture,
                        $"{(double.IsFinite(score.Distance) ? score.Distance.ToString("F4", CultureInfo.InvariantCulture) : score.Error)} | "));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendAggregate(
        StringBuilder sb, string title, IReadOnlyList<RowResult> results, IReadOnlyList<string> labels,
        Func<RowResult, bool> include)
    {
        var rows = results.Where(include).ToList();
        if (rows.Count == 0) return;

        sb.AppendLine(CultureInfo.InvariantCulture, $"## {title}");
        sb.AppendLine();
        sb.AppendLine("| candidate | rows | mean | median | p10 (best) | p90 (worst) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var label in labels)
        {
            var values = rows
                .SelectMany(r => r.Scores.Where(s => s.Label == label && double.IsFinite(s.Distance)))
                .Select(s => s.Distance)
                .OrderBy(d => d)
                .ToList();
            if (values.Count == 0) continue;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| `{label}` | {values.Count} | {values.Average():F4} | {Percentile(values, 0.50):F4} | " +
                $"{Percentile(values, 0.10):F4} | {Percentile(values, 0.90):F4} |");
        }

        sb.AppendLine();
    }

    private static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        var index = Math.Clamp((int)Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }
}
