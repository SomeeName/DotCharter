using System.Diagnostics;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     <b>What one evaluation of the matcher actually costs on this machine</b> —
///     one render of the reference medley through a representative rig, plus one
///     <see cref="ToneFeatureExtractor" /> pass over the result. That single number
///     is what every default in <see cref="ToneMatcherOptions" /> is derived from,
///     and it is the thing that drifts: it moves with the box, the build
///     configuration and the rig.
///     <para>
///         <b>This is the re-measurement, not an assertion.</b> It prints and it
///         bounds only loosely — a wall-clock assertion in the gate flakes the moment
///         two suites run in parallel, and this one shares a box with a game. Gated
///         on <c>DOTCHARTER_AUDIO_EVAL=1</c> like everything else in the lane that
///         opens an engine.
///     </para>
///     <para>
///         <b>Run it in RELEASE.</b> Measured 2026-09-12 in Debug it was 6.51 s per
///         evaluation and 2.55 s in Release, of which 0.44 s was the extractor — the
///         Debug figure is not a product measurement and must never be the one a
///         budget is set from.
///     </para>
/// </summary>
[Collection("NativeEngine")] // NullDeviceRigHost sets the process-wide DCA_ENABLE_NULL_DEVICE
public sealed class RenderCostEvalTests
{
    private const string Gate = "DOTCHARTER_AUDIO_EVAL";

    /// <summary>A chain of the shape the converter emits for a real Rocksmith tone,
    /// so the cost is the one the matcher actually pays rather than a
    /// <c>gain</c> node's.</summary>
    private static DcRigPreset RepresentativeRig() => new([
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
    public async Task OneEvaluation_CostsWhatTheBudgetDefaultsAssume()
    {
        if (Environment.GetEnvironmentVariable(Gate) != "1")
            Assert.Skip($"{Gate} is not 1 — this measurement opens a device-less engine.");

        using var host = new NullDeviceRigHost();
        if (!host.IsAvailable)
            Assert.Skip("native DSP ABI (juce_sharp.dll) not loadable: " + host.Describe());

        var ct = TestContext.Current.CancellationToken;
        var renderer = new RigRenderer(host);
        var extractor = new ToneFeatureExtractor();
        var medley = ReferenceDi.Medley();
        var rig = RepresentativeRig();

        // One warm-up: the first render pays a JIT and an engine-prepare that no
        // subsequent evaluation in a match does, and counting it would inflate the
        // number the budget is set from.
        _ = extractor.Extract(await renderer.RenderAsync(medley, rig, ct));

        const int passes = 5;
        var renderMs = 0.0;
        var extractMs = 0.0;
        for (var i = 0; i < passes; i++)
        {
            var sw = Stopwatch.StartNew();
            var wet = await renderer.RenderAsync(medley, rig, ct);
            renderMs += sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            _ = extractor.Extract(wet);
            extractMs += sw.Elapsed.TotalMilliseconds;
        }

        var render = renderMs / passes;
        var extract = extractMs / passes;
        var total = render + extract;
        var defaults = new ToneMatcherOptions();
        var wall = (defaults.Budget ?? ToneMatcher.DefaultWallBudget).TotalSeconds;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"medley {medley.Duration.TotalSeconds:0.0} s | render {render:0} ms + extract {extract:0} ms " +
            $"= {total / 1000.0:0.00} s per evaluation | defaults: MaxRenders {defaults.MaxRenders}, " +
            $"wall {wall:0} s => the wall admits ~{wall * 1000.0 / total:0} evaluations");

        // The ONLY assertion, and it is the one the defaults rest on: the wall budget
        // must be able to spend the render cap, or `MaxRenders` is decorative and a
        // reader of the options believes the search is deeper than it is. A generous
        // factor, because the box is shared.
        Assert.True(wall * 1000.0 >= defaults.MaxRenders * total * 0.5,
            $"the wall budget ({wall:0} s) cannot reach even half of MaxRenders " +
            $"({defaults.MaxRenders}) at {total / 1000.0:0.00} s an evaluation — the two defaults " +
            "disagree and the render cap is decoration.");
    }
}
