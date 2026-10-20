using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     <see cref="AiModelPricing" /> — the small list-price table behind the
///     panel's "12.4 s · ~$0.04".
///
///     <para>
///         The rows are pinned BY VALUE. They are list prices copied from a price
///         page, nothing in the app can verify them, and a silently wrong one puts
///         a wrong dollar figure on screen that reads as authoritative. Pinning
///         them means changing one is a deliberate edit in two places.
///     </para>
/// </summary>
public sealed class AiModelPricingTests
{
    [Theory]
    [InlineData("claude-opus-5-5", 4.0, 20.0)]
    [InlineData("claude-sonnet-5-5", 2.0, 10.0)]
    [InlineData("claude-opus-5", 5.0, 25.0)]
    [InlineData("claude-sonnet-5", 2.0, 10.0)]
    [InlineData("claude-haiku-4-5", 1.0, 5.0)]
    [InlineData("claude-haiku-5-5", 0.1, 0.5)]
    [InlineData("gpt-6-astra", 10.0, 50.0)]
    [InlineData("gpt-6-sol", 2.0, 10.0)]
    [InlineData("gpt-6-luna", 0.1, 0.5)]
    public void TheTable_PinsEachFamilysListPrice(string modelId, double input, double output)
    {
        var row = AiModelPricing.Find(modelId);

        Assert.NotNull(row);
        Assert.Equal(input, row.InputUsdPerMTok);
        Assert.Equal(output, row.OutputUsdPerMTok);
    }

    /// <summary>A dated or aliased id is the SAME model and must find the same row
    /// — a table keyed by the exact string would fall through to "unpriced", i.e.
    /// free, for two of the three ways the id is written.</summary>
    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("claude-opus-5-latest")]
    [InlineData("claude-opus-5-20260401")]
    [InlineData("anthropic/claude-opus-5")]
    public void AVariantOfAKnownId_StillFindsItsRow(string modelId)
        => Assert.Equal("opus-5", AiModelPricing.Find(modelId)?.Family);

    /// <summary>
    ///     <c>claude-opus-5-5</c> CONTAINS <c>opus-5</c>, so the first-match scan
    ///     prices Opus 5.5 at Opus 5's $5/$25 if the shorter row ever comes first.
    ///     Each id must reach its OWN family, and the old one must keep its own.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5-5", "opus-5-5")]
    [InlineData("claude-opus-5-5-latest", "opus-5-5")]
    [InlineData("anthropic/claude-opus-5-5", "opus-5-5")]
    [InlineData("claude-sonnet-5-5", "sonnet-5-5")]
    [InlineData("claude-opus-5", "opus-5")]
    [InlineData("claude-opus-5-20260401", "opus-5")]
    [InlineData("claude-sonnet-5", "sonnet-5")]
    [InlineData("gpt-6-sol", "gpt-6-sol")]
    [InlineData("openai/gpt-6-luna", "gpt-6-luna")]
    public void ANewerIdContainingAnOlderFamily_FindsItsOwnRow(string modelId, string family)
        => Assert.Equal(family, AiModelPricing.Find(modelId)?.Family);

    /// <summary>The ordering rule itself, for rows nobody wrote an id test for: no
    /// row may come after a row whose family token it contains.</summary>
    [Fact]
    public void NoRow_IsShadowedByAnEarlierShorterRow()
    {
        var table = AiModelPricing.Table;
        for (var later = 0; later < table.Count; later++)
        {
            for (var earlier = 0; earlier < later; earlier++)
            {
                Assert.False(table[later].Family.Contains(table[earlier].Family, StringComparison.Ordinal),
                $"'{table[later].Family}' is shadowed by the earlier row '{table[earlier].Family}'.");
            }
        }
    }

    /// <summary>The other half of the feature is a LOCAL server, where a request
    /// costs nothing. An unknown id must answer "no price", never a guess.</summary>
    [Theory]
    [InlineData("llama-3.1-8b-instruct")]
    [InlineData("qwen2.5-coder")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownModel_HasNoPrice(string? modelId)
    {
        Assert.Null(AiModelPricing.Find(modelId));
        Assert.False(AiModelPricing.TryEstimateUsd(new AiUsage(modelId ?? "", 10_000, 1_000), out _));
        Assert.Null(AiModelPricing.FormatUsd(new AiUsage(modelId ?? "", 10_000, 1_000)));
    }

    [Fact]
    public void TheEstimate_IsInputPlusOutputAtListPrice()
    {
        // 7,500 in + 900 out on Opus 5: 7.5e-3 * 5 + 0.9e-3 * 25 = 0.0375 + 0.0225.
        Assert.True(AiModelPricing.TryEstimateUsd(new AiUsage("claude-opus-5", 7_500, 900), out var usd));

        Assert.Equal(0.06, usd, 6);
        Assert.Equal("0.06", AiModelPricing.FormatUsd(new AiUsage("claude-opus-5", 7_500, 900)));
    }

    /// <summary>A third of a cent is a real, reassuring number; rounded to two
    /// decimals it reads as "$0.00", which reads as a bug.</summary>
    [Fact]
    public void ACostBelowACent_KeepsFourDecimals()
        => Assert.Equal("0.0035", AiModelPricing.FormatUsd(new AiUsage("claude-haiku-4-5", 2_000, 300)));
}
