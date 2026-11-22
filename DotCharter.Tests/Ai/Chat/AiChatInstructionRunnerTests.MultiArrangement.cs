using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Tests.Ai.Chat;

/// <summary>#478: one request over several arrangements runs the pass on each in turn; the reviews queue and show one by one.</summary>
public sealed partial class AiChatInstructionRunnerTests
{
    private Arrangement AddRhythm()
    {
        var rhythm = ChartProjectCloner.CloneArrangement(_f.Arrangement);
        rhythm.Name = "Rhythm";
        _f.Project.Arrangements.Add(rhythm);
        return rhythm;
    }

    [Fact]
    public async Task One_request_over_two_arrangements_runs_each_and_queues_the_second_review_behind_the_first()
    {
        var rhythm = AddRhythm();
        var (runner, run, _) = Runner(Model());
        var request = new AiChatInstructionRequest(Octaves, AiChatScope.WholeArrangement(_f.Arrangement))
        {
            MoreScopes = [AiChatScope.WholeArrangement(rhythm)]
        };

        var reply = await runner.RunInstructionAsync(request, CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Held, reply.Status);
        Assert.Same(_f.Arrangement, run.Review!.Arrangement);
        Assert.Equal(1, run.QueuedReviewCount);
        // The reply says which arrangement each part is about, in the order they ran.
        var lead = reply.Text.IndexOf("Lead:", StringComparison.Ordinal);
        Assert.True(lead >= 0);
        Assert.True(reply.Text.IndexOf("Rhythm:", StringComparison.Ordinal) > lead);

        // Discarding the first brings the second one up; then none is left.
        run.DiscardReview();
        Assert.Same(rhythm, run.Review!.Arrangement);
        Assert.Equal(0, run.QueuedReviewCount);
        run.DiscardReview();
        Assert.Null(run.Review);
    }

    [Fact]
    public async Task A_request_over_one_arrangement_is_unchanged_no_arrangement_header_and_no_queue()
    {
        var (runner, run, _) = Runner(Model());

        var reply = await runner.RunInstructionAsync(
            new AiChatInstructionRequest(Octaves, AiChatScope.WholeArrangement(_f.Arrangement)), CancellationToken.None);

        Assert.DoesNotContain("Lead:", reply.Text, StringComparison.Ordinal);
        Assert.Equal(0, run.QueuedReviewCount);
    }

    [Fact]
    public async Task A_new_single_request_replaces_the_queued_reviews_too()
    {
        var rhythm = AddRhythm();
        var (runner, run, _) = Runner(Model());
        await runner.RunInstructionAsync(new AiChatInstructionRequest(Octaves, AiChatScope.WholeArrangement(_f.Arrangement))
        {
            MoreScopes = [AiChatScope.WholeArrangement(rhythm)]
        }, CancellationToken.None);
        Assert.Equal(1, run.QueuedReviewCount);

        await runner.RunInstructionAsync(
            new AiChatInstructionRequest(Octaves, AiChatScope.WholeArrangement(rhythm)), CancellationToken.None);

        Assert.Equal(0, run.QueuedReviewCount);
        Assert.Same(rhythm, run.Review!.Arrangement);
    }
}
