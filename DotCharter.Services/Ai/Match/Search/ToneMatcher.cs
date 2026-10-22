using DotCharter.Localization;
using System.Diagnostics;
using System.Globalization;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>The matcher's knobs. Defaults are the shipping budget.</summary>
/// <param name="RetrievalTopK">How many library hits to consider.</param>
/// <param name="RefineCandidates">How many of the scored candidates get CMA-ES
/// refinement. Refining everything spends the whole budget on rigs retrieval
/// already ranked last.</param>
/// <param name="MaxRenders">
/// The hard render budget for one match — <b>the REAL cap, reconciled with
/// <paramref name="Budget" /> rather than decorative.</b>
/// <para>
/// It was 400 against a 60 s wall. One render of the 20 s medley plus one
/// <c>ToneFeatureExtractor</c> pass costs ~2.55 s in Release on the owner's box
/// (<c>RenderCostEvalTests</c> re-measures it), so the wall expired after ~23
/// renders and the shipping defaults delivered three seed renders and at most one
/// CMA-ES generation. A reader of this record believed the search was 17× deeper
/// than it was. 48 renders ≈ 2 minutes of evaluation, which the 150 s wall can
/// actually spend.</para></param>
/// <param name="Budget">
/// The wall-clock budget, or null for <see cref="ToneMatcher.DefaultWallBudget" />
/// (150 s). Whichever bound is hit first ends the search. The render
/// count is the one a test asserts on — wall time depends on the rig and the
/// machine.
/// <para>
/// 150 s is <paramref name="MaxRenders" /> × the measured 2.55 s (≈122 s) with
/// ~25 % headroom for a contended box. That admits 4 seeds plus 2 refinements of
/// 2 generations each at λ 8 — the plan is printed in the result notes, so a run
/// that did less says so.</para></param>
/// <param name="PopulationSize">
/// CMA-ES λ, or null for the standard <c>4 + ⌊3·ln n⌋</c>.
/// <para>
/// <b>8 rather than null.</b> A retrieved rig has ~15–25 continuous coordinates, so
/// the standard formula gives λ 12–13 and a budget of 22 evaluations buys ONE
/// generation. CMA-ES earns its keep by ADAPTING a covariance over generations;
/// one generation is a random sample around the start point. A narrower population
/// over more generations is the right trade at this budget, and the matcher shrinks
/// λ further rather than skipping a refinement it cannot afford at full width —
/// which is what it would otherwise do, silently, leaving the lane measuring retrieval
/// only.</para></param>
/// <param name="SpectralWeight">Weight of the raw-audio
/// <see cref="MultiScaleSpectralDistance" /> term beside the feature distance.
/// Small: the feature extractor is the metric, this only breaks ties the
/// embedding is blind to.</param>
/// <param name="Seed">CMA-ES seed — the whole match is deterministic for it.</param>
/// <param name="InitialSigma">CMA-ES step size. Smaller than the optimiser's own
/// default because retrieval already placed the start point.</param>
/// <param name="SelfDistanceWindow">The window length used for the self-distance
/// floor.</param>
/// <param name="PriorInitialSigma">CMA-ES step size for the PRIOR's own candidate.
/// Wider than <paramref name="InitialSigma" /> deliberately: a retrieval hit was
/// MEASURED against this target and its knobs are already near a local optimum,
/// while a prior is a guess from a model that never heard the recording. Its chain
/// is the valuable part; its knob values deserve more exploration.</param>
/// <param name="PriorRetrievalPoolFactor">How many times <paramref name="RetrievalTopK" />
/// library rows are pulled before the prior re-ranks them. The shortlist is still
/// <paramref name="RetrievalTopK" /> long — this only widens what the prior is
/// allowed to choose FROM, and costs a distance sort, not a render. 1 disables
/// prior-steered retrieval without disabling the prior itself.</param>
/// <param name="PriorFamilyWeight">How hard the prior steers retrieval, in
/// <c>[0,1]</c>: a row's ranking distance is scaled by
/// <c>1 - PriorFamilyWeight × overlap</c>, where overlap is the Jaccard similarity
/// of its module-type set with the prior's. At 0.5 a row with the prior's exact
/// family set ranks as if it were half as far. It is MULTIPLICATIVE because the
/// feature distance has no fixed scale — an additive bonus would mean something
/// different for every extractor.</param>
/// <param name="UsePlausibility">Whether the ranking distance is scaled by
/// <see cref="RigPlausibility" />. On by default; off reproduces the unscaled
/// ranking exactly, which is what an A/B of the change is run with.</param>
/// <param name="PlausibilityWeight">How hard an implausible rig is marked down:
/// the ranking distance is <c>raw × (1 + PlausibilityWeight × penalty)</c>.
/// <para>
/// <b>0.6 is calibrated, not chosen for roundness.</b> The first end-to-end
/// measurement had a no-amp/no-cab chain beat the authored Rocksmith rig 0.4909 to
/// 0.6953 — a 1.42× gap — on six of twelve rows. That chain's penalty is
/// <see cref="RigPlausibility.NoAmpNoCabWeight" /> (0.85), so 0.6 turns it into a
/// 1.51× multiplier: enough to flip exactly that ranking, and small enough that a
/// rig which really is 2× closer still wins. It is MULTIPLICATIVE for the same
/// reason the prior's family steering is — the feature distance has no fixed
/// scale.</para></param>
public sealed record ToneMatcherOptions(
    int RetrievalTopK = 4,
    // 2, not 3: at the reconciled budget a third refinement halves the generations
    // the first two get, and retrieval already ranked it last.
    int RefineCandidates = 2,
    int MaxRenders = 48,
    TimeSpan? Budget = null,
    double SpectralWeight = 0.15,
    int Seed = 20260912,
    double InitialSigma = 0.25,
    TimeSpan? SelfDistanceWindow = null,
    double PriorInitialSigma = 0.40,
    int PriorRetrievalPoolFactor = 6,
    double PriorFamilyWeight = 0.5,
    bool UsePlausibility = true,
    double PlausibilityWeight = 0.6,
    int? PopulationSize = 8);

