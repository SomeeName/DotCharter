using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai;

/// <summary>
///     The house-idiom section of the system prompt: that it is DERIVED from the
///     shipping converter rather than typed, that it covers what the converter
///     really produces, and that the chain-shape rules the first measured eval
///     found missing are actually in the prompt.
/// </summary>
public sealed class ToneIdiomPromptTests
{
    [Fact]
    public void TheIdiom_IsDerivedFromTheConverter_NotTyped()
    {
        // Every row must be reproducible by running the real converter over the
        // real gear catalogue. If this table were hand-written, a converter
        // change would leave it stale and this test could not exist at all.
        var converter = new RsToneConverter();
        var catalogue = RsGearCatalog.Shared;
        var checkedRows = 0;

        foreach (var row in RsConverterIdiom.Rows)
        {
            if (row.ModuleTypeId == "cab") continue; // the collapsed cabinet row

            foreach (var name in row.Examples)
            {
                var gear = catalogue.AllGear.FirstOrDefault(g =>
                    string.Equals(g.Name, name, StringComparison.Ordinal) &&
                    string.Equals(g.Category, row.RsCategory, StringComparison.Ordinal));
                Assert.NotNull(gear);

                // An amp converts to its own game chain (`rsamp`, #88) where the
                // install's tables are loaded, which the idiom names by its
                // designable stand-in for the amp's slot; it never depends on them.
                if (row.Slot is "amp" or "bass amp" or "DI preamp")
                {
                    Assert.Equal("Amps", gear!.Type);
                    Assert.Equal(row.Slot switch { "bass amp" => "bassamp", "DI preamp" => "tubepre", _ => "amp" }, row.ModuleTypeId);
                }
                else
                {
                    Assert.Contains(row.ModuleTypeId, ConvertOne(converter, gear!));
                }
                checkedRows++;
            }
        }

        Assert.True(checkedRows > 30,
            $"Only {checkedRows} idiom examples were re-derived; the block looks empty.");
    }

