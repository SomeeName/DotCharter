using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     The renderer's own rules, over a fake host — level matching, the mono
///     convention, the scrub, and that a render is a pure function of (DI, rig).
///     The native engine gets <see cref="RigRendererNativeTests" />.
/// </summary>
public sealed class RigRendererTests
{
    /// <summary>
    ///     A host that is deliberately STATEFUL: each render adds a little more of a
    ///     running accumulator, exactly the way a delay line or a reverb tank carries
    ///     state across calls. If <see cref="RigRenderer" /> ever stops asking for a
    ///     rebuild, or a future host stops honouring it, this fake makes the second
    ///     render differ from the first — which is the failure that is otherwise
    ///     invisible until a search lane reports that a rig it already tried scores
    ///     differently the second time.
    /// </summary>
    private sealed class SpyHost : IOfflineRigHost
    {
        private double _state;

        public int Renders { get; private set; }

        public int LastRate { get; private set; }

        public bool IsAvailable => true;

        public string Describe() => "spy";

        public void Render(DcRigPreset rig, float[] left, float[] right, int sampleRate)
        {
            Renders++;
            LastRate = sampleRate;
            _state += 0.25;
            var gain = rig.Modules.Count + 1.0;
            for (var i = 0; i < left.Length; i++)
            {
                // The state is a SHAPE change (a leaking offset), not a gain, so that
                // level matching downstream cannot hide it — a gain-only difference
                // would be normalised away and the vacuity test below would pass for
                // the wrong reason.
                left[i] = (float)(left[i] * gain + _state * 0.02);
                right[i] = (float)(right[i] * gain * -1.0); // out of phase, on purpose
            }
        }

        /// <summary>What a rebuild would do. The renderer cannot call this — only the
        /// host can — so it stands in for "the host resets between renders".</summary>
        public void ResetState() => _state = 0;

        public void Dispose() { }
    }

    private static DcRigPreset Rig(params string[] modules) =>
        new([.. modules.Select(m => new DcRigModule(m, new Dictionary<string, float>()))]);

    [Fact]
    public async Task Render_IsLevelMatchedToTheAnalysisLevel()
    {
        var renderer = new RigRenderer(new SpyHost());

        var wet = await renderer.RenderAsync(ReferenceDi.Generate(), Rig("amp", "cab"), TestContext.Current.CancellationToken);

        double sq = 0, peak = 0;
        foreach (var s in wet.Samples) { sq += s * (double)s; peak = Math.Max(peak, Math.Abs(s)); }
        var rmsDb = 20 * Math.Log10(Math.Sqrt(sq / wet.Samples.Length));

        Assert.Equal(LevelMatch.TargetRmsDbfs, rmsDb, 1);
        Assert.True(20 * Math.Log10(peak) > LevelMatch.PeakCeilingDbfs - 6,
            "the peak guard bound on ordinary DI material; the RMS target has no crest headroom.");
        Assert.True(20 * Math.Log10(peak) <= LevelMatch.PeakCeilingDbfs + 1e-6,
            "the render was left above the peak ceiling.");
    }

    /// <summary>
    ///     The LEFT plane is the answer. The fake deliberately puts the right plane
    ///     out of phase; a renderer that folded the two would hand back silence, which
    ///     is precisely what the native <c>channels: 1</c> convenience does to a
    ///     <c>widener</c> or a ping-pong <c>delay</c>.
    /// </summary>
    [Fact]
    public async Task Render_TakesTheLeftPlaneAndDoesNotFoldTheStereoImage()
    {
        var renderer = new RigRenderer(new SpyHost());

        var wet = await renderer.RenderAsync(ReferenceDi.Generate(), Rig("widener"), TestContext.Current.CancellationToken);

        Assert.Contains(wet.Samples, s => Math.Abs(s) > 0.01f);
    }

    [Fact]
    public async Task SameDiAndRig_RenderBitIdentically_WhenTheHostRebuilds()
    {
        var host = new SpyHost();
        var renderer = new RigRenderer(host);
        var di = ReferenceDi.Generate();
        var rig = Rig("overdrive", "amp", "cab");

        var first = await renderer.RenderAsync(di, rig, TestContext.Current.CancellationToken);
        host.ResetState();
        var second = await renderer.RenderAsync(di, rig, TestContext.Current.CancellationToken);

        Assert.Equal(first.Samples, second.Samples);
        Assert.Equal(2, host.Renders);
        Assert.Equal(RigRenderer.RenderRate, host.LastRate);
    }

    /// <summary>
    ///     And the same call WITHOUT the reset must differ — otherwise the test above
    ///     would pass for a renderer that ignored the host entirely, and the whole
    ///     determinism claim would be vacuous.
    /// </summary>
    [Fact]
    public async Task TheDeterminismTestIsNotVacuous_AStatefulHostChangesItsAnswer()
    {
        var renderer = new RigRenderer(new SpyHost());
        var di = ReferenceDi.Generate();
        var rig = Rig("overdrive");

        var first = await renderer.RenderAsync(di, rig, TestContext.Current.CancellationToken);
        var second = await renderer.RenderAsync(di, rig, TestContext.Current.CancellationToken);

        Assert.Equal(first.Samples.Length, second.Samples.Length);
        Assert.NotEqual(first.Samples, second.Samples);
    }

    [Fact]
    public async Task NonFiniteSamplesFromARig_AreScrubbedRatherThanPropagated()
    {
        var renderer = new RigRenderer(new PoisonHost());

        var wet = await renderer.RenderAsync(ReferenceDi.Generate(), Rig("fuzz"), TestContext.Current.CancellationToken);

        Assert.All(wet.Samples, s => Assert.True(float.IsFinite(s), "a non-finite sample survived the render."));
    }

    private sealed class PoisonHost : IOfflineRigHost
    {
        public bool IsAvailable => true;

        public string Describe() => "poison";

        public void Render(DcRigPreset rig, float[] left, float[] right, int sampleRate)
        {
            for (var i = 0; i < left.Length; i++) left[i] *= 0.5f;
            left[left.Length / 2] = float.NaN;
            left[left.Length / 3] = float.PositiveInfinity;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task AnUnavailableHost_FailsLoudlyInsteadOfReturningSilence()
    {
        var renderer = new RigRenderer(new DeadHost());

        Assert.False(renderer.IsAvailable);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => renderer.RenderAsync(ReferenceDi.Generate(), Rig("amp"), TestContext.Current.CancellationToken));
        Assert.Contains("no native engine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DeadHost : IOfflineRigHost
    {
        public bool IsAvailable => false;

        public string Describe() => "no native engine";

        public void Render(DcRigPreset rig, float[] left, float[] right, int sampleRate) =>
            throw new InvalidOperationException("The offline rig host is not available: no native engine");

        public void Dispose() { }
    }
}