/// <summary>
///     The <see cref="ToneMatchCandidate.Origin" /> vocabulary, so a UI can say
///     where a winner came from without parsing free text it hopes nobody changed.
///     <para>
///         An origin is a "+"-joined trail, most significant first: the PRIOR's own
///         candidate refined by the optimiser is <c>prior:ai:claude+cmaes</c>, a
///         library row the prior pulled into the shortlist and that was then refined
///         is <c>retrieval:prior-seeded+cmaes</c>. Read it with
///         <see cref="IsPrior" /> / <see cref="WasRefined" /> rather than by
///         comparing whole strings — the prior's source tag is part of it.
///     </para>
/// </summary>
public static class ToneMatchOrigins
{
    /// <summary>The caller's prior, unrefined. With a source it is
    /// <c>prior:&lt;source&gt;</c>.</summary>
    public const string Prior = "prior";

    /// <summary>A library row from the unbiased top-k.</summary>
    public const string Retrieval = "retrieval";

    /// <summary>A library row that reached the shortlist because it shares the
    /// prior's module families — it was NOT in the unbiased top-k. This is the
    /// visible evidence that the prior steered retrieval.</summary>
    public const string PriorSeededRetrieval = "retrieval:prior-seeded";

    /// <summary>The trained estimator's guess.</summary>
    public const string Estimator = "estimator";

    /// <summary>Appended to whatever it started as when CMA-ES improved it.</summary>
    public const string RefinedSuffix = "+cmaes";

    /// <summary>True when the prior is anywhere in this candidate's ancestry —
    /// either the prior itself or a row the prior pulled in.</summary>
    /// <param name="origin">A candidate's origin.</param>
    public static bool IsPrior(string? origin)
        => origin is not null &&
           (origin.StartsWith(Prior, StringComparison.Ordinal) ||
            origin.StartsWith(PriorSeededRetrieval, StringComparison.Ordinal));

    /// <summary>True when this candidate is the prior's OWN rig (refined or not),
    /// as opposed to a library row the prior merely steered towards.</summary>
    /// <param name="origin">A candidate's origin.</param>
    public static bool IsPriorRig(string? origin)
        => origin is not null && origin.StartsWith(Prior, StringComparison.Ordinal);

    /// <summary>True when CMA-ES improved this candidate.</summary>
    /// <param name="origin">A candidate's origin.</param>
    public static bool WasRefined(string? origin)
        => origin is not null && origin.EndsWith(RefinedSuffix, StringComparison.Ordinal);
}

