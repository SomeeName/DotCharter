using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     What a design request carries beyond the words typed: ONLINE RESEARCH for
///     the CLI providers (<see cref="AiProviderSettings.WebResearch" />, off by
///     default) and the player's TUNING from the rig tuner. The exact argument
///     lists with research ON are the providers' own contract tests; this pins the
///     switch both ways and where the instruction goes.
/// </summary>
public sealed class ToneDesignResearchTests
{
    private sealed class NoRunner : IClaudeCliRunner
    {
        public Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("never run");
    }

    private static ToneDesignPrompt Prompt(string? tuning = null) =>
        ToneDesignPromptBuilder.Build(new ToneDesignRequest("Master of Puppets rhythm", [], null,
            "Master of Puppets", "Metallica", tuning));

    [Fact]
    public void ResearchIsOffByDefault_AndAnOlderConfigGetsItOffToo()
    {
        Assert.False(new AiProviderSettings(AiProviderKind.ClaudeCli, "", null).WebResearch);
        Assert.False(new AiProviderSettings(AiProviderKind.CodexCli, "", null, UseLibraryTones: false).WebResearch);
    }

    [Fact]
    public void ClaudeCode_GetsTheWebTools_OnlyWhileResearchIsOn()
    {
        var on = new ClaudeCliToneModel(new AiProviderSettings(AiProviderKind.ClaudeCli, "", null, WebResearch: true), @"C:\x\claude.exe",
            new NoRunner(), workingDirectory: Path.GetTempPath()).BuildArguments(Prompt());
        var off = new ClaudeCliToneModel(new AiProviderSettings(AiProviderKind.ClaudeCli, "", null, WebResearch: false),
            @"C:\x\claude.exe", new NoRunner(), workingDirectory: Path.GetTempPath()).BuildArguments(Prompt());

        Assert.Equal(ToneDesignResearch.ClaudeCliTools, on[on.ToList().IndexOf("--tools") + 1]);
        Assert.Equal(ToneDesignResearch.ClaudeCliTools, on[on.ToList().IndexOf("--allowedTools") + 1]);
        Assert.Equal("", off[off.ToList().IndexOf("--tools") + 1]);
        Assert.DoesNotContain("--allowedTools", off);
        // never more than the two read-only web tools
        Assert.Equal(["WebSearch", "WebFetch"], ToneDesignResearch.ClaudeCliTools.Split(','));
    }

    [Fact]
    public void Codex_SearchesLive_OnlyWhileResearchIsOn_AndIsToldNotToOtherwise()
    {
        static string SearchFlag(bool research)
        {
            var dir = Path.GetTempPath();
            var args = new CodexCliToneModel(new AiProviderSettings(AiProviderKind.CodexCli, "", null, WebResearch: research),
                @"C:\x\codex.exe", new NoRunner(), workingDirectory: dir).BuildArguments("s", "r").ToList();
            return args[args.IndexOf("-c") + 1];
        }

        Assert.Equal("web_search=\"live\"", SearchFlag(true));
        Assert.Equal("web_search=\"disabled\"", SearchFlag(false));
    }

    [Fact]
    public void TheInstruction_RidesTheConversation_NeverTheCachedSystemPrompt()
    {
        var prompt = Prompt();
        Assert.DoesNotContain(ToneDesignResearch.Instruction, prompt.System);

        Assert.Contains(ToneDesignResearch.Instruction, ClaudeCliToneModel.BuildStandardInput(prompt, systemPromptOnFile: true, research: true));
        Assert.DoesNotContain(ToneDesignResearch.Instruction, ClaudeCliToneModel.BuildStandardInput(prompt, systemPromptOnFile: true));
        Assert.Contains(ToneDesignResearch.Instruction, CodexCliToneModel.BuildStandardInput(prompt, research: true));
        Assert.DoesNotContain(ToneDesignResearch.Instruction, CodexCliToneModel.BuildStandardInput(prompt));

        // …and before the request it is about
        var stdin = ClaudeCliToneModel.BuildStandardInput(prompt, systemPromptOnFile: true, research: true);
        Assert.True(stdin.IndexOf("## Research", StringComparison.Ordinal) < stdin.IndexOf("## Request", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePlayersTuning_IsSentWithTheRequest_WhenTheTunerHasOne()
    {
        var with = Prompt("Drop D (D A D G B E)").Messages[^1].Text;
        Assert.Contains("The player's guitar is tuned to: Drop D (D A D G B E)", with);
        Assert.DoesNotContain("tuned to", Prompt().Messages[^1].Text);
    }
}
