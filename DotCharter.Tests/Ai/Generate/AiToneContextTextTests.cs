using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     The charter's own words for Generate tones with AI… (#316): cleaned, capped, carried in the
///     request's context beside the metadata, and printed in the prompt the model reads. An empty box
///     leaves both exactly as the metadata alone wrote them.
/// </summary>
public sealed class AiToneContextTextTests
{
    private static ChartProject Project() => new()
    {
        Chart = { Artist = "Sakasma", SongName = "Polkadot Stingray", AlbumName = "Album", AlbumYear = "2019" }
    };

    private static string Prompt(string? context)
    {
        var features = new ToneWindowFeatureExtractor().Extract(GenerateFakes.Playing());
        var prompt = RsToneGeneratePrompt.Build(
            new RsToneMapRequest(features, context, "Tone_A", "Tone A", ToneStems.Guitar, "Polkadot Stingray", "Sakasma"));
        return prompt.Messages[0].Text.ReplaceLineEndings("\n");
    }

    [Fact]
    public void The_typed_text_reaches_the_context_beside_the_metadata_and_the_prompt()
    {
        var arrangement = new Arrangement { Name = "Lead" };

        var context = AiToneGenerationRun.ContextFor(Project(), arrangement, "The solo is a wah into a cranked Marshall.");

        Assert.Equal(
            "band: Sakasma, song: Polkadot Stingray, album: Album, year: 2019, arrangement: Lead, " +
            "the charter's notes: The solo is a wah into a cranked Marshall.",
            context);
        Assert.Contains("the charter's notes: The solo is a wah into a cranked Marshall.", Prompt(context), StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_alone_is_a_context_when_the_project_knows_nothing()
    {
        var project = new ChartProject { Chart = { SongName = "", Artist = "" } };

        Assert.Equal(
            "the charter's notes: dark and heavy",
            AiToneGenerationRun.ContextFor(project, new Arrangement { Name = "" }, "dark and heavy"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public void Empty_text_leaves_the_context_and_the_prompt_unchanged(string? typed)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var without = AiToneGenerationRun.ContextFor(Project(), arrangement);

        var with = AiToneGenerationRun.ContextFor(Project(), arrangement, typed);

        Assert.Equal(without, with);
        Assert.Equal(Prompt(without), Prompt(with));
        Assert.DoesNotContain("charter's notes", Prompt(with), StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_is_capped_so_a_pasted_essay_is_not_sent_with_every_tone()
    {
        var typed = new string('x', AiToneContextText.MaxLength * 3);

        var cleaned = AiToneContextText.Normalize(typed);

        Assert.Equal(AiToneContextText.MaxLength, cleaned!.Length);
        var context = AiToneGenerationRun.ContextFor(Project(), new Arrangement(), typed)!;
        Assert.EndsWith(AiToneContextText.Label + new string('x', AiToneContextText.MaxLength), context, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cap_never_cuts_a_surrogate_pair_in_half()
    {
        // 499 letters, then an emoji (two UTF-16 units) straddling the 500 cap.
        var typed = new string('a', AiToneContextText.MaxLength - 1) + "😀tail";

        var cleaned = AiToneContextText.Normalize(typed)!;

        Assert.Equal(AiToneContextText.MaxLength - 1, cleaned.Length);
        Assert.False(char.IsHighSurrogate(cleaned[^1]));
    }

    [Fact]
    public void Line_breaks_and_runs_of_spaces_collapse_to_one_line() =>
        Assert.Equal("verse: clean comes later", AiToneContextText.Normalize("  verse:   clean\r\n\r\ncomes\tlater \n"));
}
