using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The daily public sync watch (#77) is the ONE workflow allowed a trigger other than the
///     manual button (<see cref="CiWorkflowLintTests" />), so it stays what made that
///     acceptable: one tiny ubuntu job, at most once a day, a 5-minute cap, that reads public
///     data and its own history and can write nothing but issues — no secret, no push, no
///     submodule (the private JuceSharp would need a token), and it runs only in the dev repo.
/// </summary>
public sealed class PublicSyncWatchWorkflowLintTests
{
    public const string FileName = "public-sync-watch.yml";

    private static string Yaml() =>
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), ".github", "workflows", FileName)).Replace("\r\n", "\n");

    // The YAML with comments removed, so a comment cannot satisfy or trip a rule.
    private static string Code() => Regex.Replace(Yaml(), @"(?m)^\s*#.*\n|[ \t]+#.*$", "");

    [Fact]
    public void The_watch_is_one_tiny_daily_linux_job()
    {
        var code = Code();
        var crons = Regex.Matches(code, @"(?m)^\s*- cron: '([^']+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Single(crons);
        // Minute and hour fixed, every day: once a day, never more.
        Assert.Matches(@"^\d{1,2} \d{1,2} \* \* \*$", crons[0]);

        var jobs = Regex.Matches(code[code.IndexOf("\njobs:", StringComparison.Ordinal)..], @"(?m)^  ([a-z][\w-]*):\s*$");
        Assert.Single(jobs);
        Assert.Contains("runs-on: ubuntu-latest", code, StringComparison.Ordinal);
        var timeout = Regex.Match(code, @"timeout-minutes:\s*(\d+)");
        Assert.True(timeout.Success && int.Parse(timeout.Groups[1].Value) <= 5, "the watch needs a timeout of at most 5 minutes");
        Assert.Contains("if: (github.event_name == 'workflow_dispatch' || github.event_name == 'schedule') && endsWith(github.repository, '-Dev')", code, StringComparison.Ordinal);

        var script = Regex.Match(code, @"run: pwsh -NoProfile -File (tools/release/Find-UnimportedPublicCommits\.ps1)\b");
        Assert.True(script.Success, "the watch runs the shared check script");
        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot(), script.Groups[1].Value)));
        foreach (var action in Regex.Matches(code, @"(?m)^\s*(?:-\s+)?uses:\s*(\S+)").Select(m => m.Groups[1].Value))
            Assert.Matches(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$", action);
    }

    [Fact]
    public void The_watch_reads_public_data_and_writes_only_issues()
    {
        var code = Code();
        // Exactly one permissions block, at the top: read the dev checkout (private, so
        // `contents: read` is needed to clone it), write issues. Nothing else.
        var blocks = Regex.Matches(code, @"(?m)^(\s*)permissions:[ \t]*\n((?:\1  [a-z-]+: [a-z]+\n)+)");
        Assert.Single(blocks);
        Assert.Equal("", blocks[0].Groups[1].Value);
        var granted = Regex.Matches(blocks[0].Groups[2].Value, @"([a-z-]+): ([a-z]+)").Select(m => $"{m.Groups[1].Value}={m.Groups[2].Value}").Order(StringComparer.Ordinal);
        Assert.Equal(["contents=read", "issues=write"], granted);
        Assert.Single(Regex.Matches(code, @"permissions:"));

        Assert.DoesNotContain("secrets.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("git push", code, StringComparison.Ordinal);
        Assert.Matches(@"submodules: false", code);
        Assert.Matches(@"persist-credentials: false", code);
        Assert.Matches(@"GH_TOKEN: \$\{\{ github\.token \}\}", code);
    }
}
