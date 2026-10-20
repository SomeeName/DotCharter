using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The REAL process runner, over a benign stand-in child rather than
///     <c>claude</c> — nothing here costs a token or needs a login.
///     <para>
///         These are the only cases that can prove the kill path, which is the one
///         part of this provider a fake runner cannot cover: a design the user
///         cancelled must not leave a Node process holding the pipes.
///     </para>
/// </summary>
public sealed class ClaudeCliRunnerTests
{
    private static string Shell => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    [Fact]
    public async Task ItCapturesStdout_StdinAndTheExitCode()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "uses cmd.exe as the stand-in child");

        var outcome = await new ClaudeCliRunner().RunAsync(
            new ClaudeCliRequest(Shell, ["/c", "more & exit 3"], "hello from stdin",
                Path.GetTempPath(), TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        // `more` copies stdin to stdout, so this proves the payload was written AND
        // that the stream was CLOSED — an open stdin is a child that never exits.
        Assert.Contains("hello from stdin", outcome.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(3, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task Cancellation_KillsTheChild_AndThrows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "uses cmd.exe as the stand-in child");

        using var cts = new CancellationTokenSource();
        // The PIDs that already exist, not a COUNT (#236): other tests start and
        // end ping processes at the same time, so a count can stand still while
        // ours starts. Same pattern as StemToolChildProcessTests.
        var before = PingProcessIds();

        var run = new ClaudeCliRunner().RunAsync(
            // A child that would sit there for half a minute, plus a grandchild, so
            // the entireProcessTree half of the kill is exercised too.
            new ClaudeCliRequest(Shell, ["/c", "ping -n 30 127.0.0.1 > nul"], "",
                Path.GetTempPath(), TimeSpan.FromMinutes(5)),
            cts.Token);

        // Wait for the child to actually exist before cancelling, so this cannot
        // pass by cancelling before anything was started.
        HashSet<int> spawned;
        while (true)
        {
            spawned = PingProcessIds();
            spawned.ExceptWith(before);
            if (spawned.Count > 0 || run.IsCompleted) break;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(spawned.Count > 0, "the stand-in child never started");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Structure, not performance: the assertion is that the child is GONE, and
        // the bound is only there so a broken kill path fails instead of hanging
        // the suite. Ten seconds against a kill that takes milliseconds — no timing
        // claim is being made.
        for (var i = 0; i < 500 && AnyAlive(spawned); i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(AnyAlive(spawned), "the cancelled child was not killed");
    }

    [Fact]
    public async Task ADeadlineThatExpires_IsAnAnswer_NotACancellation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "uses cmd.exe as the stand-in child");

        var outcome = await new ClaudeCliRunner().RunAsync(
            new ClaudeCliRequest(Shell, ["/c", "ping -n 30 127.0.0.1 > nul"], "",
                Path.GetTempPath(), TimeSpan.FromMilliseconds(250)),
            CancellationToken.None);

        // A timeout comes BACK as an outcome the provider maps to a Network fault;
        // only the caller's own token throws. Conflating the two would make every
        // slow model look like a user pressing Cancel.
        Assert.True(outcome.TimedOut);
        Assert.Equal(-1, outcome.ExitCode);
    }

    [Fact]
    public async Task AMissingBinary_IsNotConfigured()
    {
        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => new ClaudeCliRunner().RunAsync(
            new ClaudeCliRequest(
                Path.Combine(Path.GetTempPath(), $"no-such-claude-{Guid.NewGuid():N}.exe"),
                ["--version"], "", Path.GetTempPath(), TimeSpan.FromSeconds(5)),
            CancellationToken.None));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
    }

    private static HashSet<int> PingProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("PING"))
        {
            ids.Add(p.Id);
            p.Dispose();
        }

        return ids;
    }

    private static bool AnyAlive(HashSet<int> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(id);
                if (!p.HasExited) return true;
            }
            catch (ArgumentException)
            {
                // gone
            }
            catch (InvalidOperationException)
            {
                // gone
            }
        }

        return false;
    }
}
