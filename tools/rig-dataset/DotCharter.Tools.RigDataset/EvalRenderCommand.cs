using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Localization;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using JuceSharp.AudioIo;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     <c>eval-render</c> — <b>does the estimator's guess SOUND close?</b>
///     <para>
///         Every number the trainer reports is a LABEL metric: family F1, exact chain
///         match, knob MAE. The owner's goal is not the exact gear, it is the sound —
///         and those two come apart in both directions. A predicted chain that gets
///         one pedal wrong out of five can be inaudible; a chain that is exactly right
///         with a gain knob 0.2 off can be a different amplifier. This command answers
///         the audible question directly: render the predicted rig and the true rig
///         over the SAME dry DI span and measure the distance the match lane's own
///         metric reports.
///     </para>
///     <para>
///         <b>Three numbers per row, and the last two are what make the first one
///         readable.</b> A raw <c>ToneSimilarity</c> distance has no absolute scale —
///         it depends on the riff, the DI, the separation bleed. So every row also
///         carries its own SELF-FLOOR (the target measured against a disjoint window
///         of itself: what "the same tone" costs on this material) and the distance
///         of a fixed GENERIC rig (overdrive → amp → cab at catalogue defaults, the
///         same <c>floor-generic</c> the audio eval uses). <c>distanceOverFloor</c>
///         is comparable across rows; "beats generic" is the per-row sanity check
///         that the model contributed anything at all.
///     </para>
///     <para>
///         <b>It inherits the metric's ceiling and says so.</b> <c>tonefx-v2</c>
///         measures 1.56× content invariance (see
///         <c>docs/agents/services/ai-match-search.md</c>), so a rig distance and a
///         riff distance are within a factor of 1.56 of each other — no aggregate
///         here can be read as finer than that, and a median that moves by 5 % between
///         two models is noise.
///     </para>
///     <para>
///         <b>One engine, one seat, renders serialised</b> — the same rule
///         <see cref="RenderCommand" /> and <see cref="KnobSensitivityCommand" />
///         document. <c>--threads</c> bounds the rows in flight, which parallelises
///         the feature extraction and the I/O around a render queue of one.
///     </para>
/// </summary>
public sealed class EvalRenderCommand
{
    /// <summary>Schema version of the emitted summary JSON. Bump on any layout change.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    ///     Default length of each self-floor window, in seconds.
    ///     <para>
    ///         <b>2 s and not the matcher's 5 s, because a dataset clip is 5 s long.</b>
    ///         The floor needs two DISJOINT windows of the target, so a 5 s clip cannot
    ///         hold two 5 s ones and every row would report NaN. At 2 s a 5 s clip holds
    ///         two with a second between them. That is a WEAKER floor than the matcher's
    ///         — <c>ToneMatcher.SelfDistance</c> records that below about 2× the window
    ///         the number means very little, and 5 s of 2 s windows is exactly the
    ///         marginal case it warns about — so the floor is a SCALE, not a target, and
    ///         the raw median distance is reported beside it for that reason.
    ///     </para>
    /// </summary>
    public const double DefaultSelfWindowSeconds = 2.0;

    private readonly EvalRenderOptions _o;

    /// <summary>Builds the command over its parsed flags.</summary>
    /// <param name="options">The parsed command line.</param>
    public EvalRenderCommand(EvalRenderOptions options) => _o = options;

    /// <summary>
    ///     The information-free control: an overdrive into an amp into a cab, every
    ///     knob at its catalogue default.
    ///     <para>
    ///         <b>The same chain <c>AudioEvalTests</c> calls <c>floor-generic</c></b>,
    ///         and kept identical on purpose: it is the answer a model that learned
    ///         nothing but the corpus's most common shape would give, so "the
    ///         prediction beats generic" is the weakest claim worth making and the
    ///         first one to check. Empty param dictionaries mean the catalogue
    ///         defaults, so this chain cannot drift when a knob's default is retuned —
    ///         it IS whatever the engine calls default today.
    ///     </para>
    /// </summary>
    /// <returns>The generic rig.</returns>
    public static DcRigPreset GenericRig() =>
        new([
            new DcRigModule("overdrive", new Dictionary<string, float>()),
            new DcRigModule("amp", new Dictionary<string, float>()),
            new DcRigModule("cab", new Dictionary<string, float>())
        ]);

