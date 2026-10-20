using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Tests.Ai.Chart;

namespace DotCharter.Tests.Ai.Chat;

/// <summary>
///     The chat's answer to a sections &amp; phrases instruction (#561): the job the pane picked
///     reaches the run, the proposals are HELD as section/phrase changes, and the reply lists each
///     one in the review card's own words. On a FAKE model.
/// </summary>
public sealed class AiChatStructureReplyTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };

    public AiChatStructureReplyTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    [Fact]
    public async Task A_sections_and_phrases_instruction_holds_marker_changes_and_lists_them()
    {
        var model = new FakeChartModel((_, _) => JsonSerializer.Serialize(new
        {
            changes = new object[]
            {
                new { op = "add", kind = "section", bar = 5, beat = (int?)null, name = "chorus" },
                new { op = "rename", kind = (string?)null, id = "p2", bar = (int?)null, beat = (int?)null, name = "chorus1" }
            }
        }));
        var run = new AiChartPassRun(_state, _history, null, _loc, new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model), () => _config, () => null);
        var runner = new AiChatInstructionRunner(run, _loc);

        var reply = await runner.RunInstructionAsync(new AiChatInstructionRequest("Mark the chorus.",
            AiChatScope.WholeArrangement(), Job: AiChartJob.SectionsAndPhrases), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Held, reply.Status);
        Assert.Same(run.Review, reply.Review);
        Assert.Equal(AiChartJob.SectionsAndPhrases, reply.Review!.Job);
        Assert.Equal(2, reply.Review.Items.Count);
        Assert.Equal(reply.Review.Id, reply.ToTurn(DateTimeOffset.UnixEpoch).ReviewId);
        Assert.StartsWith(_loc.Plural("AiChat_StructureProposed_One", "AiChat_StructureProposed_Many", 2), reply.Text,
            StringComparison.Ordinal);
        Assert.Contains(_loc.Format("AiChat_ProposedSection", "Phrase · chorus1", "0:08", "Renamed from “verse”"), reply.Text,
            StringComparison.Ordinal);
        Assert.Contains(_loc.Format("AiChat_ProposedSection", "Section · Chorus", "0:08", "New"), reply.Text,
            StringComparison.Ordinal);
        // The prompt is the structure job's, never the FHP / fingering one.
        Assert.Equal(AiStructurePrompt.SchemaName, Assert.Single(model.Prompts).SchemaName);
        Assert.Equal(2, _f.Arrangement.EventPoints.Count);
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
