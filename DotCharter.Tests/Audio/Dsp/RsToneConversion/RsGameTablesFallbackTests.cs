using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     The converter WITHOUT the game tables (#755: no install, or no key file) against
///     the same gear WITH them (the synthetic install): every gear the game's own numbers
///     drive falls back to the generic, label-reading mapping it had before, and never
///     throws. Each case asserts both sides, so a fallback that silently kept a game
///     path — or a game path that stopped reading the tables — fails here.
/// </summary>
public sealed class RsGameTablesFallbackTests
{
    private static IReadOnlyList<DcRigModule> Convert(RsGear gear, out ConversionReport report)
    {
        report = new ConversionReport();
        return RsToneConverter.ConvertGear(gear, RsGearCatalog.Shared, RsGearOverrides.Shared, report);
    }

    private static RsPedal Pedal(string key, params (string Pot, float Value)[] knobs)
    {
        Assert.True(RsGearCatalog.Shared.TryGetGear(key, out var gear), key);
        var type = gear!.Type == "Racks" ? RsGearType.Rack : RsGearType.Pedal;
        return new RsPedal(type == RsGearType.Rack ? RsGearRole.Rack : RsGearRole.PrePedal, 1, key, type, gear.Category,
            knobs.ToDictionary(k => key + "_" + k.Pot, k => k.Value, StringComparer.Ordinal));
    }

    private static (IReadOnlyList<DcRigModule> Game, ConversionReport GameReport, IReadOnlyList<DcRigModule> Generic, ConversionReport GenericReport)
        Both(RsGear gear)
    {
        IReadOnlyList<DcRigModule> game, generic;
        ConversionReport gameReport, genericReport;
        using (SyntheticRsGame.Use()) game = Convert(gear, out gameReport);
        using (SyntheticRsGame.UseNone()) generic = Convert(gear, out genericReport);
        return (game, gameReport, generic, genericReport);
    }

    [Fact]
    public void AnAmp_RunsItsGameChain_OnlyWithTheTables()
    {
        var (game, _, generic, _) = Both(new RsAmp("Amp_TestA", "Amp", new Dictionary<string, float>()));
        Assert.Equal([DcRigTargetCatalog.RsAmpChain], game.Select(m => m.ModuleTypeId));
        Assert.Single(generic);
        Assert.NotEqual(DcRigTargetCatalog.RsAmpChain, generic[0].ModuleTypeId);
    }

    [Fact]
    public void StudioEq_FollowsTheGamesCurve_OnlyWithTheTables()
    {
        var (game, _, generic, _) = Both(Pedal(RsGameCurves.StudioEqKey, ("Bass", 3), ("BassFreq", 215)));
        var curve = MathF.Sqrt(30f * 400f);   // the synthetic curve at its knob's middle
        Assert.Equal(curve, game[0].Params["lowFreq"], 0.05f);
        Assert.Equal(DcRigTargetCatalog.Eq, generic[0].ModuleTypeId);
        Assert.NotEqual(curve, generic[0].Params["lowFreq"], 0.05f);
    }

    [Fact]
    public void Compression_FollowsTheGamesCurves_OnlyWithTheTables()
    {
        var (game, _, generic, _) = Both(Pedal(RsGameCurves.CompressionKey, ("Comp", 50), ("Attack", 50), ("Release", 50)));
        Assert.Equal(-25f, game[0].Params["thresholdDb"], 0.01f);
        Assert.Equal(46f, game[0].Params["attackMs"], 0.01f);
        Assert.Equal(DcRigTargetCatalog.Compressor, generic[0].ModuleTypeId);
        Assert.NotEqual(-25f, generic[0].Params.GetValueOrDefault("thresholdDb", float.NaN), 0.01f);
    }

    [Fact]
    public void LoFiFilter_IsTwoGameCorners_OnlyWithTheTables()
    {
        var (game, _, generic, _) = Both(Pedal(RsGameCurves.LoFiFilterKey, ("Mix", 50), ("FilterType", 50)));
        Assert.Equal([DcRigTargetCatalog.Svf, DcRigTargetCatalog.Svf], game.Select(m => m.ModuleTypeId));
        Assert.Equal(1050f, game[0].Params["cutoffHz"], 0.5f);
        Assert.Single(generic);
    }

    [Fact]
    public void BassEmulator_IsTheGamesHarmonizerAndFilter_OnlyWithTheTables()
    {
        var (game, _, generic, _) = Both(Pedal(RsGameCurves.BassEmulatorKey, ("Body", 100), ("Tone", 100)));
        Assert.Equal([DcRigTargetCatalog.Harmonizer, DcRigTargetCatalog.Eq], game.Select(m => m.ModuleTypeId));
        Assert.Equal(6f, game[1].Params["midGainDb"], 0.01f);
        Assert.NotEqual(game.Select(m => m.ModuleTypeId), generic.Select(m => m.ModuleTypeId));
    }

    [Fact]
    public void BitCruncher_SaysItIsGeneric_WithoutTheTables()
    {
        var (game, gameReport, generic, genericReport) = Both(Pedal(RsGameCurves.BitCruncherKey, ("Mix", 40)));
        Assert.Equal(7f + RsGameCurves.GameLevelDb, game[0].Params["inputDb"], 0.01f);
        Assert.DoesNotContain(gameReport.Notes, n => n.Message.Contains("Rocksmith 2014 installation", StringComparison.Ordinal));

        Assert.Equal(DcRigTargetCatalog.BitCrusher, generic[0].ModuleTypeId);
        Assert.Equal(0.4f, generic[0].Params["mix"], 0.01f);
        Assert.Contains(genericReport.Notes, n => n.Message.Contains("need a Rocksmith 2014 installation", StringComparison.Ordinal));
    }

    [Fact]
    public void BassOverdrive_UsesTheGamesBlend_OnlyWithTheTables()
    {
        var (_, gameReport, generic, genericReport) = Both(Pedal(RsGameCurves.BassOverdriveKey, ("Gain", 50), ("Blend", 50), ("Tone", 50)));
        Assert.Contains(gameReport.Notes, n => n.Message.StartsWith("bass overdrive: the game's blend", StringComparison.Ordinal));
        Assert.Equal(DcRigTargetCatalog.BassOd, generic[0].ModuleTypeId);
        Assert.DoesNotContain(genericReport.Notes, n => n.Message.StartsWith("bass overdrive: the game's blend", StringComparison.Ordinal));
    }

    [Fact]
    public void AcousticEmulator_GetsTheGamesMidBand_OnlyWithTheTables()
    {
        var (game, _, generic, _) = Both(Pedal(RsGameCurves.AcousticEmulatorKey, ("Mid", 1), ("MidShift", 100)));
        Assert.Equal([DcRigTargetCatalog.AcousticSim, DcRigTargetCatalog.Svf], game.Select(m => m.ModuleTypeId));
        Assert.Equal(100f, game[1].Params["cutoffHz"], 0.05f);
        Assert.Equal([DcRigTargetCatalog.AcousticSim], generic.Select(m => m.ModuleTypeId));
    }

    [Fact]
    public void AFlangerChorus_ReadsItsBank_OnlyWithTheTables()
    {
        var (game, gameReport, generic, _) = Both(Pedal("Pedal_Chorus", ("Mix", 50), ("Depth", 50), ("Rate", 4)));
        Assert.Equal(DcRigTargetCatalog.Chorus, game[0].ModuleTypeId);
        Assert.Equal(1f, game[0].Params["voices"]);
        // Depth 50 % of the synthetic 10 ms delay, half each side.
        Assert.Equal(2.5f, game[0].Params["depthMs"], 0.01f);
        Assert.Equal(DcRigTargetCatalog.Chorus, generic[0].ModuleTypeId);
        Assert.NotEqual(2.5f, generic[0].Params.GetValueOrDefault("depthMs", float.NaN), 0.01f);
        Assert.Contains(gameReport.Notes, n => n.Message.Contains("chorus delay", StringComparison.Ordinal));
    }

    [Fact]
    public void AWholeToneWithEveryGameGear_ConvertsWithoutTheTables()
    {
        using var none = SyntheticRsGame.UseNone();
        var keys = new[]
        {
            RsGameCurves.StudioEqKey, RsGameCurves.CompressionKey, RsGameCurves.LoFiFilterKey, RsGameCurves.BassEmulatorKey,
            RsGameCurves.BitCruncherKey, RsGameCurves.BassOverdriveKey, RsGameCurves.AcousticEmulatorKey
        }.Concat(RsGameCurves.ChorusKeys);
        foreach (var key in keys)
        {
            var modules = Convert(Pedal(key), out _);
            Assert.All(modules, m => Assert.Contains(m.ModuleTypeId, DcRigTargetCatalog.ModuleTypeIds));
        }
    }
}