    /// <summary>
    ///     The distance between two DISJOINT windows of one clip, measured with the
    ///     same metric a candidate is measured with — the noise floor of the row's
    ///     comparison, and the same construction as <c>ToneMatcher.SelfDistance</c>.
    ///     <para>
    ///         <b>NaN means "no floor known"</b> and a consumer must fall back rather
    ///         than divide. It is never 0 for a short clip: 0 is a legal floor value
    ///         meaning "perfectly self-similar", which is the wrong answer for a
    ///         right-looking reason.
    ///     </para>
    /// </summary>
    /// <param name="target">The clip to measure against itself.</param>
    /// <param name="similarity">The metric; its extractor is used on both windows.</param>
    /// <param name="windowSeconds">Each window's length.</param>
    /// <returns>The self-distance, or NaN.</returns>
    public static double SelfFloor(MonoAudio target, IToneSimilarity similarity, double windowSeconds)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(similarity);
        var n = (int)Math.Round(windowSeconds * target.SampleRate);
        if (n <= 0 || target.Samples.Length < 2 * n) return double.NaN;
        var a = new MonoAudio(target.Samples[..n], target.SampleRate);
        var b = new MonoAudio(target.Samples[^n..], target.SampleRate);
        return similarity.Distance(similarity.Extractor.Extract(a), similarity.Extractor.Extract(b));
    }

    /// <summary>Runs the whole eval and writes the JSONL and the summary JSON.</summary>
    /// <param name="ct">Cancellation; a Ctrl-C writes what has been scored so far.</param>
    /// <returns>0 on success, 2 when the renderer is unavailable, 3 on a bad input.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!File.Exists(_o.PredictionsPath))
        {
            Console.Error.WriteLine($"error: no predictions file at {_o.PredictionsPath}.");
            return 3;
        }

        if (!Directory.Exists(_o.Root))
        {
            Console.Error.WriteLine($"error: no dataset root at {_o.Root}.");
            return 3;
        }

        var all = EvalRenderPredictions.ParseAll(File.ReadLines(_o.PredictionsPath), out var parseErrors);
        Console.WriteLine($"{all.Count} prediction rows read from {_o.PredictionsPath}"
                          + (parseErrors.Count > 0 ? $" ({parseErrors.Count} refused)" : "") + ".");
        foreach (var e in parseErrors.Take(5)) Console.Error.WriteLine("  refused " + e);
        if (all.Count == 0)
        {
            Console.Error.WriteLine("error: nothing to score.");
            return 3;
        }

        // A predictions file with no span on any row is scored over the FIRST N
        // seconds of each DI, which is not the audio the clip was rendered from — so
        // in clip mode every distance would carry a content mismatch on top of the
        // rig difference, and the whole table would read as "the model is bad".
        if (!_o.TargetIsRender && all.All(r => r.StartSeconds <= 0 && r.LengthSeconds <= 0))
        {
            Console.Error.WriteLine(
                "WARNING: no row carries a 'seconds' span. In --target clip mode the predicted rig is then "
                + "rendered over the START of each DI while the clip was rendered from a window somewhere "
                + "inside it, so every distance carries a CONTENT mismatch the metric cannot remove. "
                + "Emit \"seconds\": [start, length], or use --target render.");
        }

        var picked = EvalRenderSelection.Subsample(all.Count, _o.Limit, _o.Seed);
        if (picked.Length != all.Count)
            Console.WriteLine($"Subsampled {picked.Length} of {all.Count} rows (seed {_o.Seed}).");

        if (_o.BelowNormalPriority) TrySetBelowNormal();

        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
        using var host = new NullDeviceRigHost();
        var renderer = new RigRenderer(host);
        if (!renderer.IsAvailable)
        {
            Console.Error.WriteLine($"error: the offline renderer is unavailable ({renderer.Describe()}). "
                                    + "No numbers are written — a fabricated sound-distance table is worse than none.");
            return 2;
        }

        Console.WriteLine($"Renderer: {renderer.Describe()}");
        var similarity = new ToneSimilarity();
        var generic = GenericRig();
        var di = ClipSource.Unscanned(_o.Root);

        IToneMatcher? matcher = null;
        if (_o.Refine)
        {
            matcher = await BuildMatcherAsync(renderer, similarity, ct).ConfigureAwait(false);
            if (matcher is null) return 2;
        }

        var results = new ConcurrentDictionary<int, EvalRenderRow>();
        var sw = Stopwatch.StartNew();
        var done = 0;

        try
        {
            await Parallel.ForEachAsync(picked,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _o.Threads), CancellationToken = ct },
                async (index, token) =>
                {
                    var row = await ScoreAsync(all[index], index, renderer, similarity, generic, di, matcher, token)
                        .ConfigureAwait(false);
                    results[index] = row;
                    var n = Interlocked.Increment(ref done);
                    if (_o.ProgressEvery > 0 && n % _o.ProgressEvery == 0)
                    {
                        var perRow = sw.Elapsed.TotalSeconds / n;
                        Console.WriteLine($"[{n}/{picked.Length}] {perRow:F2} s/row · "
                                          + $"eta {TimeSpan.FromSeconds(perRow * (picked.Length - n)):d\\.hh\\:mm\\:ss}");
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Stopped after {results.Count} rows; writing what was scored.");
        }

        var rows = results.Values.OrderBy(r => r.Index).ToArray();
        Write(rows, picked.Length, parseErrors, renderer.Describe(), similarity.Extractor.Id, sw.Elapsed);
        return 0;
    }

    // ------------------------------------------------------------------- one row

    private async Task<EvalRenderRow> ScoreAsync(
        RigPrediction p, int index, RigRenderer renderer, ToneSimilarity similarity,
        DcRigPreset generic, ClipSource di, IToneMatcher? matcher, CancellationToken ct)
    {
        var plausibility = SafePenalty(p.Predicted);
        EvalRenderRow Skip(string why) => new(index, p.ClipPath, p.DiPath,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            plausibility, p.Confidence, "none", why);

        MonoAudio dry;
        try
        {
            dry = ReadDiSpan(di, p);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
                                       or UnauthorizedAccessException)
        {
            return Skip($"DI '{p.DiPath}': {ex.GetType().Name}: {ex.Message}");
        }

        if (dry.Samples.Length == 0) return Skip($"DI '{p.DiPath}' is empty or the span is outside it.");

        MonoAudio target;
        var targetSource = "clip";
        var truthDistance = double.NaN;
        try
        {
            var clip = _o.TargetIsRender ? null : TryReadClip(p.ClipPath);
            if (clip is not null)
            {
                target = clip;
            }
            else
            {
                // Re-rendering the truth rig is the ONLY way to get a target when the
                // clip is not on disk — and in --target render mode it is the point:
                // a prediction identical to the truth then measures EXACTLY 0, which
                // is what proves this harness is not scoring noise.
                target = await renderer.RenderAsync(dry, p.Truth, ct).ConfigureAwait(false);
                targetSource = "render";
                truthDistance = 0.0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Skip($"target: {ex.GetType().Name}: {ex.Message}");
        }

        if (target.Samples.Length == 0) return Skip("the target is empty.");

        var targetFeatures = similarity.Extractor.Extract(target);
        var floor = SelfFloor(target, similarity, _o.SelfWindowSeconds);

        double distance;
        try
        {
            var wet = await renderer.RenderAsync(dry, p.Predicted, ct).ConfigureAwait(false);
            distance = similarity.Distance(similarity.Extractor.Extract(wet), targetFeatures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A rig the engine refuses is a FINDING — the model emitted a chain that
            // cannot be built — so it is counted and named, never dropped quietly into
            // an aggregate it would have worsened.
            return Skip($"the engine refused the predicted rig: {ex.GetType().Name}: {ex.Message}");
        }

        var genericDistance = double.NaN;
        try
        {
            var wet = await renderer.RenderAsync(dry, generic, ct).ConfigureAwait(false);
            genericDistance = similarity.Distance(similarity.Extractor.Extract(wet), targetFeatures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"row {index}: the generic baseline render failed ({ex.Message}); "
                                    + "the row keeps its distance and loses its control column.");
        }

        var refined = double.NaN;
        if (matcher is not null)
            refined = await RefineAsync(matcher, target, dry, p.Predicted, index, ct).ConfigureAwait(false);

        var overFloor = double.IsFinite(floor) && floor > 0 ? distance / floor : double.NaN;
        return new EvalRenderRow(index, p.ClipPath, p.DiPath, distance, floor, overFloor,
            genericDistance, truthDistance, refined, plausibility, p.Confidence, targetSource, null);
    }

    private async Task<double> RefineAsync(IToneMatcher matcher, MonoAudio target, MonoAudio dry,
        DcRigPreset prior, int index, CancellationToken ct)
    {
        try
        {
            var result = await matcher.MatchAsync(new ToneMatchRequest(
                target, dry, new ToneMatchPrior(prior, "estimator"), Candidates: 1)
            {
                // The target IS a render of this DI when we made it ourselves; in clip
                // mode it is a wet clip off disk, rendered from the SAME dry take, so
                // the raw-audio term is valid on both paths. It is NOT valid against a
                // separated stem of a record, which this eval never scores.
                TargetIsRender = true
            }, null, ct).ConfigureAwait(false);
            // The WINNER's measured distance — what the product would hand the user —
            // and NOT the minimum over the candidates. The matcher ranks by
            // plausibility-weighted distance, so the winner can be further in RAW
            // distance than the prior was, and at a small --refine-renders /
            // --refine-library the retrieval seeds outrank the prior outright
            // (measured 2026-09-14: 0.4312 -> 0.5829 on three rows at 6 renders over
            // an 8-row library). That is the product's answer and it is what this
            // column must report; taking the minimum would flatter a path nobody
            // ships.
            return result.Candidates.Count > 0 ? result.Candidates[0].Distance : double.NaN;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"row {index}: refinement failed ({ex.Message}).");
            return double.NaN;
        }
    }

    private static double SafePenalty(DcRigPreset rig)
    {
        try { return RigPlausibility.Penalty(rig); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return double.NaN; }
    }

    // ----------------------------------------------------------------------- io

    /// <summary>The dry span the truth clip was rendered from, at the render rate.
    /// A span past the end of the DI is zero-padded rather than refused — the trainer
    /// records the span it drew, and a DI trimmed since is a stale path, not a reason
    /// to lose the row.</summary>
    private MonoAudio ReadDiSpan(ClipSource pool, RigPrediction p)
    {
        var samples = pool.Read(p.DiPath);
        var rate = RigRenderer.RenderRate;
        var start = (int)Math.Round(Math.Max(0, p.StartSeconds) * rate);
        var length = p.LengthSeconds > 0
            ? (int)Math.Round(p.LengthSeconds * rate)
            : Math.Max(0, samples.Length - start);
        if (length <= 0) return new MonoAudio([], rate);

        var span = new float[length];
        var n = Math.Clamp(samples.Length - start, 0, length);
        if (n > 0) Array.Copy(samples, start, span, 0, n);
        return new MonoAudio(span, rate);
    }

    /// <summary>The wet clip already on disk, level-matched into the same space every
    /// render lands in. Null when it is not there (a predictions file written against
    /// a corpus this machine does not hold).</summary>
    private MonoAudio? TryReadClip(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        var path = Path.GetFullPath(Path.Combine(_o.Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return null;

        var buffer = WavIo.Read(path);
        var mono = buffer.Channels == 1 ? buffer.Interleaved : buffer.ToMono();
        // Level-matched, and that is not a formality: the metric's own rule is that
        // both sides of a comparison arrive at the same RMS, or the mel and MFCC
        // blocks report a level difference as a spectral one. Every render comes out
        // of RigRenderer already at LevelMatch's target; a clip off disk does not.
        return LevelMatch.ToAnalysisLevel(
            MonoResampler.To(new MonoAudio(mono, buffer.SampleRate), RigRenderer.RenderRate));
    }

    private async Task<IToneMatcher?> BuildMatcherAsync(RigRenderer renderer, ToneSimilarity similarity,
        CancellationToken ct)
    {
        Console.WriteLine($"--refine: building a {_o.RefineLibrary}-row retrieval library "
                          + $"({_o.RefineLibrary} renders, once) and giving each row {_o.RefineRenders} renders.");
        var provider = new RigLibraryProvider(
            _ => Task.FromResult(ReferenceDi.Medley()),
            new RigSampler(),
            renderer,
            similarity.Extractor,
            new RigLibraryOptions(Count: _o.RefineLibrary, Seed: _o.Seed));
        try
        {
            var library = await provider.GetAsync(null, ct).ConfigureAwait(false);
            Console.WriteLine($"Library: {library.Count} rows ({library.RenderCount} renders this run).");
            return new ToneMatcher(renderer, similarity, new FixedRigLibraryProvider(library), LocalizationService.Instance, null,
                new ToneMatcherOptions(
                    RetrievalTopK: 1,
                    RefineCandidates: 1,
                    MaxRenders: _o.RefineRenders,
                    Budget: TimeSpan.FromSeconds(_o.RefineSeconds),
                    Seed: _o.Seed,
                    SelfDistanceWindow: TimeSpan.FromSeconds(_o.SelfWindowSeconds)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"error: --refine could not build its library ({ex.Message}).");
            return null;
        }
    }

    // -------------------------------------------------------------------- output

    private static readonly JsonSerializerOptions Json = new()
    {
        // The floors and the distances are legitimately NaN on some rows and a
        // serializer that throws on one would lose the whole file.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions SummaryJson = new(Json) { WriteIndented = true };

    private void Write(IReadOnlyList<EvalRenderRow> rows, int attempted, IReadOnlyList<string> parseErrors,
        string renderer, string extractorId, TimeSpan elapsed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_o.OutPath))!);
        File.WriteAllLines(_o.OutPath, rows.Select(r => JsonSerializer.Serialize(r, Json)));

        var scored = rows.Where(r => r.Scored).ToArray();
        var topN = _o.TopConfident > 0 ? _o.TopConfident : Math.Max(1, scored.Length / 10);
        var top = EvalRenderSelection.TopConfident(scored, topN);

        var summary = new EvalRenderSummary(
            SchemaVersion,
            Path.GetFullPath(_o.PredictionsPath),
            Path.GetFullPath(_o.Root),
            extractorId,
            renderer,
            attempted,
            scored.Length,
            rows.Count - scored.Length,
            parseErrors.Count,
            rows.Where(r => r.Error is not null).Take(20).Select(r => $"row {r.Index}: {r.Error}").ToArray(),
            _o.SelfWindowSeconds,
            _o.TargetIsRender ? "render" : "clip",
            _o.Refine,
            topN,
            EvalRenderStats.Compute(scored),
            EvalRenderStats.Compute(top),
            elapsed.TotalSeconds,
            scored.Length > 0 ? elapsed.TotalSeconds / scored.Length : double.NaN,
            Notes());

        File.WriteAllText(_o.SummaryPath, JsonSerializer.Serialize(summary, SummaryJson));

        Console.WriteLine();
        Console.WriteLine($"Scored {scored.Length} of {attempted} rows in {elapsed:d\\.hh\\:mm\\:ss} "
                          + $"({summary.SecondsPerRow:F2} s/row).");
        Print("all", summary.All);
        if (top.Count > 0) Print($"top-{topN} confident", summary.TopConfident);
        Console.WriteLine($"Wrote {_o.OutPath}");
        Console.WriteLine($"Wrote {_o.SummaryPath}");
    }

    private static void Print(string label, EvalRenderStats s)
    {
        Console.WriteLine($"  {label}: n={s.Rows} "
                          + $"median d={Fmt(s.MedianDistance)} mean={Fmt(s.MeanDistance)} "
                          + $"median d/floor={Fmt(s.MedianDistanceOverFloor)} (floor known on {s.FloorKnown}) "
                          + $"<1.5x {Pct(s.FractionUnder1p5)} <2x {Pct(s.FractionUnder2)} <3x {Pct(s.FractionUnder3)} "
                          + $"· generic median={Fmt(s.MedianGenericDistance)} beats generic {Pct(s.FractionBeatsGeneric)}"
                          + (double.IsFinite(s.MedianTruthDistance)
                              ? $" · truth median={Fmt(s.MedianTruthDistance)}"
                              : "")
                          + (double.IsFinite(s.MedianRefinedDistance)
                              ? $" · refined median={Fmt(s.MedianRefinedDistance)} "
                                + $"improved {Pct(s.FractionRefinedBeatsPredicted)}"
                              : ""));
    }

    private static string Fmt(double v) => double.IsFinite(v) ? v.ToString("F4") : "n/a";

    private static string Pct(double v) => double.IsFinite(v) ? $"{v * 100:F1}%" : "n/a";

    private string Notes() =>
        "Every distance is a ToneSimilarity (tonefx-v2) distance between two level-matched mono clips: the "
        + "predicted rig rendered over the row's dry DI span, against the target. "
        + (_o.TargetIsRender
            ? "TARGET = the TRUTH rig re-rendered over the same span, so a prediction equal to the truth "
              + "measures exactly 0 and 'truthDistance' is 0 by construction -- this mode is the harness's own "
              + "non-vacuity check, not the product question. "
            : "TARGET = the wet clip already on disk, which carries the corpus augmentations (level, tilt, "
              + "pickup comb, bleed, separation) that no rig can reproduce -- so even a perfect prediction "
              + "does NOT measure 0 here, and the floor and the generic column are what make the number "
              + "readable. ")
        + $"'selfFloor' is the target against a disjoint {_o.SelfWindowSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} s "
        + "window of itself, the same construction ToneMatcher uses; NaN means the target was too short to "
        + "hold two, and a consumer must fall back rather than divide. A 5 s clip holds two 2 s windows "
        + "MARGINALLY -- the floor is a scale, not a target. "
        + "'genericDistance' is the fixed overdrive -> amp -> cab at catalogue defaults (audio-eval's "
        + "'floor-generic') over the same DI span. "
        + "THE METRIC IS THE CEILING: tonefx-v2 measures 1.56x content invariance, so two aggregates within "
        + "a few percent of each other are the same aggregate.";

    private static void TrySetBelowNormal()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("warning: could not lower the process priority: " + ex.Message);
        }
    }
}