    [Fact]
    public void TheIdiom_NamesTheModulesTheFirstEvalFoundMissing()
    {
        // The 2026-09-12 baseline lost most of its module score by answering the
        // GENERIC module where the converter means a specific one. Every one of
        // these must appear in the block, or the model is never told.
        var block = RsConverterIdiom.Block;
        foreach (var id in new[]
                 {
                     "multiband", "graphiceq", "springreverb", "platereverb",
                     "hallreverb", "bbddelay", "acousticsim", "octfuzz", "bassamp",
                     "tubepre", "rat", "ds1", "hm2", "bigmuff", "limiter", "multitap"
                 })
        {
            Assert.Contains("\n" + id + " [", block, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheIdiom_SplitsTheStompboxFromTheRack_BecauseThatIsBothIdentityAndOrder()
    {
        // `Compression` is a PEDAL and `Studio Compressor` a RACK. Both convert to
        // `compressor` since 2026-10 (the game's bank for the pedal is the Wwise
        // Compressor, #69 — it rode `optocomp` before), and the slot still tells
        // them apart, which is also where in the chain the module goes:
        // RsTone.InSignalOrder puts pedals before the amp and racks after the cab.
        var block = RsConverterIdiom.Block;
        Assert.Contains("compressor [pedal] <- Compression", block, StringComparison.Ordinal);
        Assert.Contains("compressor [rack] <- Studio Compressor", block, StringComparison.Ordinal);
        Assert.Contains("multiband [bass pedal] <- MB Comp", block, StringComparison.Ordinal);
        Assert.Contains("tubepre [DI preamp] <- ", block, StringComparison.Ordinal);

        // Every row carries one, and only from the derived vocabulary — a row
        // tagged from prose would drift the first time the catalogue grew.
        foreach (var row in RsConverterIdiom.Rows)
        {
            Assert.Contains(row.Slot, new[]
            {
                "pedal", "rack", "bass pedal", "bass rack", "amp", "bass amp",
                "DI preamp", "cabinet"
            });
        }
    }

    [Fact]
    public void TheFamilySummary_IsTheREACHABLESetAndNothingElse()
    {
        // The point of this block is what it leaves OUT: a rig converted from a
        // Rocksmith tone contains no plain `reverb` at all, and the model
        // answered one seven times on the train split with only a general
        // "prefer the specific module" rule to stop it. The list is generated
        // from ReachableModules, so it cannot quietly grow an entry the
        // converter does not produce.
        var block = RsConverterIdiom.Block;
        var summary = block[block.IndexOf(
            "## Everything a converted rig can contain", StringComparison.Ordinal)..];

        Assert.Contains("reverb: hallreverb, platereverb, springreverb", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("reverb: hallreverb, platereverb, reverb", summary, StringComparison.Ordinal);

        // Bass gear is the other half: `MB Comp` is the only compressor
        // Rocksmith ships for bass at all, which is why every converted bass
        // rig's dynamics module is `multiband`.
        var bassLine = summary.Split('\n').Single(l => l.StartsWith("Gear Rocksmith ships for BASS", StringComparison.Ordinal));
        Assert.Contains("multiband", bassLine, StringComparison.Ordinal);
        Assert.Contains("bassamp", bassLine, StringComparison.Ordinal);
        Assert.DoesNotContain("optocomp", bassLine, StringComparison.Ordinal);

        // Every id in the summary is one the converter really emits.
        foreach (var line in summary.Split('\n'))
        {
            var colon = line.IndexOf(':');
            // Family lines only — a one-word key then the list. The prose above
            // carries a colon too, and it is not a list of module ids.
            if (colon < 0 || line.StartsWith("##", StringComparison.Ordinal)) continue;
            if (line[..colon].Contains(' ', StringComparison.Ordinal)) continue;
            foreach (var id in line[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Assert.Contains(id, RsConverterIdiom.ReachableModules);
        }
    }

    [Fact]
    public void TheIdiom_IsDeterministic_BecauseItIsACachedPromptPrefix()
    {
        Assert.Same(RsConverterIdiom.Block, RsConverterIdiom.Block);

        // …and the ORDER is fixed, not dictionary order: two derivations of the
        // same data must print the same bytes or the provider's prompt cache
        // misses on every process restart.
        var ordered = RsConverterIdiom.Rows
            .OrderBy(r => r.RsCategory, StringComparer.Ordinal)
            .ThenBy(r => r.ModuleTypeId, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(ordered.Select(r => r.ModuleTypeId), RsConverterIdiom.Rows.Select(r => r.ModuleTypeId));

        foreach (var row in RsConverterIdiom.Rows)
            Assert.Equal(row.Examples.Order(StringComparer.Ordinal), row.Examples);
    }

    [Fact]
    public void EveryIdiomModule_IsInTheCatalogueBlockTheModelMayAnswerWith()
    {
        // An idiom row naming a module the catalogue section does not list is a
        // module the model is told to use and then cannot spell the params of.
        var catalogueBlock = ToneDesignPromptBuilder.CatalogueBlock;
        foreach (var id in RsConverterIdiom.ReachableModules)
            Assert.Contains("\n" + id + " — ", "\n" + catalogueBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSystemPrompt_CarriesTheChainShapeRulesTheEvalFoundMissing()
    {
        var prompt = ToneDesignPromptBuilder.SystemPrompt;

        // Bass ended at the head on 10 of 35 rows; acoustic answered acpre on 5.
        Assert.Contains("bassamp -> cab", prompt, StringComparison.Ordinal);
        Assert.Contains("acousticsim in FRONT", prompt, StringComparison.Ordinal);
        Assert.Contains("# The house idiom", prompt, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ConvertOne(RsToneConverter converter, RsGearData gear)
    {
        var knobs = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var knob in gear.Knobs ?? []) knobs[knob.Key] = knob.DefaultValue;

        var cab = new RsCab(RsToneJsonParser.DefaultCabinetKey, "Cabinet",
            new Dictionary<string, float>(StringComparer.Ordinal));

        RsTone tone;
        if (gear.Type is "Amps" or "Amp")
        {
            tone = new RsTone("t", "t", 0f, [], new RsAmp(gear.Key, gear.Category, knobs), cab, [], [], []);
        }
        else
        {
            tone = new RsTone("t", "t", 0f, [],
                new RsAmp(RsToneJsonParser.SyntheticAmpKey, "Amp",
                    new Dictionary<string, float>(StringComparer.Ordinal)),
                cab,
                [new RsPedal(RsGearRole.PrePedal, 1, gear.Key,
                    gear.Type == "Racks" ? RsGearType.Rack : RsGearType.Pedal, gear.Category, knobs)],
                [], []);
        }

        return converter.Convert(tone, out _).Modules.Select(m => m.ModuleTypeId).ToList();
    }
}
