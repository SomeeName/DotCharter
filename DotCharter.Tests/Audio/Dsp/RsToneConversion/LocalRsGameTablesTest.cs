using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     OWNER-ONLY (#755): the game tables built from a real Rocksmith 2014 install are
///     the tables the rig was tuned against. Until #755 they were committed — amp
///     chains generated into <c>RsAmpChains.g.cs</c>, knob curves transcribed into
///     <c>RsGameCurves</c> / <c>RsGameChorusCurves</c>; now the app reads them from the
///     player's install, so this compares that build, group by group, to the SHA-256
///     of the removed tables' canonical text (<see cref="RsGameTablesCanonical" />).
///     Hashes only: no game number is in this file. SKIPS without an install and the
///     real key file, or <see cref="TestRsGameTables.GearDirsVariable" />.
/// </summary>
public sealed class LocalRsGameTablesTest
{
    /// <summary>SHA-256 of each group's canonical text, taken from the committed tables
    /// on the day they were removed.</summary>
    private static readonly IReadOnlyDictionary<string, string> RemovedTableSha256 = new Dictionary<string, string>
    {
        ["amps"] = "8d6bca090264ed5e27c341a39d9596ce9fb84bc22b1d6fb51c131ee2680e94b5",
        ["studioeq"] = "6a16e514a62283d9ac0dde6ab1d9afcc367b0348358bc4744bc80eee6b535051",
        ["compression"] = "f6a27617573a726246816052264fffab3987248404ed3bcc48aa690267480d17",
        ["lofi"] = "ef44fdc9d683862265b7389c7f20f2f0002d0871518ad3c1846344e6c85c042a",
        ["bassemulator"] = "3a3e6c1ed352a6f3533e51b9495603d8587236443ab26d40cc87f2b11149b286",
        ["bitcruncher"] = "177cb0d0ba8545b600c92d3560162306e751228beeddfca1ba1adafe4406b6ec",
        ["bassoverdrive"] = "b5a3065e781d048efbf8f292a8a5e3e0b9966cfbe180477f5a04910204c35651",
        ["acoustic"] = "bf0ead55591328f546553d544638b7c464a36d055d3442dd47450f2463872496",
        ["chorus"] = "d491004dbf2bd2538aa4f578a252a449a00b36358af76aa8d87d24307f590fbb",
    };

    /// <summary>SHA-256 of the canonical text (<see cref="NativeAmpTableText" />) of the amp
    /// chains JuceSharp compiled in until #25 (<c>dcdsp_rsamp_chains.h</c>, 63 rows), taken
    /// from that header the day it was deleted.</summary>
    private const string RemovedNativeChainsSha256 = "5d2f6b642fa859b76d6e10247442256befc4ba167a1bec459af6b07ad11e7207";

    /// <summary>Env var: a folder OUTSIDE the repo to write each group's canonical text to.</summary>
    private const string DumpVariable = "DOTCHARTER_RS_GAME_TABLES_DUMP";

    private readonly ITestOutputHelper _out;

    public LocalRsGameTablesTest(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheInstallBuildsTheTablesTheRigWasTunedAgainst()
    {
        var tables = TestRsGameTables.RequireReal();
        var texts = RsGameTablesCanonical.Texts(tables);
        var mismatches = new List<string>();
        var compared = 0;
        foreach (var group in RsGameTablesCanonical.Groups)
        {
            if (!texts.TryGetValue(group, out var text))
            {
                // An extract may hold only some gear; the install holds it all.
                if (!TestRsGameTables.FromExtract) mismatches.Add($"{group}: not built");
                continue;
            }

            compared++;
            var sha = RsGameTablesCanonical.Sha256(text);
            _out.WriteLine($"{group}: {sha}");
            if (sha != RemovedTableSha256[group]) mismatches.Add($"{group}: {sha}");
            // To diff a mismatch, the text of the player's own install goes to a folder
            // of their choosing (outside the repo), never to the test log.
            if (Environment.GetEnvironmentVariable(DumpVariable) is { Length: > 0 } dump)
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(dump).FullName, group + ".txt"), text);
        }

        _out.WriteLine(TestRsGameTables.Source);
        Assert.True(compared > 0, "no group was built: " + TestRsGameTables.Source);
        Assert.True(mismatches.Count == 0, "differs from the removed tables:\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    ///     JuceSharp #25: the native amp chains the install builds are, number for number,
    ///     the chains the engine compiled in until the header was deleted (blocks, baked
    ///     <c>init.bnk</c> defaults, knob ranges, curves) — compared through the BLOB the
    ///     engine receives, read back independently of its writer.
    /// </summary>
    [Fact]
    public void TheNativeAmpTable_IsTheChainsTheEngineCompiledIn()
    {
        var tables = TestRsGameTables.RequireReal();
        Assert.SkipWhen(TestRsGameTables.FromExtract, "a partial extract builds no full native table");
        Assert.NotNull(tables.NativeAmpTable);
        var text = NativeAmpTableText.Canonical(tables.NativeAmpTable!);
        var sha = NativeAmpTableText.Sha256(text);
        _out.WriteLine($"native amp table: {tables.NativeAmpTable!.Length} bytes, {text.Count(c => c == '\n')} lines, sha256 {sha}");
        if (Environment.GetEnvironmentVariable(DumpVariable) is { Length: > 0 } dump)
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(dump).FullName, "native-amps.txt"), text);
        Assert.Equal(tables.AmpModels.Select(m => m.Key), NativeAmpTableText.Keys(tables.NativeAmpTable!));
        Assert.Equal(RemovedNativeChainsSha256, sha);
    }

    /// <summary>...and the engine takes it: every row's key is the row the engine's
    /// public list names (JuceSharp <c>dcdsp_rsamp_models.h</c>).</summary>
    [Fact]
    public void TheEngine_AcceptsTheNativeAmpTable()
    {
        var tables = TestRsGameTables.RequireReal();
        Assert.SkipWhen(TestRsGameTables.NativeAmpTableAccepted is null, "native library not loadable here: " + TestRsGameTables.Source);
        Assert.True(TestRsGameTables.NativeAmpTableAccepted, "the engine refused the install's amp chains (see the engine log)");
        Assert.Equal(tables.AmpModels.Count, JuceRsAmpTable.LoadedRowCount);
    }

    [Fact]
    public void TheAmpTable_HasARow_ForEveryAmpTheCatalogueShips_WithItsKnobs()
    {
        var tables = TestRsGameTables.RequireReal();
        Assert.SkipWhen(tables.AmpModels.Count == 0, "no amp banks in this source");
        var catalogueAmps = RsGearCatalog.Shared.AllGear.Where(g => g.Type == "Amps").Select(g => g.Key).OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(catalogueAmps, tables.AmpModels.Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(tables.AmpModels.Count - 1, (int)Spec("model").Max);

        var drift = new List<string>();
        foreach (var model in tables.AmpModels)
        {
            Assert.True(RsGearCatalog.Shared.TryGetGear(model.Key, out var gear));
            var knobs = (gear!.Knobs ?? []).ToDictionary(k => k.Key, StringComparer.Ordinal);
            Assert.True(model.Knobs.Count <= RsToneConverter.RsAmpKnobParams.Length, model.Key);
            if (model.Knobs.Count != knobs.Count) drift.Add($"{model.Key}: {model.Knobs.Count} knobs, catalogue {knobs.Count}");
            foreach (var k in model.Knobs)
            {
                if (!knobs.TryGetValue(k.Key, out var c)) drift.Add($"{k.Key}: not in the catalogue");
                else if (c.MinValue != k.Min || c.MaxValue != k.Max) drift.Add($"{k.Key}: {k.Min}..{k.Max}, catalogue {c.MinValue}..{c.MaxValue}");
            }
        }

        Assert.True(drift.Count == 0, string.Join("\n", drift));
        Assert.Equal(RsAmpLevelTrims.ByKey.Keys.Order(StringComparer.Ordinal), tables.AmpModels.Select(m => m.Key));
    }

    [Fact]
    public void TheNativeModelLabels_AreTheTable_InOrder()
    {
        var tables = TestRsGameTables.RequireReal();
        Assert.SkipWhen(tables.AmpModels.Count == 0, "no amp banks in this source");
        var native = TryLoadNativeModelLabels();
        Assert.SkipWhen(native is null, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");
        Assert.Equal(tables.AmpModels.Select(m => m.Name), native!);
    }

    private static DcRigTargetCatalog.ParamSpec Spec(string id)
    {
        Assert.True(DcRigTargetCatalog.TryGetParam(DcRigTargetCatalog.RsAmpChain, id, out var spec));
        return spec;
    }

    private static IReadOnlyList<string>? TryLoadNativeModelLabels()
    {
        try
        {
            var svc = new JuceDspChainService();
            svc.LoadCatalog();
            return svc.Catalog.FirstOrDefault(m => m.Id == DcRigTargetCatalog.RsAmpChain)?
                .Params.FirstOrDefault(p => p.Id == "model")?.ValueLabels;
        }
        catch
        {
            return null;
        }
    }
}