/// <summary>The whole run's report, written beside the per-row JSONL.</summary>
/// <param name="SchemaVersion">See <see cref="EvalRenderCommand.SchemaVersion" />.</param>
/// <param name="Predictions">Absolute path of the predictions file scored.</param>
/// <param name="Root">Absolute path of the dataset root.</param>
/// <param name="Extractor">The feature extractor id both sides were embedded with.</param>
/// <param name="Renderer">One line naming the offline render backend.</param>
/// <param name="Attempted">How many rows this run tried to score.</param>
/// <param name="Scored">How many carry a measurement.</param>
/// <param name="Skipped">How many were skipped, with a reason each.</param>
/// <param name="Unparseable">Lines the predictions file could not be read from.</param>
/// <param name="SkipReasons">The first twenty skip reasons, verbatim.</param>
/// <param name="SelfWindowSeconds">Length of each self-floor window.</param>
/// <param name="Target"><c>clip</c> or <c>render</c>.</param>
/// <param name="Refined">Whether <c>--refine</c> ran.</param>
/// <param name="TopConfidentN">How many rows the top-N block covers.</param>
/// <param name="All">The aggregate over every scored row.</param>
/// <param name="TopConfident">The aggregate over the most confident rows.</param>
/// <param name="ElapsedSeconds">Wall time of the whole run.</param>
/// <param name="SecondsPerRow">Wall time per scored row.</param>
/// <param name="Notes">What the numbers mean and what they cannot mean.</param>
public sealed record EvalRenderSummary(
    int SchemaVersion,
    string Predictions,
    string Root,
    string Extractor,
    string Renderer,
    int Attempted,
    int Scored,
    int Skipped,
    int Unparseable,
    IReadOnlyList<string> SkipReasons,
    double SelfWindowSeconds,
    string Target,
    bool Refined,
    int TopConfidentN,
    EvalRenderStats All,
    EvalRenderStats TopConfident,
    double ElapsedSeconds,
    double SecondsPerRow,
    string Notes);

