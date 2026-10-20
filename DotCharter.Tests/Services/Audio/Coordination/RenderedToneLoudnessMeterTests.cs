using DotCharter.Services.Ai.Match;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     The meter's own rules, without the native engine: it measures what the chain
///     renders AFTER the bypass fade-in, drains a ringing tail first, caches per chain,
///     shares one render between concurrent callers, and gives up quietly (null, the
///     estimate stays) when there is no offline rig.
/// </summary>
public sealed class RenderedToneLoudnessMeterTests
{
    /// <summary>A chain whose only stage is a <c>gain</c> of <paramref name="db" />.</summary>
    private static List<DcRigModule> GainChain(float db) =>
        [new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = db })];

    /// <summary>
    ///     A rig host that scales by the chain's <c>gain</c> modules, writes a full-scale
    ///     click into the first 10 ms of every non-empty render (the dry leak through the
    ///     applier's fade-in), and rings a tail for <see cref="TailRenders" /> empty renders
    ///     after a non-empty one.
    /// </summary>
    private sealed class ScalingHost : IOfflineRigHost
    {
        private int _ringing;
        public int TailRenders { get; init; }
        public int PhraseRenders;
        public int EmptyRendersWhileRinging;
        public bool Available { get; init; } = true;
        public ManualResetEventSlim? Hold { get; init; }

        public bool IsAvailable => Available;
        public string Describe() => "fake";

        public void Render(DcRigPreset rig, float[] left, float[] right, int sampleRate)
        {
            if (rig.Modules.Count == 0)
            {
                if (_ringing > 0)
                {
                    _ringing--;
                    EmptyRendersWhileRinging++;
                    left[0] = 0.5f;
                }

                return;
            }

            Hold?.Wait(TimeSpan.FromSeconds(60));
            Interlocked.Increment(ref PhraseRenders);
            var db = rig.Modules.Where(m => m.ModuleTypeId == "gain").Sum(m => m.Params["gainDb"]);
            var g = MathF.Pow(10f, db / 20f);
            for (var i = 0; i < left.Length; i++) left[i] *= g;
            for (var i = 0; i < sampleRate / 100; i++) left[i] = 1f;
            _ringing = TailRenders;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task ItMeasuresThePhrase_NotTheFadeInLeak()
    {
        var meter = new RenderedToneLoudnessMeter(new ScalingHost());
        var unity = (await meter.MeasureAsync(GainChain(0f), TestContext.Current.CancellationToken))!.Value;
        var phrase = CalibrationPhrase.Samples;

        Assert.Equal(IntegratedLoudness.Measure(phrase, CalibrationPhrase.SampleRate), unity.Lufs, 3);
        // The 0 dBFS click sits in the pre-roll and is not measured.
        Assert.Equal(IntegratedLoudness.PeakDbfs(phrase), unity.PeakDbfs, 3);

        var minus6 = (await meter.MeasureAsync(GainChain(-6f), TestContext.Current.CancellationToken))!.Value;
        Assert.Equal(unity.Lufs - 6, minus6.Lufs, 2);
    }

    [Fact]
    public async Task APreviousTail_IsDrainedBeforeTheNextMeasurement()
    {
        var host = new ScalingHost { TailRenders = 3 };
        var meter = new RenderedToneLoudnessMeter(host);
        await meter.MeasureAsync(GainChain(0f), TestContext.Current.CancellationToken);
        await meter.MeasureAsync(GainChain(-3f), TestContext.Current.CancellationToken);
        Assert.Equal(3, host.EmptyRendersWhileRinging);
    }

    [Fact]
    public async Task AChain_IsRenderedOnce_AndThenAnsweredFromTheCache()
    {
        var host = new ScalingHost();
        var meter = new RenderedToneLoudnessMeter(host);
        Assert.Null(meter.TryGetCached(GainChain(-2f)));

        var first = await meter.MeasureAsync(GainChain(-2f), TestContext.Current.CancellationToken);
        var second = await meter.MeasureAsync(GainChain(-2f), TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(first, meter.TryGetCached(GainChain(-2f)));
        Assert.Equal(1, host.PhraseRenders);
        Assert.Equal(1, meter.RenderCount);
        // A chain that differs by a hair is another chain.
        Assert.Null(meter.TryGetCached(GainChain(-2.0001f)));
    }

    [Fact]
    public async Task ConcurrentRequests_ForOneChain_ShareOneRender()
    {
        using var hold = new ManualResetEventSlim(false);
        var host = new ScalingHost { Hold = hold };
        var meter = new RenderedToneLoudnessMeter(host);

        var a = meter.MeasureAsync(GainChain(1f), TestContext.Current.CancellationToken);
        var b = meter.MeasureAsync(GainChain(1f), TestContext.Current.CancellationToken);
        hold.Set();
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, host.PhraseRenders);
    }

    [Fact]
    public async Task NoOfflineRig_AnswersNull_AndStopsAsking()
    {
        var host = new ScalingHost { Available = false };
        var meter = new RenderedToneLoudnessMeter(host);
        Assert.Null(await meter.MeasureAsync(GainChain(0f), TestContext.Current.CancellationToken));
        Assert.Null(await meter.MeasureAsync(GainChain(1f), TestContext.Current.CancellationToken));
        Assert.Equal(0, host.PhraseRenders);
    }

    [Fact]
    public async Task ASilentChain_IsMeasuredAsSilence_NotAsAHugeBoost()
    {
        var meter = new RenderedToneLoudnessMeter(new ScalingHost());
        var silent = (await meter.MeasureAsync(GainChain(-400f), TestContext.Current.CancellationToken))!.Value;
        Assert.False(silent.IsMeasured);
    }
}
