using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

public sealed class ClaudeCliOutcomeLogLineTests
{
    [Fact]
    public void TheLogLine_NeverQuotesStandardOutput()
    {
        const string reply = "{\"tone\":\"the user's private design\"}";
        var outcome = new ClaudeCliOutcome(0, reply, "warning: slow");

        var line = outcome.ToRedactedLogLine();

        Assert.DoesNotContain("private design", line, StringComparison.Ordinal);
        Assert.Contains($"stdout={reply.Length} chars", line, StringComparison.Ordinal);
        Assert.Contains("stderr=warning: slow", line, StringComparison.Ordinal);
        Assert.StartsWith("exit=0,", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongStandardError_IsCutAt400Characters()
    {
        var outcome = new ClaudeCliOutcome(1, "", new string('e', 1000));

        var line = outcome.ToRedactedLogLine();

        Assert.Contains($"stderr={new string('e', 400)}…,", line, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('e', 401), line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyStandardError_IsNamedEmpty()
    {
        var outcome = new ClaudeCliOutcome(-1, "", "   ", TimedOut: true);

        Assert.Equal("exit=-1, stderr=(empty), stdout=0 chars", outcome.ToRedactedLogLine());
    }
}
