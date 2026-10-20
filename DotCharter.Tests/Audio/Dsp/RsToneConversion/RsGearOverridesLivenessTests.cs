using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
/// Liveness + coverage gate over the embedded <c>rs_gear_overrides.json</c>:
/// every override key must resolve against the real RS gear catalogue (a typo
/// or a renamed key fails here forever after), and the cab table must cover
/// the ENTIRE catalogue so no CDLC cab ever falls back to the generic substring
/// guess. Amps have no rows here since #88: every amp the game ships runs its
/// own soundbank chain (<c>RsAmpChains</c>).
/// </summary>
public sealed class RsGearOverridesLivenessTests
{
    private static readonly RsGearCatalog Catalog = RsGearCatalog.Shared;
    private static readonly RsGearOverrides Overrides = RsGearOverrides.Shared;

    private static IEnumerable<RsGearData> GearOfType(string type)
        => Catalog.AllGear.Where(g => g.Type == type);

    [Fact]
    public void SharedOverrides_LoadedNonEmpty()
    {
        // Guards the loader itself: an unparseable embedded resource degrades to
        // Empty, which would otherwise pass the per-key liveness checks vacuously.
        Assert.NotEmpty(Overrides.CabModelStems);
        Assert.NotEmpty(Overrides.TargetModuleMap);
    }

    // --------------------------------------------------------------- liveness

    [Fact]
    public void CabModel_EveryStemPrefixesARealCatalogueCab()
    {
        var cabKeys = GearOfType("Cabinets").Select(g => g.Key).ToArray();
        foreach (var (stem, model) in Overrides.CabModelStems)
        {
            Assert.True(cabKeys.Any(k => k.StartsWith(stem, StringComparison.Ordinal)),
                $"cabModel stem '{stem}' matches no catalogue cabinet key");
            Assert.InRange(model, 0, 25); // the cab IR roster (dcdsp_cab_irs.h; 12-25 since #74)
        }
    }

    [Fact]
    public void ForcedDriveSets_AreRealDistortionCategoryGear()
    {
        // The two heuristic OVERRIDES only steer the generic overdrive-vs-
        // distortion split, so they are meaningless on anything else.
        var keys = Overrides.ForcedDistortionKeys.Concat(Overrides.ForcedOverdriveKeys);
        foreach (var key in keys)
        {
            Assert.True(Catalog.TryGetGear(key, out var gear),
                $"drive-routing key '{key}' is not in the gear catalogue");
            Assert.Equal("Distortion", gear!.Category);
        }
    }

    [Fact]
    public void TargetModule_EveryKeyIsRealCatalogueGear()
    {
        foreach (var key in Overrides.TargetModuleMap.Keys)
        {
            Assert.True(Catalog.TryGetGear(key, out _),
                $"targetModule key '{key}' is not in the gear catalogue");
        }
    }

