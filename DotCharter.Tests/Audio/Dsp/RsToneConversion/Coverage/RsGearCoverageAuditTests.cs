using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Coverage;

/// <summary>
///     Keeps <c>docs/agents/services/rs-tone-coverage.md</c> equal to what the
///     converter does today, and pins the audit's own probe so a green doc cannot
///     come from a probe that sees nothing.
/// </summary>
public sealed class RsGearCoverageAuditTests
{
    /// <summary>Set to <c>1</c> to rewrite the committed guide from the audit.</summary>
    public const string RegenVariable = "DOTCHARTER_REGEN_RS_COVERAGE";

    /// <summary>The audit WITHOUT the game tables (#755): what every checkout converts,
    /// and what the committed guide records.</summary>
    private static readonly Lazy<IReadOnlyList<GearCoverage>> Rows = new(() =>
    {
        using var none = SyntheticRsGame.UseNone();
        return RsGearCoverageAudit.Run(RsGearCatalog.Shared, RsGearOverrides.Shared, RsGearLibraryUsage.Snapshot());
    });

    /// <summary>The audit WITH the player's install, or null without one (owner-only).</summary>
    private static readonly Lazy<IReadOnlyList<GearCoverage>?> RealRows = new(() =>
    {
        if (TestRsGameTables.TryReal() is not { } tables) return null;
        using var real = RsGameTables.Scope(tables);
        return RsGearCoverageAudit.Run(RsGearCatalog.Shared, RsGearOverrides.Shared, RsGearLibraryUsage.Snapshot());
    });

    [Fact]
    public void CommittedGuide_IsWhatTheConverterDoesToday()
    {
        var expected = RsGearCoverageAudit.Render(Rows.Value, RsGearLibraryUsage.Snapshot());
        var path = Path.Combine(TestPaths.RepoRoot(), RsGearCoverageAudit.DocRelativePath);

        if (Environment.GetEnvironmentVariable(RegenVariable) == "1")
            File.WriteAllText(path, expected);

        Assert.True(File.Exists(path), $"missing {RsGearCoverageAudit.DocRelativePath}");
        var actual = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(actual == expected,
            $"{RsGearCoverageAudit.DocRelativePath} is stale: the converter or the gear data changed. " +
            $"Regenerate it with {RegenVariable}=1 and review the diff — it IS the coverage change.");
    }

    [Fact]
    public void Audit_HasOneRowPerCatalogueKey()
    {
        var catalog = RsGearCatalog.Shared;
        var expected = catalog.Count(RsGearType.Amp) + catalog.Count(RsGearType.Cabinet) +
                       catalog.Count(RsGearType.Pedal) + catalog.Count(RsGearType.Rack);
        Assert.Equal(613, expected); // 63 + 450 + 83 + 17, the snapshot's own counts
        Assert.Equal(expected, Rows.Value.Count);
    }

    [Fact]
    public void KnobProbe_SeesAKnobThatLands()
    {
        // Non-vacuity, positive half: the JCM800's Gain lands on the generic amp's
        // gain without the game tables (#755), so the probe must report the param it
        // moves. (With them it is rsamp.k1: EveryAmp_IsItsOwnGameChain…, owner-only.)
        var row = Row("Amp_MarshallJCM800");
        var gain = row.Knobs.Single(k => k.Knob.Pot == "Gain");
        Assert.Contains("amp.gain", gain.Params);
    }

    [Fact]
    public void KnobProbe_SeesAKnobThatFolds()
    {
        // Negative half: the Shred Zone's three EQ knobs have no `distortion`
        // param, so the probe must call them folded and the row cannot be ✅.
        // (This was the HG-500's Mid-Frequency until #88 ran every amp through
        // its own game chain, and the EN-30's Bright before #70.)
        var row = Row("Pedal_ShredZone");
        var bass = row.Knobs.Single(k => k.Knob.Pot == "Bass");
        Assert.False(bass.Lands);
        Assert.NotEqual(CoverageStatus.Modelled, row.Status);
        Assert.Contains(row.Reasons, r => r.Contains("'Bass' folds", StringComparison.Ordinal));
    }

