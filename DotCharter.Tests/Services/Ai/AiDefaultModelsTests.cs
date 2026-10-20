using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     What each provider kind starts with, and what the model box shows when the
///     picker moves. Literals on purpose: a comparison against the constant the
///     code reads passes whatever the constant says.
/// </summary>
public sealed class AiDefaultModelsTests
{
    [Theory]
    [InlineData(AiProviderKind.ClaudeCli, "claude-haiku-5-5")]
    [InlineData(AiProviderKind.CodexCli, "gpt-6-luna")]
    [InlineData(AiProviderKind.Claude, "claude-opus-5-5")]
    [InlineData(AiProviderKind.OpenAiCompatible, null)]
    [InlineData(AiProviderKind.None, null)]
    public void EachKind_StartsWithItsModel(AiProviderKind kind, string? expected) =>
        Assert.Equal(expected, AiDefaultModels.For(kind));

    [Theory]
    // An empty box takes the picked kind's default.
    [InlineData(AiProviderKind.None, AiProviderKind.ClaudeCli, "", "claude-haiku-5-5")]
    [InlineData(AiProviderKind.None, AiProviderKind.CodexCli, "  ", "gpt-6-luna")]
    [InlineData(AiProviderKind.None, AiProviderKind.OpenAiCompatible, "", "")]
    // The previous kind's untouched default does not follow the user across.
    [InlineData(AiProviderKind.ClaudeCli, AiProviderKind.CodexCli, "claude-haiku-5-5", "gpt-6-luna")]
    [InlineData(AiProviderKind.CodexCli, AiProviderKind.ClaudeCli, "gpt-6-luna", "claude-haiku-5-5")]
    [InlineData(AiProviderKind.ClaudeCli, AiProviderKind.OpenAiCompatible, "claude-haiku-5-5", "")]
    // A typed or saved id is the user's, whatever the move.
    [InlineData(AiProviderKind.ClaudeCli, AiProviderKind.CodexCli, "claude-opus-5-5", "claude-opus-5-5")]
    [InlineData(AiProviderKind.ClaudeCli, AiProviderKind.ClaudeCli, "claude-opus-5-5", "claude-opus-5-5")]
    [InlineData(AiProviderKind.Claude, AiProviderKind.ClaudeCli, "claude-sonnet-5", "claude-sonnet-5")]
    // Re-picking the same kind never rewrites, even its own default.
    [InlineData(AiProviderKind.CodexCli, AiProviderKind.CodexCli, "gpt-6-luna", "gpt-6-luna")]
    public void Seed_FillsAnEmptyBox_AndReplacesOnlyTheLastKindsDefault(
        AiProviderKind previous, AiProviderKind picked, string current, string expected) =>
        Assert.Equal(expected, AiDefaultModels.Seed(previous, picked, current));
}
