using System.Globalization;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Prior;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Prior;

/// <summary>
///     The matched rig, back into words. The assertions are on the PROMPT (a
///     narrator that does not name the modules is describing a rig it was not
///     shown) and on the REFUSAL PATH (a narration whose text is "I can't help with
///     that" is worse than no narration at all).
/// </summary>
public sealed class ToneNarratorTests
{
    private static ToneSection Section()
        => new("s1", "solo", ToneStems.Guitar, TimeSpan.FromSeconds(125), TimeSpan.FromSeconds(158));

    private static ToneDesignModelReply Reply(string json)
        => new(json, new AiUsage("claude-fake-1", 11, 22));

    private static (ToneNarrator Narrator, FakeToneDesignModel Model, FakeToneDesignModelFactory Factory)
        Build(string json, AiProviderSettings? settings = null)
    {
        var model = new FakeToneDesignModel(_ => Reply(json));
        var factory = new FakeToneDesignModelFactory(model);
        var narrator = new ToneNarrator(
            () => PriorFakes.Config(settings ?? PriorFakes.Claude),
            factory,
            culture: () => CultureInfo.GetCultureInfo("de-DE"));
        return (narrator, model, factory);
    }

    [Fact]
    public async Task ThePromptNamesEveryModuleAndTheSection()
    {
        var (narrator, model, _) = Build("""{"name":"Glass Ceiling","rationale":"clean and wide"}""");
        var rig = PriorFakes.Rig("compressor", "amp", "cab", "reverb");

        await narrator.NarrateAsync(rig, Section(), "Song", "Artist", CancellationToken.None);

        var text = model.LastPrompt!.Messages[0].Text;
        foreach (var id in new[] { "compressor", "amp", "cab", "reverb" })
            Assert.Contains(id, text, StringComparison.Ordinal);
        Assert.Contains("solo", text, StringComparison.Ordinal);
        Assert.Contains("2:05", text, StringComparison.Ordinal);
        Assert.Contains("2:38", text, StringComparison.Ordinal);
        Assert.Contains("Song", text, StringComparison.Ordinal);
        Assert.Contains("Artist", text, StringComparison.Ordinal);
    }

    /// <summary>The knob values go in REAL UNITS, from the one describer both the
    /// prior text and this prompt use — a model told "gainDb 6" and a panel showing
    /// "+6 dB" must be reading the same rendering.</summary>
    [Fact]
    public async Task ThePromptCarriesKnobValuesInRealUnits()
    {
        var (narrator, model, _) = Build("""{"name":"Loud","rationale":"loud"}""");
        var rig = new DcRigPreset(
        [
            new DcRigModule("gain", new Dictionary<string, float>(StringComparer.Ordinal) { ["gainDb"] = 6f })
        ]);

        await narrator.NarrateAsync(rig, Section(), null, null, CancellationToken.None);

        Assert.Contains("gainDb 6 dB", model.LastPrompt!.Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAppsLanguageIsInTheSystemPrompt()
    {
        var (narrator, model, _) = Build("""{"name":"Name","rationale":"why"}""");

        await narrator.NarrateAsync(PriorFakes.Rig("amp"), Section(), null, null, CancellationToken.None);

        Assert.Contains("Answer in German", model.LastPrompt!.System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWellFormedReply_Parses_AndCarriesTheUsage()
    {
        var (narrator, _, factory) = Build(
            """{"name":"Velvet Crunch","rationale":"A mid-gain crunch; the amp is doing the work."}""");

        var narration = await narrator.NarrateAsync(
            PriorFakes.Rig("overdrive", "amp"), Section(), null, null, CancellationToken.None);

        Assert.NotNull(narration);
        Assert.Equal("Velvet Crunch", narration!.Name);
        Assert.Equal("A mid-gain crunch; the amp is doing the work.", narration.Rationale);
        Assert.Equal(11, narration.Usage.InputTokens);
        Assert.Equal(22, narration.Usage.OutputTokens);
        Assert.Equal(1, factory.Created);
    }

    /// <summary>A provider's model is minted per call and the caller owns it — the
    /// OpenAI-compatible one holds an HttpClient, and a narrator that leaked one per
    /// "Explain" click is a socket leak nobody would look for here.</summary>
    [Fact]
    public async Task TheModelIsDisposedAfterTheCall()
    {
        var (narrator, model, _) = Build("""{"name":"N","rationale":"r"}""");

        await narrator.NarrateAsync(PriorFakes.Rig("amp"), Section(), null, null, CancellationToken.None);

        Assert.True(model.Disposed);
    }

    /// <summary>A refusal is not a narration. The reply parses as text but carries
    /// no object — the answer must be null, never a "tone" whose name is an
    /// apology.</summary>
    [Theory]
    [InlineData("I can't help with that.")]
    [InlineData("")]
    [InlineData("{\"rationale\":\"no name here\"}")]
    [InlineData("{\"name\":\"   \",\"rationale\":\"blank name\"}")]
    [InlineData("[\"not\",\"an\",\"object\"]")]
    public async Task ARefusalOrAMalformedReply_IsNull(string json)
    {
        var (narrator, _, _) = Build(json);

        Assert.Null(await narrator.NarrateAsync(
            PriorFakes.Rig("amp"), Section(), null, null, CancellationToken.None));
    }

    [Fact]
    public async Task NoProvider_IsUnavailableAndNull()
    {
        var narrator = new ToneNarrator(
            () => PriorFakes.Config(AiProviderSettings.Disabled),
            new FakeToneDesignModelFactory(null));

        Assert.False(narrator.IsAvailable);
        Assert.Null(await narrator.NarrateAsync(
            PriorFakes.Rig("amp"), Section(), null, null, CancellationToken.None));
    }

    /// <summary>Settings that look complete for a provider this build cannot mint
    /// (the reserved BuiltIn slot) must read as unavailable — otherwise the surface
    /// offers a button whose every press is a NotConfigured error.</summary>
    [Fact]
    public void AProviderThisBuildCannotMint_IsUnavailable()
    {
        var narrator = new ToneNarrator(
            () => PriorFakes.Config(new AiProviderSettings(AiProviderKind.BuiltIn, "local-1", null)),
            new FakeToneDesignModelFactory(new FakeToneDesignModel(_ => Reply("{}"))));

        Assert.False(narrator.IsAvailable);
    }

    /// <summary>A model that ignores the word budget is CUT, at a word boundary. A
    /// twenty-word "name" is a layout bug in whatever shows it.</summary>
    [Fact]
    public async Task AnOverlongNameIsClippedAtAWordBoundary()
    {
        var words = string.Join(' ', Enumerable.Range(1, 30).Select(i => "w" + i));
        var (narrator, _, _) = Build($$"""{"name":"{{words}}","rationale":"r"}""");

        var narration = await narrator.NarrateAsync(
            PriorFakes.Rig("amp"), Section(), null, null, CancellationToken.None);

        Assert.NotNull(narration);
        Assert.Equal(ToneNarrator.MaxNameWords, narration!.Name.TrimEnd('…').Split(' ').Length);
        Assert.EndsWith("…", narration.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (narrator, _, _) = Build("""{"name":"N","rationale":"r"}""");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => narrator.NarrateAsync(PriorFakes.Rig("amp"), Section(), null, null, cts.Token));
    }
}
