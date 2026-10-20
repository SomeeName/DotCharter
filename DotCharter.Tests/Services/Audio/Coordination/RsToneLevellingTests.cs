using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>The levelling pass over a song's converted tones: cache-only, alias-safe,
/// idempotent, and hands-off for anything that is not a measured conversion.</summary>
public sealed class RsToneLevellingTests
{
    private static DcRigPreset Converted(float ampGain, float estimateDb) => new([
        new DcRigModule("amp", new Dictionary<string, float> { ["gain"] = ampGain }),
        new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = estimateDb })
    ]);

    private static async Task<FakeToneLoudnessMeter> MeterWith(params DcRigPreset[] measured)
    {
        var meter = new FakeToneLoudnessMeter { Answer = body => new ToneLoudness(-20 - 10 * body[0].Params["gain"], -12) };
        foreach (var p in measured) await meter.MeasureAsync(RsToneLevelling.Body(p)!, TestContext.Current.CancellationToken);
        return meter;
    }

    [Fact]
    public async Task AMeasuredTone_GetsThePolicysGain_AnUnmeasuredOneKeepsItsEstimate_AndIsPending()
    {
        var measured = Converted(0.2f, 1f);
        var unmeasured = Converted(0.7f, 2f);
        var meter = await MeterWith(measured);
        var levelling = new RsToneLevelling(meter);
        var converted = new Dictionary<string, DcRigPreset> { ["a"] = measured, ["b"] = unmeasured };
        var volumes = new Dictionary<string, float> { ["a"] = -15f, ["b"] = -15f };

        var levelled = levelling.Level(converted, volumes, out var pending);

        var intents = RsToneLoudnessPolicy.IntentOffsetsDb([-15f, -15f]);
        Assert.Equal(RsToneLoudnessPolicy.TrailingGainDb(new ToneLoudness(-22, -12), intents[0]),
            RsToneLevelling.TrailingGainDb(levelled["a"])!.Value, 4);
        Assert.Same(unmeasured, levelled["b"]);
        var body = Assert.Single(pending);
        Assert.Equal(ToneLoudnessKey.For(RsToneLevelling.Body(unmeasured)!), ToneLoudnessKey.For(body));
    }

    [Theory]
    [InlineData(-58.0, 24f, 10f)]    // 34 dB short: the trailing gain's 24, the amp chain's trim the other 10
    [InlineData(-30.0, 6f, 0f)]      // inside the trailing gain's range: the trim is not touched
    [InlineData(-90.0, 24f, 24f)]    // 54 dB short: both at their ±24
    public async Task WhatTheTrailingGainCannotGive_GoesIntoTheAmpChainsTrim(double lufs, float trailing, float trim)
    {
        // #88: a Rocksmith amp's own chain at a clean setting can sit 30+ dB under the
        // reference; the trailing gain stops at ±24 dB.
        var tone = new DcRigPreset([
            new DcRigModule("rsamp", new Dictionary<string, float> { ["model"] = 17f, ["k1"] = 0f }),
            new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = 3f })
        ]);
        var meter = new FakeToneLoudnessMeter { Answer = _ => new ToneLoudness(lufs, -60) };
        await meter.MeasureAsync(RsToneLevelling.Body(tone)!, TestContext.Current.CancellationToken);

        var levelled = new RsToneLevelling(meter).Level(
            new Dictionary<string, DcRigPreset> { ["a"] = tone }, new Dictionary<string, float>(), out _)["a"];

        Assert.Equal(trailing, RsToneLevelling.TrailingGainDb(levelled)!.Value, 3);
        Assert.Equal(trim, RsToneLevelling.AmpLevelDb(levelled)!.Value, 3);
    }

    [Fact]
    public async Task AToneWithoutAnAmpChain_KeepsTheClamp()
    {
        var tone = Converted(0.2f, 3f);
        var meter = new FakeToneLoudnessMeter { Answer = _ => new ToneLoudness(-70, -60) };
        await meter.MeasureAsync(RsToneLevelling.Body(tone)!, TestContext.Current.CancellationToken);

        var levelled = new RsToneLevelling(meter).Level(
            new Dictionary<string, DcRigPreset> { ["a"] = tone }, new Dictionary<string, float>(), out _)["a"];

        Assert.Equal(24f, RsToneLevelling.TrailingGainDb(levelled)!.Value, 3);
        Assert.Null(RsToneLevelling.AmpLevelDb(levelled));
    }

    [Fact]
    public async Task EveryAliasOfATone_PointsAtOneLevelledPreset_AndLevellingTwiceChangesNothing()
    {
        var tone = Converted(0.4f, 0f);
        var levelling = new RsToneLevelling(await MeterWith(tone));
        var converted = new Dictionary<string, DcRigPreset> { ["key"] = tone, ["Name"] = tone };
        var volumes = new Dictionary<string, float> { ["key"] = -12f, ["Name"] = -12f };

        var once = levelling.Level(converted, volumes, out _);
        var twice = levelling.Level(converted, volumes, out _);

        Assert.Same(once["key"], once["Name"]);
        Assert.NotSame(tone, once["key"]);
        Assert.Equal(RsToneLevelling.TrailingGainDb(once["key"]), RsToneLevelling.TrailingGainDb(twice["key"]));
        // Only the trailing gain moved.
        Assert.Equal(tone.Modules[0], once["key"].Modules[0]);
    }

    [Fact]
    public async Task ARigWithNoTrailingGain_OrASilentMeasurement_IsLeftAlone()
    {
        var noGain = new DcRigPreset([new DcRigModule("amp", new Dictionary<string, float> { ["gain"] = 0.5f })]);
        var silentTone = Converted(0.9f, 4f);
        var meter = new FakeToneLoudnessMeter { Answer = _ => new ToneLoudness(IntegratedLoudness.Silence, double.NegativeInfinity) };
        await meter.MeasureAsync(RsToneLevelling.Body(silentTone)!, TestContext.Current.CancellationToken);
        var levelling = new RsToneLevelling(meter);

        var levelled = levelling.Level(
            new Dictionary<string, DcRigPreset> { ["n"] = noGain, ["s"] = silentTone },
            new Dictionary<string, float>(), out var pending);

        Assert.Same(noGain, levelled["n"]);
        Assert.Same(silentTone, levelled["s"]);
        Assert.Empty(pending);
    }

    [Fact]
    public void WithNoMeter_NothingChanges()
    {
        var tone = Converted(0.4f, 0f);
        var levelled = new RsToneLevelling(null).Level(
            new Dictionary<string, DcRigPreset> { ["a"] = tone }, new Dictionary<string, float>(), out var pending);
        Assert.Same(tone, levelled["a"]);
        Assert.Empty(pending);
    }
}
