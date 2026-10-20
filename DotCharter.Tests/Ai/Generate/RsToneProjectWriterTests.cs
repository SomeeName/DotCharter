using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     Where a generated set LANDS: the project's own tone definitions, the placed
///     tone changes pointed at them, and the arrangement's Rocksmith slots — all
///     through the writers the app already has.
/// </summary>
public sealed class RsToneProjectWriterTests
{
    private static (ChartProject Project, Arrangement Arrangement, IReadOnlyList<ToneWindow> Windows) Fixture(
        params double[] changeMs)
    {
        var beats = GenerateFakes.Beats(bars: 16);
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        foreach (var ms in changeMs) arrangement.ToneChanges.Add(GenerateFakes.At(ms, beats));
        project.Arrangements.Add(arrangement);

        return (project, arrangement, ToneWindowPlanner.Plan(arrangement.ToneChanges, beats, 32_000));
    }

    private static GeneratedTone Tone(ToneWindow window, string key)
    {
        var (amp, cab) = GenerateFakes.RealGuitarGear();
        var tone = new RsTone(
            key, key, RsToneAuthoring.DefaultVolumeDb, [RsToneAuthoring.CleanDescriptorUIName],
            new RsAmp(amp, RsGearCatalog.Shared.GetCategory(amp), new Dictionary<string, float>(StringComparer.Ordinal)),
            new RsCab(cab, RsGearCatalog.Shared.GetCategory(cab), new Dictionary<string, float>(StringComparer.Ordinal)),
            [], [], []);

        return new GeneratedTone(window, tone, new ToneGenerationProvenance(
            window, ToneStems.Guitar, [ToneGenerationSources.Audio], "fake-model", "", null, "no-renderer", null),
            null);
    }

    [Fact]
    public void ItWritesOneToneDefinitionPerTone_AndPointsTheChangesAtThem()
    {
        var (project, arrangement, windows) = Fixture(4000, 12_000);

        var result = RsToneProjectWriter.Write(
            project, arrangement, [Tone(windows[0], "verse"), Tone(windows[1], "chorus")]);

        Assert.Equal(["verse", "chorus"], result.Keys);
        Assert.Equal(2, result.ToneChangesPointed);
        Assert.Equal(2, project.ToneDefinitions.Count);
        Assert.Equal(["verse", "chorus"], arrangement.ToneChanges.Select(c => c.ToneName).ToArray());

        // The body is the manifest JSON the package export embeds verbatim — written
        // by the app's one RsTone serializer, not a second one here.
        foreach (var definition in project.ToneDefinitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Json));
            Assert.Contains("\"GearList\"", definition.Json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ItFillsTheArrangementsRocksmithToneSlots()
    {
        var (project, arrangement, windows) = Fixture(4000, 12_000, 20_000);

        RsToneProjectWriter.Write(project, arrangement,
            [Tone(windows[0], "a"), Tone(windows[1], "b"), Tone(windows[2], "c")]);

        Assert.Equal("a", arrangement.ToneBase);
        Assert.Equal("b", arrangement.ToneA);
        Assert.Equal("c", arrangement.ToneB);
        Assert.Equal("", arrangement.ToneC);
        Assert.Equal(["a", "b", "c"], arrangement.ToneNames);
    }

    [Fact]
    public void RewritingReplacesTheDefinition_RatherThanAddingASecondOne()
    {
        var (project, arrangement, windows) = Fixture(4000);
        RsToneProjectWriter.Write(project, arrangement, [Tone(windows[0], "verse")]);
        RsToneProjectWriter.Write(project, arrangement, [Tone(windows[0], "verse")]);

        Assert.Single(project.ToneDefinitions);
    }

    [Fact]
    public void AnInvalidTone_IsREFUSED_AndNothingIsWritten()
    {
        // All-or-nothing: a half-written arrangement whose second tone was rejected
        // is worse than one that was not touched.
        var (project, arrangement, windows) = Fixture(4000, 12_000);
        var good = Tone(windows[0], "verse");
        var bad = good with
        {
            Window = windows[1],
            Tone = good.Tone! with
            {
                Key = "chorus",
                Amp = new RsAmp("Amp_Nope", "Amp", new Dictionary<string, float>(StringComparer.Ordinal))
            }
        };

        var ex = Assert.Throws<RsToneCatalogException>(
            () => RsToneProjectWriter.Write(project, arrangement, [good, bad]));

        Assert.Contains("Amp_Nope", ex.Message, StringComparison.Ordinal);
        Assert.Empty(project.ToneDefinitions);
        Assert.Equal("", arrangement.ToneBase);
        Assert.All(arrangement.ToneChanges, c => Assert.Equal("", c.ToneName));
    }

    [Fact]
    public void AFailedWindowIsSkipped_AndTheRestStillLand()
    {
        var (project, arrangement, windows) = Fixture(4000, 12_000);
        var failed = new GeneratedTone(windows[0], null, null, "no bass stem");

        var result = RsToneProjectWriter.Write(project, arrangement, [failed, Tone(windows[1], "chorus")]);

        Assert.Equal(["chorus"], result.Keys);
        Assert.Equal("", arrangement.ToneChanges[0].ToneName);
        Assert.Equal("chorus", arrangement.ToneChanges[1].ToneName);
    }

    [Fact]
    public void NothingGenerated_ChangesNothing()
    {
        var (project, arrangement, windows) = Fixture(4000);

        var result = RsToneProjectWriter.Write(
            project, arrangement, [new GeneratedTone(windows[0], null, null, "failed")]);

        Assert.Empty(result.Keys);
        Assert.Null(result.BaseTone);
        Assert.Empty(project.ToneDefinitions);
        Assert.Equal("", arrangement.ToneBase);
    }

    [Fact]
    public void TheWrittenBodyParsesBackToTheSameGear()
    {
        // The round trip the package export depends on: what is stored must read back
        // as the tone that was generated.
        var (project, arrangement, windows) = Fixture(4000);
        var generated = Tone(windows[0], "verse");
        RsToneProjectWriter.Write(project, arrangement, [generated]);

        Assert.True(RsToneJsonParser.TryParse(project.ToneDefinitions[0].Json, out var parsed));

        Assert.NotNull(parsed);
        Assert.Equal(generated.Tone!.Amp.GearKey, parsed!.Amp.GearKey);
        Assert.Equal(generated.Tone.Cabinet.GearKey, parsed.Cabinet.GearKey);
        Assert.Equal("verse", parsed.Key);
        Assert.Null(RsToneCatalogValidation.FirstFault(parsed, RsGearCatalog.Shared));
    }
}