    [Fact]
    public void ConverterDrops_AreReportedMissing()
    {
        // A gear the converter cannot place at all must read ❌, not 🟡.
        var missing = Rows.Value.Where(r => r.Modules.Count == 0).ToList();
        Assert.All(missing, r => Assert.Equal(CoverageStatus.Missing, r.Status));
        Assert.All(Rows.Value.Where(r => r.Modules.Count > 0),
            r => Assert.NotEqual(CoverageStatus.Missing, r.Status));
    }

    [Fact]
    public void Judgements_NameRealGearAndRealModels()
    {
        var catalog = RsGearCatalog.Shared;
        var cabKeys = catalog.AllGear.Where(g => g.Type == "Cabinets").Select(g => g.Key).ToList();
        foreach (var (stem, (model, _)) in RsGearCoverageJudgements.SameSpeakerCabStems)
        {
            Assert.True(cabKeys.Any(k => RsGearCoverageAudit.CabStem(k) == stem), $"'{stem}' is no cabinet family");
            Assert.InRange(model, 0, 25);
        }
    }

    [Fact]
    public void AJudgementOnlyVouchesForTheModelItNames()
    {
        // The TW112C family is judged same-layout ON the 1x12 Combo. Without the
        // override that picks it, the same key must fall back to 🟡.
        const string key = "Cab_TW112C_57_Cone";
        var row = Row(key);
        Assert.Equal(RsGearCoverageJudgements.SameSpeakerCabStems["Cab_TW112C"].Model, (int)row.Modules[0].Params["model"]);
        Assert.Equal(CoverageStatus.Modelled, row.Status);

        var rerouted = RsGearCoverageAudit.Run(RsGearCatalog.Shared, RsGearOverrides.Empty, RsGearLibraryUsage.Empty)
            .Single(r => r.Gear.Key == key);
        Assert.Equal(CoverageStatus.Approximated, rerouted.Status);
    }

    [Fact]
    public void EveryAmp_IsItsOwnGameChain_AndEveryKnobCarriesAVerdict()
    {
        // #88: no amp knob folds, and each is ✅ or 🟡 with a reason in its Notes.
        // The chains are the player's install's (#755): owner-only.
        var rows = RealRows.Value;
        Assert.SkipWhen(rows is null, "needs the Rocksmith game tables: " + TestRsGameTables.Source);
        var amps = rows!.Where(r => r.Gear.Type == "Amps").ToList();
        Assert.Equal(63, amps.Count);
        Assert.All(amps, r => Assert.Equal("rsamp", r.Modules[0].ModuleTypeId));
        Assert.All(amps, r => Assert.All(r.Knobs, k => Assert.True(k.Lands, $"{r.Gear.Key}.{k.Knob.Name} folds")));
        var verdicts = amps.SelectMany(r => r.Knobs.Select(k => RsGearCoverageAudit.KnobVerdict(r, k))).ToList();
        Assert.All(verdicts, v => Assert.True(v is " ✅" or " 🟡"));
        // Both halves occur: a probe that rated everything one way would be vacuous.
        Assert.Contains(" ✅", verdicts);
        Assert.Contains(" 🟡", verdicts);

        // The GBC-1H's Bass is EQ-only (✅); its Gain drives the drive stage (🟡).
        var gb100 = rows.Single(r => r.Gear.Key == "Amp_GB100");
        Assert.Equal(" ✅", RsGearCoverageAudit.KnobVerdict(gb100, gb100.Knobs.Single(k => k.Knob.Pot == "Bass")));
        Assert.Equal(" 🟡", RsGearCoverageAudit.KnobVerdict(gb100, gb100.Knobs.Single(k => k.Knob.Pot == "Gain")));
    }

    [Fact]
    public void ValueLabelMirror_MatchesTheNativeCatalogue_WhenTheDllLoads()
    {
        IReadOnlyList<DspModuleType> native;
        try
        {
            var svc = new JuceDspChainService();
            svc.LoadCatalog();
            native = svc.Catalog;
        }
        catch
        {
            native = [];
        }

        Assert.SkipWhen(native.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        foreach (var ((module, param), labels) in RsGearCoverageJudgements.Labels)
        {
            var info = native.Single(m => m.Id == module).Params.Single(p => p.Id == param);
            Assert.Equal(labels, info.ValueLabels);
        }
    }

    private static GearCoverage Row(string key) => Rows.Value.Single(r => r.Gear.Key == key);
}