/// <summary>The <c>eval-render</c> command line.</summary>
/// <param name="PredictionsPath">The predictions JSONL to score.</param>
/// <param name="Root">The dataset root the row paths are relative to.</param>
/// <param name="OutPath">Where the per-row JSONL goes.</param>
/// <param name="SummaryPath">Where the summary JSON goes.</param>
/// <param name="Limit">Score only N rows; 0 means all of them.</param>
/// <param name="Seed">Seed of the subsample draw and of the refinement.</param>
/// <param name="Threads">Rows in flight; renders still serialise on one engine.</param>
/// <param name="SelfWindowSeconds">Each self-floor window's length.</param>
/// <param name="TargetIsRender">Re-render the truth rig as the target instead of
/// reusing the wet clip on disk.</param>
/// <param name="TopConfident">How many rows the top-N block covers; 0 means a tenth.</param>
/// <param name="Refine">Run the matcher's refinement from the predicted rig.</param>
/// <param name="RefineRenders">Render budget per row under <paramref name="Refine" />.</param>
/// <param name="RefineLibrary">Retrieval-library size built once for the refinement.</param>
/// <param name="RefineSeconds">Wall budget per row under <paramref name="Refine" />.</param>
/// <param name="ProgressEvery">Print a progress line every N rows; 0 silences it.</param>
/// <param name="BelowNormalPriority">Drop the process to BelowNormal before rendering.</param>
public sealed record EvalRenderOptions(
    string PredictionsPath,
    string Root,
    string OutPath,
    string SummaryPath,
    int Limit,
    int Seed,
    int Threads,
    double SelfWindowSeconds,
    bool TargetIsRender,
    int TopConfident,
    bool Refine,
    int RefineRenders,
    int RefineLibrary,
    double RefineSeconds,
    int ProgressEvery,
    bool BelowNormalPriority);