/// <summary>
///     "Match a tone from audio", the no-training route: retrieval over a
///     pre-rendered library, then gradient-free knob refinement over the real
///     renderer with a content-invariant loss. This is ST-ITO (arXiv 2410.21233)
///     transplanted onto our chain, with Okita 2026's "predict then search" as the
///     optional estimator arm.
///     <para>
///         The pipeline, in order: (a) evaluate the caller's prior if it gave one;
///         (b) retrieve the top-k library rigs for the target's features;
///         (c) refine the best few with CMA-ES over
///         <see cref="KnobLayout" />; (d) add the trained estimator's guess when one
///         is available; (e) report every candidate sorted by distance, next to the
///         TARGET'S SELF-DISTANCE — the floor a match cannot beat.
///     </para>
///     <para>
///         <b>A prior does three things, not one.</b> It is candidate zero; it
///         RE-RANKS retrieval, so its family choice steers which library rows are
///         shortlisted at all (<c>RetrievalSeeds</c>); and its own candidate is
///         refined from its knob vector with a WIDER initial sigma than a retrieval
///         hit gets — a guess deserves more exploration than a measurement does.
///         All three are inert when <see cref="ToneMatchRequest.Prior" /> is null,
///         and the pure-audio path is then byte-identical to the pre-prior one.
///     </para>
///     <para>
///         <b>The floor is the honest part.</b> Two different 5 s windows of the
///         target, put through the same distance, give the number a PERFECT match
///         would score: the target's own content variation. A candidate at or below
///         it is as close as this metric can tell, and a UI that reports a raw
///         distance without it is reporting a number with no scale.
///     </para>
///     <para>
///         <b>Everything is counted in RENDERS.</b> Wall time depends on the rig and
///         the machine; renders do not, and they are what a test can assert.
///     </para>
/// </summary>
public sealed class ToneMatcher(
    IRigRenderer renderer,
    IToneSimilarity similarity,
    IRigLibraryProvider library,
    ILocalizationService localization,
    IRigEstimator? estimator = null,
    ToneMatcherOptions? options = null,
    IMatchResultStore? artifacts = null) : IToneMatcher
{
    /// <summary>
    ///     The wall budget used when neither the request nor the options name one.
    ///     <b>It lives here and not as a literal in two places</b> — it was 60 s in
    ///     the constructor while <c>MaxRenders</c> was 400, which at the measured
    ///     2.55 s an evaluation made the cap unreachable and the record misleading.
    ///     Any change to it belongs beside
    ///     <see cref="ToneMatcherOptions.MaxRenders" />'s justification.
    /// </summary>
    public static TimeSpan DefaultWallBudget { get; } = TimeSpan.FromSeconds(150);

    private readonly ToneMatcherOptions _options = options ?? new ToneMatcherOptions();

    /// <inheritdoc />
    public async Task<ToneMatchResult> MatchAsync(
        ToneMatchRequest request,
        IProgress<string>? status,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The library's background grower shares the one offline engine with every
        // render below; held for the whole search so no row is queued in front of
        // a candidate.
        using var growthHold = library.PauseGrowth();
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var budget = new MatchBudget(
            _options.MaxRenders,
            request.Budget ?? _options.Budget ?? DefaultWallBudget,
            sw);

        var extractor = similarity.Extractor;

        // The window the caller picked is a window of SONG, not of playing. Cut the
        // silent head and tail off before anything measures it: the extractor's
        // dynamics and modulation blocks average over the whole buffer (only its
        // spectral blocks gate frames), and the self-distance floor slices the raw
        // head and tail, so a silent lead-in embeds as the all-zero vector and turns
        // the floor into the distance between nothing and something. Reported in the
        // notes, never silent — see MatchTargetAudio.
        var trimmed = MatchTargetAudio.Trim(request.Target);
        if (trimmed.Note is { } trimNote) notes.Add(trimNote);
        var target = trimmed.Content > TimeSpan.Zero ? trimmed.Audio : request.Target;

        var targetFeatures = extractor.Extract(target);
        var targetSpectra = TargetSpectra(request, target);
        var floor = SelfDistance(target, extractor, request.TargetIsRender);
        var targetTooShort = !MatchTargetAudio.IsMeasurable(trimmed);

        // THE 0 % ANCHOR, and it is taken FIRST — before the prior, before retrieval,
        // out of the same budget. One render of the information-free reference rig
        // over the caller's own DI. Without it a percentage has one anchor, which is
        // what made `100 x floor / max(d, floor)` report 100 for everything (W5b);
        // with it, "no better than plugging straight into an amp" has a number.
        // Taking it first is what makes a candidate's SCORE independent of the
        // budget: a tighter budget changes which candidates exist, never what a
        // given one scores.
        ToneFeatures? referenceFeatures = null;
        if (budget.TryTake())
        {
            status?.Report(localization.Format("Progress_MeasuringScale", budget.Used, budget.MaxRenders));
            var referenceWet = await renderer
                .RenderAsync(request.ReferenceDi, MatchScale.ReferenceRig(), ct).ConfigureAwait(false);
            referenceFeatures = extractor.Extract(referenceWet);
        }
        else
        {
            notes.Add("no scale: the budget was gone before the reference rig could be rendered");
        }

        // (a)+(b)+(d) — assemble the candidate structures before spending a render.
        var seeds = new List<(DcRigPreset Rig, string Origin)>();
        var prior = request.Prior;
        if (prior is not null)
        {
            seeds.Add((prior.Rig, string.IsNullOrEmpty(prior.Source)
                ? ToneMatchOrigins.Prior
                : $"{ToneMatchOrigins.Prior}:{prior.Source}"));
        }

        var lib = await library.GetAsync(status, ct).ConfigureAwait(false);
        if (lib.RenderCount > 0) notes.Add($"library built: {lib.RenderCount} renders");
        foreach (var seed in RetrievalSeeds(lib, targetFeatures, prior, notes))
            seeds.Add(seed);

        // A caller that knows the target is a BASS part gets the best bass-family rows
        // as EXTRA seeds (sections lane). The library is drawn from the
        // mixed archetype, so the nearest rows to a bass stem are regularly dark
        // guitar amps — near in the embedding and wrong as an answer. Extra seeds
        // rather than a filter: a hint is knowledge about the instrument, not a
        // measurement of the tone, and it must never remove the row that actually
        // measured closest.
        if (string.Equals(request.StemHint, ToneStems.Bass, StringComparison.OrdinalIgnoreCase))
        {
            var already = new HashSet<DcRigPreset>(seeds.Select(static s => s.Rig));
            var added = 0;
            foreach (var rig in BassSeeds(lib, targetFeatures, similarity))
            {
                if (added >= _options.RetrievalTopK) break;
                // Walk DOWN the bass ranking past anything ordinary retrieval already
                // found, rather than taking the top k and deduplicating them away: when
                // the target really is a bass part the two rankings agree at the top,
                // and taking-then-deduplicating adds nothing in exactly the case the
                // hint was given for.
                if (!already.Add(rig)) continue;
                seeds.Add((rig, "retrieval:bass"));
                added++;
            }

            notes.Add($"bass hint: {added} extra bass-family seeds");
        }

        if (estimator is { IsAvailable: true })
        {
            try
            {
                var estimate = await estimator.EstimateAsync(request.Target, ct).ConfigureAwait(false);
                seeds.Add((estimate.Rig, ToneMatchOrigins.Estimator));
                notes.Add($"estimator {estimate.ModelId} confidence {estimate.Confidence:0.00}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A missing/broken model must not lose the retrieval result.
                notes.Add($"estimator unavailable: {ex.GetType().Name}");
            }
        }

        // CATALOG GATE, before a render is spent on any of them. A rig this app
        // cannot build is REJECTED and NAMED — never clamped into range, never
        // dropped without saying so — because a matcher that hands out gear the
        // catalogue does not have, or a knob outside its declared range, produces a
        // tone the player cannot actually get. Retrieval cannot trip this (every
        // library row is a sampler draw built from the catalogue's own specs); the
        // caller's PRIOR and the estimator's decoded chain can, and nothing else
        // checked them before this.
        for (var i = seeds.Count - 1; i >= 0; i--)
        {
            if (RigCatalogValidation.FirstFault(seeds[i].Rig) is not { } fault) continue;
            notes.Add($"rejected {seeds[i].Origin}: {fault.Message}");
            seeds.RemoveAt(i);
        }

        // Score every seed once.
        var scored = new List<ToneMatchCandidate>();
        for (var i = 0; i < seeds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!budget.TryTake()) break;
            status?.Report(localization.Format("Progress_RenderingCandidate", i + 1, seeds.Count, budget.Used, budget.MaxRenders));
            var (d, wet) = await EvaluateAsync(seeds[i].Rig, request, targetSpectra, targetFeatures, ct).ConfigureAwait(false);
            scored.Add(Candidate(seeds[i].Rig, d, seeds[i].Origin, request,
                new Scoring(targetFeatures, wet, referenceFeatures, floor, targetTooShort)));
        }

        scored.Sort(static (a, b) => a.RankingDistance.CompareTo(b.RankingDistance));

        if (_options.UsePlausibility && _options.PlausibilityWeight > 0.0)
        {
            var markedDown = scored.Count(c => c.PlausibilityPenalty > 0.0);
            if (markedDown > 0)
            {
                var worst = scored.MaxBy(c => c.PlausibilityPenalty)!;
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"plausibility: {markedDown} of {scored.Count} candidates marked down " +
                    $"(worst {worst.PlausibilityPenalty:0.00}: " +
                    $"{string.Join(", ", RigPlausibility.Explain(worst.Rig, request.StemHint).Select(f => f.Code))})"));
            }
        }

        // (c) refine the best few, sharing what is left of the budget equally.
        var refineCount = Math.Min(_options.RefineCandidates, scored.Count);
        var refined = new List<ToneMatchCandidate>();
        var plan = new List<string>();
        for (var i = 0; i < refineCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = budget.Remaining;
            if (remaining <= 0 || budget.Expired) break;
            var share = Math.Max(0, remaining / (refineCount - i));
            var candidate = scored[i];
            var layout = KnobLayout.For(candidate.Rig);
            if (layout.Count == 0) continue; // nothing continuous to tune

            // SHRINK THE POPULATION RATHER THAN SKIPPING THE REFINEMENT. The old gate
            // was a flat `share < 8`: under a tight budget the whole refinement stage
            // silently did nothing and the lane measured retrieval only, which is
            // exactly what the first end-to-end eval had to work around by hand
            // (RetrievalTopK 3 / RefineCandidates 1). A smaller λ is a real CMA-ES run
            // — it explores less per generation and adapts over more of them — and it
            // is strictly better than no run at all. Below CmaEs's own minimum of 4
            // there is genuinely nothing to do.
            var lambda = _options.PopulationSize ?? 4 + (int)Math.Floor(3.0 * Math.Log(layout.Count));
            lambda = Math.Clamp(lambda, 4, Math.Max(4, share));
            if (share < 4) continue;
            plan.Add(string.Create(CultureInfo.InvariantCulture,
                $"{layout.Count}D λ{lambda}×{share / lambda}gen"));

            status?.Report(
                localization.Format("Progress_RefiningCandidate", i + 1, refineCount, budget.Used, budget.MaxRenders));

            // The PRIOR's own candidate starts from the PRIOR'S KNOB VECTOR and gets
            // a wider sigma. The start point is the same array either way — the
            // candidate IS the prior's rig — but saying so explicitly is what keeps
            // it true if a later stage ever rewrites a candidate before refinement,
            // and it is where the wider step size is decided.
            var fromPrior = prior is not null && ToneMatchOrigins.IsPriorRig(candidate.Origin);
            var sigma = fromPrior ? _options.PriorInitialSigma : _options.InitialSigma;
            var seedVector = fromPrior && SameChain(layout.Template, prior!.Rig)
                ? layout.Encode(prior.Rig)
                : layout.Encode();
            if (fromPrior)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"candidate {i + 1} seeded from the prior (sigma {sigma:0.00})"));
            }

            var start = Array.ConvertAll(seedVector, static f => (double)f);

            // The BEST evaluation's feature vector, kept as it goes past. The refined
            // rig has to be SCORED like any other candidate, and its vector was
            // already computed inside the objective — re-rendering it afterwards would
            // spend a render on an answer we already had, and a score derived from a
            // second render of a deterministic pipeline is the same number anyway.
            ToneFeatures? bestWet = null;
            var bestSeen = double.PositiveInfinity;
            var result = await CmaEs.MinimizeAsync(
                async (x, token) =>
                {
                    if (!budget.TryTake()) return double.PositiveInfinity;
                    var rig = layout.Decode(Array.ConvertAll(x, static v => (float)v));
                    var (d, wet) = await EvaluateAsync(rig, request, targetSpectra, targetFeatures, token)
                        .ConfigureAwait(false);
                    if (d < bestSeen) { bestSeen = d; bestWet = wet; }
                    status?.Report(
                        localization.Format("Progress_RefiningCandidate", i + 1, refineCount, budget.Used, budget.MaxRenders));
                    return d;
                },
                start,
                new CmaEsOptions(
                    Seed: _options.Seed + i,
                    PopulationSize: lambda,
                    InitialSigma: sigma,
                    MaxEvaluations: share),
                ct).ConfigureAwait(false);

            if (double.IsFinite(result.BestValue) && result.BestValue < candidate.Distance)
            {
                // The optimiser moves KNOBS only, so the refined rig's structure — and
                // therefore its plausibility penalty — is the one it started from. That
                // is also why the objective above is the RAW distance: a constant scale
                // cannot move an optimum, and comparing against `candidate.Distance`
                // then compares two numbers in one space.
                refined.Add(Candidate(
                    layout.Decode(Array.ConvertAll(result.Best, static v => (float)v)),
                    result.BestValue,
                    candidate.Origin + ToneMatchOrigins.RefinedSuffix,
                    request,
                    bestWet is null
                        ? null
                        : new Scoring(targetFeatures, bestWet, referenceFeatures, floor, targetTooShort)));
            }
            else
            {
                // A refinement that did not beat its start point is DISCARDED, not
                // returned: "a prior that is already right must not be made worse".
                notes.Add($"candidate {i + 1} ({candidate.Origin}) not improved by refinement");
            }
        }

        var all = new List<ToneMatchCandidate>(scored);
        all.AddRange(refined);
        all.Sort(static (a, b) => a.RankingDistance.CompareTo(b.RankingDistance));
        if (all.Count > request.Candidates) all = all.GetRange(0, request.Candidates);

        // The effective PLAN, not the configured one: what was actually affordable
        // once the seeds were paid for. A reader of the notes can then see that a run
        // refined nothing, or refined one candidate for one generation, instead of
        // inferring it from a render count.
        var planDetail = plan.Count > 0
            ? $" [{string.Join(", ", plan)}]"
            : " (budget exhausted by the seeds)";
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"plan: {scored.Count} seeds, {plan.Count} refinement(s){planDetail}, " +
            $"cap {budget.MaxRenders} renders / {budget.Wall.TotalSeconds:0} s"));
        notes.Add($"{budget.Used} renders in {sw.Elapsed.TotalSeconds:0.0} s");

        // The SCALE, in the notes, always — a headline percentage that does not say
        // what its two ends were measured at is the number W5b replaced.
        var anyScore = all.Select(c => c.Score).FirstOrDefault(s => s is not null);
        if (anyScore is not null) notes.Add(MatchScale.DescribeScale(anyScore));

        // The POST-CONDITION, not a repeat of the gate above: the seeds were checked
        // before they were rendered, and a refined rig is the template with clamped
        // coordinates — so a fault here is OUR bug (a decode that escaped a range, a
        // new arm that skipped the gate) and not a caller's. Throwing says so;
        // publishing a rig the app cannot build would not.
        foreach (var candidate in all)
        {
            if (RigCatalogValidation.FirstFault(candidate.Rig) is { } escaped)
            {
                throw new InvalidOperationException(
                    $"The matcher produced a rig outside the DSP module catalogue ({candidate.Origin}): " +
                    escaped.Message);
            }
        }

        var referenceDistance = referenceFeatures is null
            ? double.NaN
            : similarity.Distance(targetFeatures, referenceFeatures);

        var result0 = new ToneMatchResult(all, floor, sw.Elapsed, notes)
        {
            TargetFeatures = targetFeatures.Vector,
            ReferenceFeatures = referenceFeatures?.Vector,
            ReferenceDistance = referenceDistance
        };

        // Persisted so a result can be inspected after the fact. A store that cannot
        // write is a match without a receipt, never a lost match — the path is null
        // and the notes say so.
        if (artifacts is not null)
        {
            var path = artifacts.TryWrite(result0, extractor.Id, request.StemHint, out var why);
            if (path is not null) result0 = result0 with { ArtifactPath = path };
            else if (why is not null) notes.Add("features not persisted: " + why);
        }

        return result0;
    }

    /// <summary>
    ///     The retrieval shortlist, STEERED BY THE PRIOR when there is one.
    ///     <para>
    ///         Without a prior this is the plain top-k, queried at exactly
    ///         <see cref="ToneMatcherOptions.RetrievalTopK" /> — the pure-audio path
    ///         is byte-identical to what it was before priors existed, and
    ///         <c>PriorSteeringTests.NoPrior_ShortlistIsThePlainTopK</c> pins that.
    ///     </para>
    ///     <para>
    ///         With one, a POOL of <c>TopK × PriorRetrievalPoolFactor</c> rows is
    ///         pulled and re-ranked by <c>distance × (1 − w × familyOverlap)</c>, and
    ///         the top k of THAT is the shortlist. This is where the model's
    ///         knowledge actually earns its keep: the library is thousands of rigs
    ///         drawn from a prior over gear, the feature distance alone cannot tell a
    ///         fuzz from a cranked amp at the same brightness, and "it is a bass part
    ///         through an amp and a compressor" narrows that faster than another
    ///         thousand renders would. A row that only reached the shortlist this way
    ///         is marked <see cref="ToneMatchOrigins.PriorSeededRetrieval" />, so the
    ///         steering is visible in the result rather than inferred.
    ///     </para>
    ///     <para>
    ///         The bias is on RANKING only — every shortlisted row is still rendered
    ///         and scored on audio, so a confident, wrong prior costs a few candidate
    ///         slots and never a wrong answer.
    ///     </para>
    /// </summary>
    private IEnumerable<(DcRigPreset Rig, string Origin)> RetrievalSeeds(
        RigLibrary lib, ToneFeatures targetFeatures, ToneMatchPrior? prior, List<string> notes)
    {
        var k = _options.RetrievalTopK;
        if (prior is null || _options.PriorRetrievalPoolFactor <= 1 || _options.PriorFamilyWeight <= 0.0)
        {
            return lib.Query(targetFeatures, k, similarity)
                .Select(hit => (hit.Rig, ToneMatchOrigins.Retrieval));
        }

        var pool = lib.Query(targetFeatures, k * _options.PriorRetrievalPoolFactor, similarity);
        var unbiased = new HashSet<int>(pool.Take(k).Select(h => h.Index));
        var families = Families(prior.Rig);

        var ranked = pool
            .Select(hit => (hit, score: hit.Distance * (1.0 - _options.PriorFamilyWeight * Overlap(hit.Rig, families))))
            .OrderBy(x => x.score)
            .Take(k)
            .ToList();

        var pulled = ranked.Count(x => !unbiased.Contains(x.hit.Index));
        if (pulled > 0)
            notes.Add($"prior steered retrieval: {pulled} of {k} shortlisted rigs share its families");

        return ranked.Select(x => (
            x.hit.Rig,
            unbiased.Contains(x.hit.Index)
                ? ToneMatchOrigins.Retrieval
                : ToneMatchOrigins.PriorSeededRetrieval));
    }

    /// <summary>The distinct module type ids in a rig — its "families" for the
    /// purposes of retrieval steering. Bypassed modules are INCLUDED: a bypassed
    /// boost is still the board the player is describing, and the matcher can
    /// un-bypass nothing, so excluding it would make two rigs that are one switch
    /// apart look unrelated.</summary>
    private static HashSet<string> Families(DcRigPreset rig)
        => new(rig.Modules.Select(m => m.ModuleTypeId), StringComparer.Ordinal);

    /// <summary>Jaccard similarity of a rig's families with the prior's, in
    /// <c>[0,1]</c>. Jaccard rather than "how many of the prior's modules are
    /// present" because the latter scores a twelve-module kitchen sink that happens
    /// to contain the prior's four as a PERFECT match.</summary>
    private static double Overlap(DcRigPreset rig, HashSet<string> priorFamilies)
    {
        if (priorFamilies.Count == 0) return 0.0;
        var mine = Families(rig);
        if (mine.Count == 0) return 0.0;
        var shared = mine.Count(priorFamilies.Contains);
        var union = mine.Count + priorFamilies.Count - shared;
        return union <= 0 ? 0.0 : shared / (double)union;
    }

    /// <summary>True when two rigs have the same module type sequence — the
    /// precondition for encoding one against the other's <see cref="KnobLayout" />.
    /// </summary>
    private static bool SameChain(DcRigPreset a, DcRigPreset b)
    {
        if (a.Modules.Count != b.Modules.Count) return false;
        for (var i = 0; i < a.Modules.Count; i++)
        {
            if (!string.Equals(a.Modules[i].ModuleTypeId, b.Modules[i].ModuleTypeId, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     The amps and drives that make a rig a BASS rig — the same two the sampler's
    ///     <see cref="RigProfileKinds.Bass" /> archetype draws from, and the reason
    ///     the list is here rather than in the sections lane: the thing that decides
    ///     which rows count as bass must live beside the thing that built them.
    /// </summary>
    private static readonly string[] BassModules = ["bassamp", "bassod"];

    /// <summary>Every library row that is a bass rig, nearest first.</summary>
    private static IEnumerable<DcRigPreset> BassSeeds(
        RigLibrary lib, ToneFeatures target, IToneSimilarity similarity)
    {
        var scored = new List<(double Distance, DcRigPreset Rig)>();
        foreach (var entry in lib.Entries)
        {
            if (!IsBassRig(entry.Rig)) continue;
            scored.Add((similarity.Distance(new ToneFeatures(entry.Features, lib.ExtractorId), target), entry.Rig));
        }

        scored.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        return scored.Select(static s => s.Rig);
    }

    private static bool IsBassRig(DcRigPreset rig)
    {
        foreach (var module in rig.Modules)
        {
            foreach (var bass in BassModules)
            {
                if (string.Equals(module.ModuleTypeId, bass, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     A scored candidate: the MEASURED distance, plus the plausibility prior that
    ///     decides where it ranks.
    ///     <para>
    ///         <b>Retrieval, the prior's own rig, the estimator's guess and every
    ///         refined result come through here</b>, which is what makes the prior
    ///         apply to all four rather than to whichever one someone remembered.
    ///     </para>
    /// </summary>
    private ToneMatchCandidate Candidate(
        DcRigPreset rig, double distance, string origin, ToneMatchRequest request, Scoring? scoring = null)
    {
        var penalty = Penalty(rig, request);
        return new ToneMatchCandidate(rig, distance, origin)
        {
            PlausibilityPenalty = penalty,
            RankingDistance = distance * (1.0 + _options.PlausibilityWeight * penalty),
            Features = scoring?.Features.Vector,
            Score = scoring is null
                ? null
                : MatchScale.Score(
                    scoring.Target, scoring.Features, scoring.Reference, similarity,
                    scoring.SelfDistance, scoring.TargetTooShort)
        };
    }

    /// <summary>Everything a candidate needs to be SCORED rather than merely measured:
    /// the two vectors, the run's anchors, and whether the target was long enough to
    /// say anything about. Carried rather than re-derived so the score stays a pure
    /// function of three vectors.</summary>
    private sealed record Scoring(
        ToneFeatures Target,
        ToneFeatures Features,
        ToneFeatures? Reference,
        double SelfDistance,
        bool TargetTooShort);

    /// <summary>The rig's plausibility penalty, or 0 when the option is off — the ONE
    /// place the option is read, so the scale and the published number can never
    /// disagree.</summary>
    private double Penalty(DcRigPreset rig, ToneMatchRequest request)
        => !_options.UsePlausibility || _options.PlausibilityWeight <= 0.0
            ? 0.0
            : RigPlausibility.Penalty(rig, request.StemHint);

    /// <summary>The raw-audio term compares every render of a match with ONE target, so
    /// the target's spectra are taken once per match rather than once per render. Null
    /// when the term does not apply (a recorded target, or a zero weight).</summary>
    private double[]?[]? TargetSpectra(ToneMatchRequest request, MonoAudio target)
        => request.TargetIsRender && _options.SpectralWeight > 0.0
            ? MultiScaleSpectralDistance.Spectra(target)
            : null;

    /// <summary>Render the reference DI through a rig and measure how far the result
    /// is from the target: the feature distance plus a small raw-audio term. This is
    /// the MEASUREMENT and carries no prior — see <see cref="Candidate" />.</summary>
    private async Task<(double Distance, ToneFeatures Wet)> EvaluateAsync(
        DcRigPreset rig,
        ToneMatchRequest request,
        double[]?[]? targetSpectra,
        ToneFeatures targetFeatures,
        CancellationToken ct)
    {
        var wet = await renderer.RenderAsync(request.ReferenceDi, rig, ct).ConfigureAwait(false);
        var wetFeatures = similarity.Extractor.Extract(wet);
        var d = similarity.Distance(wetFeatures, targetFeatures);
        if (targetSpectra is not null)
            d += _options.SpectralWeight * MultiScaleSpectralDistance.Distance(wet, targetSpectra);
        return (d, wetFeatures);
    }

    /// <summary>
    ///     The distance between two DIFFERENT, DISJOINT windows of the target itself,
    ///     measured with the same metric a candidate is measured with — the noise
    ///     floor of the whole comparison.
    ///     <para>
    ///         <b>Returns <see cref="double.NaN" /> — "no floor known" — when the span
    ///         cannot hold two disjoint windows.</b> Returning 0 would be wrong: it is a
    ///         LEGAL floor value meaning "this target is perfectly self-similar", so a
    ///         consumer dividing by it got a wrong answer for a right-looking reason.
    ///         NaN is the only value that cannot be mistaken for a measurement.
    ///     </para>
    ///     <para>
    ///         <b>This is a REAL limit, not a rounding.</b> The first end-to-end eval
    ///         matched a 12 s span with a 5 s window: the windows are disjoint but
    ///         cover 10 of the 12 seconds, so "the target's own variation" is measured
    ///         over nearly the whole clip and the number means very little — on eight
    ///         of twelve rows the "floor" sat ABOVE every candidate's distance. Below
    ///         2x the window it means nothing at all, and saying so is better than
    ///         reporting a floor a candidate routinely beats by 2x.
    ///     </para>
    /// </summary>
    private double SelfDistance(MonoAudio target, IToneFeatureExtractor extractor, bool targetIsRender)
    {
        var window = _options.SelfDistanceWindow ?? TimeSpan.FromSeconds(5);
        var n = (int)Math.Round(window.TotalSeconds * target.SampleRate);
        if (n <= 0 || target.Samples.Length < 2 * n) return double.NaN;

        var a = new MonoAudio(target.Samples[..n], target.SampleRate);
        var b = new MonoAudio(target.Samples[^n..], target.SampleRate);
        var d = similarity.Distance(extractor.Extract(a), extractor.Extract(b));
        // The floor has to live in the SAME space as a candidate's distance — it is
        // the scale that number is read against — so it carries the raw-audio term on
        // exactly the runs where a candidate does.
        if (targetIsRender && _options.SpectralWeight > 0.0)
            d += _options.SpectralWeight * MultiScaleSpectralDistance.Distance(a, b);
        return d;
    }

    /// <summary>Renders and wall time, both counted; whichever runs out first ends
    /// the search. <see cref="TryTake" /> is the ONE place a render is authorised,
    /// so a budget overrun is impossible by construction rather than by review.</summary>
    private sealed class MatchBudget(int maxRenders, TimeSpan wall, Stopwatch clock)
    {
        public int MaxRenders { get; } = maxRenders;
        /// <summary>The wall budget this run was given — reported in the plan note so
        /// a reader can see WHICH bound ended the search.</summary>
        public TimeSpan Wall => wall;
        public int Used { get; private set; }
        public int Remaining => Math.Max(0, MaxRenders - Used);
        public bool Expired => clock.Elapsed >= wall;

        public bool TryTake()
        {
            if (Used >= MaxRenders || Expired) return false;
            Used++;
            return true;
        }
    }
}
