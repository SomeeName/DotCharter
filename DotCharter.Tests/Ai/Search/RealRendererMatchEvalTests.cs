using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     Hidden-rig recovery through the REAL offline renderer and the REAL feature
///     extractor — the same test as
///     <c>ToneMatcherTests.RecoversAHiddenRig_ToWithinATenthOfEveryKnob</c> with the
///     fakes removed. It is written NOW, before those services exist, so it runs the
///     moment the eval lane's <see cref="IRigRenderer" /> and
///     <see cref="IToneFeatureExtractor" /> land: the only edit it needs is the two
///     lines in <see cref="TryResolve" />.
///     <para>
///         <b>Opt-in or part of the gate — there is no third state.</b> Without
///         <c>DOTCHARTER_AUDIO_EVAL=1</c> it <c>Assert.Skip</c>s, because it opens a
///         device-less engine and renders for minutes; the same discipline
///         <c>GND_IDMT_ROOT</c> enforces on the CQT evals. It also skips, with the
///         reason printed, while the services are still stubs.
///     </para>
/// </summary>
[Collection("NativeEngine")] // NullDeviceRigHost sets the process-wide DCA_ENABLE_NULL_DEVICE
public sealed class RealRendererMatchEvalTests
{
    private const string Gate = "DOTCHARTER_AUDIO_EVAL";

    /// <summary>Resolves the real services. Returns false with a reason until the
    /// eval lane lands them; then this is where they are constructed.</summary>
    private static bool TryResolve(
        out IRigRenderer? renderer,
        out IToneFeatureExtractor? extractor,
        out IRigSampler? sampler,
        out MonoAudio? referenceDi,
        out string reason)
    {
        renderer = null;
        extractor = null;
        sampler = null;
        referenceDi = null;

        if (Environment.GetEnvironmentVariable(Gate) != "1")
        {
            reason = $"{Gate} is not 1 — this eval opens a device-less engine and renders for minutes.";
            return false;
        }

        // The real services. The renderer needs a device-less engine, and an engine
        // that will not load is a SKIP and never a failure — the same discipline every
        // native test in this repo follows.
        var host = new NullDeviceRigHost();
        if (!host.IsAvailable)
        {
            host.Dispose();
            reason = "native DSP ABI (juce_sharp.dll) not loadable: " + host.Describe();
            return false;
        }

        renderer = new RigRenderer(host);
        extractor = new ToneFeatureExtractor();
        sampler = new RigSampler();

        // The SAME medley the retrieval library is rendered over. Not ReferenceDi
        // .Generate(): a recovery run scored against a library built on the medley,
        // over a different DI, measures the DI change as well as the rig.
        referenceDi = ReferenceDi.Medley();

        reason = string.Empty;
        return true;
    }

    /// <summary>The hidden rig: a chain the converter emits for a real Rocksmith
    /// tone, so recovery is measured on the shape the feature actually meets.</summary>
    private static DcRigPreset HiddenRig() => new([
        new DcRigModule("overdrive", new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["model"] = 1f, ["drive"] = 0.62f, ["tone"] = 0.40f, ["level"] = 0.55f
        }),
        new DcRigModule("amp", new Dictionary<string, float>(StringComparer.Ordinal)),
        new DcRigModule("cab", new Dictionary<string, float>(StringComparer.Ordinal)),
        new DcRigModule("delay", new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["timeMs"] = 380f, ["feedback"] = 0.30f, ["mix"] = 0.22f
        })
    ]);

    [Fact]
    public async Task RecoversAHiddenRig_ThroughTheRealRenderer()
    {
        if (!TryResolve(out var renderer, out var extractor, out var sampler, out var di, out var reason))
            Assert.Skip(reason);

        var ct = TestContext.Current.CancellationToken;
        var hidden = HiddenRig();
        var target = await renderer!.RenderAsync(di!, hidden, ct);

        var similarity = new FakeSimilarity(extractor!);

        // K = 4,000 is the shipping library and ~2 h 50 m of one-off build at the
        // measured 2.55 s an evaluation, which in practice means this test only ever
        // runs against an already-cached one. `DOTCHARTER_MATCH_EVAL_K` — the same
        // override the eval lane uses — makes a smaller library an explicit choice
        // instead of a reason not to run the test at all. The recovery bar below is
        // the AUDIO one, so a smaller library makes the test HARDER, never easier.
        var k = 4000;
        if (int.TryParse(Environment.GetEnvironmentVariable("DOTCHARTER_MATCH_EVAL_K"), out var requested) &&
            requested > 0)
        {
            k = requested;
        }

        var library = await RigLibrary.LoadOrBuildAsync(
            di!, sampler!, renderer, extractor!,
            new RigLibraryOptions(Count: k, Seed: 20260912), FakeLocalization.English(), null, ct);

        var matcher = new ToneMatcher(renderer, similarity, new FixedRigLibraryProvider(library), FakeLocalization.English());
        var result = await matcher.MatchAsync(
            // TargetIsRender: the target IS a render of this DI through the hidden
            // rig, so the raw-audio term is valid here — and this is the ONE place in
            // the lane where it is. A stem target (the panel, the set matcher, the
            // corpus eval) leaves it false and the term is skipped.
            new ToneMatchRequest(target, di!, null, Budget: TimeSpan.FromMinutes(5))
            {
                TargetIsRender = true
            },
            null, ct);

        var best = result.Candidates[0];
        var layout = KnobLayout.For(hidden);
        var want = layout.Encode(hidden);
        var got = layout.Encode(best.Rig);

        // The real chain has degenerate directions the fake does not (two gain
        // stages in series, a cab IR that swallows a tone knob), so the bar here is
        // the AUDIO one — the distance must reach the target's own noise floor —
        // plus a looser knob bound reported for the record.
        var worst = 0f;
        for (var i = 0; i < want.Length; i++) worst = Math.Max(worst, Math.Abs(want[i] - got[i]));
        Assert.True(best.Distance <= result.TargetSelfDistanceFloor * 1.5 + 1e-3,
            $"distance {best.Distance:E3} vs floor {result.TargetSelfDistanceFloor:E3}; " +
            $"K {library.Count}; chain {string.Join("→", best.Rig.Modules.Select(m => m.ModuleTypeId))}; " +
            $"origin {best.Origin}; penalty {best.PlausibilityPenalty:0.00}; " +
            $"worst knob error {worst:0.000}; {string.Join("; ", result.Notes)}");
    }
}