    [Fact]
    public void DriveRouting_EachKeyClaimsExactlyOneRouting()
    {
        var all = Overrides.ForcedDistortionKeys
            .Concat(Overrides.ForcedOverdriveKeys)
            .Concat(Overrides.TargetModuleMap.Keys)
            .ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// What a gear key may be routed TO, keyed by the gear's own catalogue
    /// category. Two things fail here: a module id that is not in the live
    /// mirror at all (a typo, or a module the catalogue lost), and a routing
    /// that crosses categories — a reverb rack pointed at a fuzz. The allow-list
    /// is per-category rather than one flat set precisely so the second kind
    /// cannot hide behind the first.
    /// <para>The <c>Amp</c> row is the odd one: an RS "amp" slot may legally
    /// resolve to a bass head, a valve DI preamp, or — for a SansAmp-style
    /// bass driver, which is a pedal RS files as an amp — the bass overdrive.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> LegalTargetsByCategory =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Distortion"] =
            [
                DcRigTargetCatalog.Fuzz, DcRigTargetCatalog.BigMuff, DcRigTargetCatalog.Klon,
                DcRigTargetCatalog.Rat, DcRigTargetCatalog.Ds1, DcRigTargetCatalog.Hm2,
                DcRigTargetCatalog.BassOd
            ],
            ["Amp"] = [DcRigTargetCatalog.BassAmp, DcRigTargetCatalog.TubePre, DcRigTargetCatalog.BassOd],
            ["Dynamics"] =
            [
                DcRigTargetCatalog.Limiter, DcRigTargetCatalog.Multiband, DcRigTargetCatalog.OptoComp,
                DcRigTargetCatalog.Sustainer,
                // Pedal_Compression: the game's bank is the Wwise Compressor.
                DcRigTargetCatalog.Compressor
            ],
            ["Filter"] =
            [
                DcRigTargetCatalog.Svf, DcRigTargetCatalog.AcousticSim, DcRigTargetCatalog.Boost,
                DcRigTargetCatalog.BitCrusher,
                // Pedal_BassEmulator: RS files it under Filter; its bank is the Wwise Harmonizer.
                DcRigTargetCatalog.Harmonizer
            ],
            ["Delay"] = [DcRigTargetCatalog.BbdDelay, DcRigTargetCatalog.Multitap],
            ["Reverb"] = [DcRigTargetCatalog.HallReverb, DcRigTargetCatalog.ConvReverb],
            ["Pitch"] = [DcRigTargetCatalog.OctFuzz, DcRigTargetCatalog.Detune],
            ["Modulation"] = [DcRigTargetCatalog.RingMod, DcRigTargetCatalog.TzFlanger]
        };

    [Fact]
    public void TargetModule_RoutesEveryKeyToALiveModuleLegalForItsCategory()
    {
        foreach (var (key, module) in Overrides.TargetModuleMap)
        {
            Assert.True(DcRigTargetCatalog.ModuleTypeIds.Contains(module),
                $"targetModule['{key}'] = '{module}' is not a module in the catalogue mirror");

            Assert.True(Catalog.TryGetGear(key, out var gear));
            var category = gear!.Category ?? "";
            Assert.True(LegalTargetsByCategory.TryGetValue(category, out var legal),
                $"targetModule['{key}']: gear category '{category}' has no routing allow-list");
            Assert.True(legal!.Contains(module, StringComparer.Ordinal),
                $"targetModule['{key}'] = '{module}' is not legal for a '{category}' gear " +
                $"(allowed: {string.Join(", ", legal)})");
        }
    }

    [Fact]
    public void StaticParams_EveryKeyIsARealCatalogueGear()
    {
        foreach (var key in Overrides.StaticParamGearKeys)
        {
            Assert.True(Catalog.TryGetGear(key, out _),
                $"staticParams key '{key}' is not in the gear catalogue");
        }
    }

    // --------------------------------------------------------------- coverage

    /// <summary>
    /// Every catalogue amp converts through the GAME'S OWN chain (#88): each one
    /// has a row in the <c>RsAmpChains</c> table its install yields, so none reaches users
    /// through the substring guess in <c>GuessVoicing</c>, and no override routes
    /// an amp anywhere else (that row would be dead data that reads as live).
    /// </summary>
    [Fact]
    public void EveryCatalogueAmp_RunsItsOwnGameChain_AndNoOverrideNamesAnAmp()
    {
        var routed = Overrides.TargetModuleMap.Keys
            .Where(k => Catalog.TryGetGear(k, out var g) && g.Type == "Amps")
            .ToArray();
        Assert.True(routed.Length == 0, $"targetModule rows for amps, which never reach it: {string.Join(", ", routed)}");

        // The chains are the player's install's (#755): owner-only from here.
        using var tables = TestRsGameTables.UseReal();
        var missing = GearOfType("Amps")
            .Where(g => !RsAmpChains.TryGetModel(g.Key, out _, out _))
            .Select(g => g.Key)
            .ToArray();
        Assert.True(missing.Length == 0, $"amps without a game chain: {string.Join(", ", missing)}");
    }

    [Fact]
    public void CabModel_CoversEveryCatalogueCab()
    {
        var missing = GearOfType("Cabinets")
            .Where(g => !Overrides.TryGetCabModel(g.Key, out _))
            .Select(g => g.Key)
            .ToArray();
        Assert.True(missing.Length == 0,
            $"cabinets without a model override: {string.Join(", ", missing)}");
    }
}
