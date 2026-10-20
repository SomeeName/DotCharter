using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     The tone's NAME as a SOFT hint (#614, owner 2026-10-10): a descriptive name ("Clean", "Dist2",
///     "Clean_Chorus") reaches the prompt as "the charter named this tone …"; a generic one ("Tone B",
///     "Base", "Default", the song's or the arrangement's own name, empty) does not; and the hint says
///     the measurements and the chat's instruction win, which follows it in the prompt.
/// </summary>
public sealed class RsToneNameHintTests
{
    [Theory]
    [InlineData("Clean")]
    [InlineData("Distortion")]
    [InlineData("Lead")]
    [InlineData("Crunch")]
    [InlineData("Acoustic")]
    [InlineData("Clean_Chorus")]
    [InlineData("Dist2")]
    [InlineData("Sakasma_Clean")]
    [InlineData("HighGainLead")]
    public void A_descriptive_name_is_a_hint(string name) =>
        Assert.Equal(name, RsToneNameHint.Of(name, "Sakasma", "Rhythm"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Tone B")]
    [InlineData("Tone_A")]
    [InlineData("ToneD")]
    [InlineData("tone 3")]
    [InlineData("Base")]
    [InlineData("Tone_Base")]
    [InlineData("Default")]
    [InlineData("Sakasma")]
    [InlineData("Sakasma_Tone_2")]
    [InlineData("Rhythm")]
    [InlineData("Rhythm_Tone")]
    [InlineData("X")]
    public void A_generic_name_is_not(string? name) =>
        Assert.Null(RsToneNameHint.Of(name, "Sakasma", "Rhythm"));

    private static string Prompt(string toneName, string? instruction = null) =>
        RsToneGeneratePrompt.Build(new RsToneMapRequest(null, "band: X", "Key_1", toneName, ToneStems.Guitar, "Sakasma",
                "X", instruction, "Rhythm"))
            .Messages[0].Text.ReplaceLineEndings("\n");

    [Fact]
    public void A_descriptive_name_reaches_the_prompt_as_a_soft_hint()
    {
        var text = Prompt("Clean_Chorus");

        Assert.Contains(RsToneGeneratePrompt.NameHintLine("Clean_Chorus"), text, StringComparison.Ordinal);
        Assert.Contains("SOFT hint", RsToneGeneratePrompt.NameHintLine("Clean_Chorus"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_generic_name_does_not_reach_the_prompt_as_a_hint()
    {
        var text = Prompt("Tone B");

        Assert.DoesNotContain("The charter named this tone", text, StringComparison.Ordinal);
        // It is still the tone's NAME (the reply carries it), just not offered as a description of its sound.
        Assert.Contains("Tone name to use: Tone B", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_instruction_still_overrides_the_name_it_follows_and_the_hint_says_so()
    {
        const string instruction = "make the Clean tone a crunchy Marshall, not clean at all";
        var text = Prompt("Clean", instruction);

        var hint = text.IndexOf(RsToneGeneratePrompt.NameHintLine("Clean"), StringComparison.Ordinal);
        var section = text.IndexOf(RsToneGeneratePrompt.InstructionHeader, StringComparison.Ordinal);
        Assert.True(hint >= 0, "the descriptive name was not offered as a hint");
        Assert.True(section > hint, "the instruction does not follow the name hint");
        Assert.Contains(instruction, text[section..], StringComparison.Ordinal);
        Assert.Contains("the charter's instruction below win wherever they disagree", text[hint..section],
            StringComparison.Ordinal);
    }
}
