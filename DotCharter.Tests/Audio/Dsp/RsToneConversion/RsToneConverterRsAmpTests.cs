using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     Every Rocksmith amp converts to <c>rsamp</c>, the game's own chain (#88):
///     the row, each knob's position on the game's own range, and a report that
///     names, knob by knob, what the clone of the chain cannot do exactly. The rows
///     are the player's install's (#755); here, the synthetic install's
///     (<see cref="SyntheticRsGame" />). What the chain then DOES with a knob is the
///     native table's job and is measured in JuceSharp's <c>GearRsAmpTests</c>.
/// </summary>
public sealed class RsToneConverterRsAmpTests
{
    private static IReadOnlyList<DcRigModule> Gear(RsAmp amp, out ConversionReport report)
    {
        report = new ConversionReport();
        return RsToneConverter.ConvertGear(amp, RsGearCatalog.Shared, RsGearOverrides.Shared, report);
    }

    private static RsAmp Amp(string key, params (string Pot, float Value)[] pots)
        => new(key, "Amp", pots.ToDictionary(p => key + "_" + p.Pot, p => p.Value, StringComparer.Ordinal));

    public static TheoryData<string> EveryAmp
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (var m in SyntheticRsGame.Tables.AmpModels) d.Add(m.Key);
            return d;
        }
    }

    [Theory]
    [MemberData(nameof(EveryAmp))]
    public void EveryAmp_BecomesItsRow_WithEveryKnobAtItsPosition(string key)
    {
        using var game = SyntheticRsGame.Use();
        Assert.True(RsAmpChains.TryGetModel(key, out var model, out var row));
        // Each knob at 30 % of its travel, given by key.
        var pots = row!.Knobs.Select(k => (k.Key[(key.Length + 1)..], k.Min + 0.3f * (k.Max - k.Min))).ToArray();
        var modules = Gear(Amp(key, pots), out _);

        var amp = Assert.Single(modules);
        Assert.Equal("rsamp", amp.ModuleTypeId);
        Assert.Equal(model, amp.Params["model"]);
        for (var i = 0; i < row.Knobs.Count; i++)
            Assert.Equal(0.3f, amp.Params[RsToneConverter.RsAmpKnobParams[i]], 1e-4f);
        Assert.Equal(row.Knobs.Count + 1, amp.Params.Count);
    }

    [Fact]
    public void AMissingKnob_IsTheManifestsDefault()
    {
        using var game = SyntheticRsGame.Use();
        var modules = Gear(Amp("Amp_TestA"), out _);
        // Gain defaults to 5 of 1..10, Bass to 10 of 0..20.
        Assert.Equal(4f / 9f, modules[0].Params["k1"], 1e-5f);
        Assert.Equal(0.5f, modules[0].Params["k2"], 1e-5f);
    }

    [Fact]
    public void AKnobThatOnlyMovesEqBands_IsNotReported_AndAGainKnobIs()
    {
        using var game = SyntheticRsGame.Use();
        Gear(Amp("Amp_TestA"), out var report);
        var messages = report.Notes.Select(n => n.Message).ToList();

        // (A knob the gear catalogue does not know is named by its key.) Bass moves a
        // Guitar Distortion EQ band and a FutzBox level: the bank's numbers through
        // documented stages.
        Assert.DoesNotContain(messages, m => m.StartsWith("knob 'Amp_TestA_Bass'", StringComparison.Ordinal));
        // Gain drives the drive stage, the FutzBox and an S-curve.
        var gain = Assert.Single(messages, m => m.StartsWith("knob 'Amp_TestA_Gain'", StringComparison.Ordinal));
        Assert.Contains("drive law is inferred", gain, StringComparison.Ordinal);
        Assert.Contains("FutzBox distortion", gain, StringComparison.Ordinal);
        Assert.Contains("S-curve", gain, StringComparison.Ordinal);
        // Its drive stage is the game's Mac stand-in for Trash.
        Assert.Contains(messages, m => m.StartsWith("drive stage: the game's own Mac stand-in", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAmpWithNoTrashStage_SaysNothingAboutAMacStandIn()
    {
        using var game = SyntheticRsGame.Use();
        Gear(Amp("DI_Amp_TestC"), out var report);
        Assert.DoesNotContain(report.Notes, n => n.Message.Contains("Mac stand-in", StringComparison.Ordinal));
    }

    /// <summary>The knobs #70 and #72 left FOLDED — each now moves its own kN,
    /// i.e. reaches the game's own curves. Owner-only: the rows are the install's.</summary>
    [Theory]
    [InlineData("Amp_HG500", "MidFreq", 4)]
    [InlineData("Amp_OrangeOR50H", "Punch", 5)]
    [InlineData("Bass_Amp_HT100B", "Pres", 5)]
    [InlineData("Bass_Amp_CH300B", "Enhance", 6)]
    [InlineData("Bass_Amp_LT25B", "MidFreq", 4)]
    [InlineData("Amp_EN50", "Bright", 5)]
    [InlineData("Amp_MarshallDSL100H", "Res", 6)]
    public void AFormerlyFoldedKnob_Lands(string key, string pot, int k)
    {
        using var tables = TestRsGameTables.UseReal();
        Assert.True(RsAmpChains.TryGetModel(key, out _, out var row));
        var knob = row!.Knobs[k - 1];
        Assert.Equal(key + "_" + pot, knob.Key);
        Assert.NotEqual(RsAmpStage.None, knob.Stages);

        var lo = Gear(Amp(key, (pot, knob.Min)), out _)[0].Params["k" + k];
        var hi = Gear(Amp(key, (pot, knob.Max)), out _)[0].Params["k" + k];
        Assert.Equal(0f, lo);
        Assert.Equal(1f, hi);
    }
}
