namespace DotCharter.Tests.Infrastructure;

/// <summary>
/// Pins <see cref="DefaultRunFilter"/>: a plain <c>dotnet test</c> runs the
/// DEFAULT GATE - it leaves out the headless-UI suite and every class of the
/// release lane (<c>tools/ci/release-lane.json</c>, run by
/// <c>Invoke-Gate.ps1 -ReleaseLane</c>) - and every invocation that names ANY
/// filter (the gate, the headless shards, the release lane, CI, the release
/// preflight) or that is not <c>dotnet test</c> at all (an IDE, the in-process
/// console runner) reaches the test framework unchanged. The discovered counts
/// behind this are in <c>DotCharter.Tests/README.md</c>.
/// </summary>
public sealed class DefaultRunFilterTests
{
    private static readonly string[] DotnetTest = ["--server", "dotnettestcli", "--dotnet-test-pipe", "pipe-1"];

    private static string[] DefaultGate() =>
        ["--filter-not-class", "*UiPerfSmokeTests", .. ReleaseLane.Read().SelectMany(e => new[] { "--filter-not-class", e.Class })];

    [Fact]
    public void A_plain_dotnet_test_runs_the_default_gate() =>
        Assert.Equal([.. DotnetTest, .. DefaultGate()], DefaultRunFilter.Apply(DotnetTest));

    [Fact]
    public void The_default_gate_leaves_out_the_release_lane_and_the_list_is_not_empty()
    {
        var applied = DefaultRunFilter.Apply(DotnetTest);
        Assert.NotEmpty(ReleaseLane.Read());
        foreach (var entry in ReleaseLane.Read())
            Assert.Contains(entry.Class, applied);
    }

    [Fact]
    public void Listing_tests_gets_the_same_default_as_running_them() =>
        Assert.Equal([.. DotnetTest, "--list-tests", .. DefaultGate()],
            DefaultRunFilter.Apply([.. DotnetTest, "--list-tests"]));

    [Fact]
    public void An_unreadable_release_lane_list_leaves_nothing_out()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "dotcharter-releaselane-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Empty(ReleaseLane.ExclusionArgs(scratch));
            File.WriteAllText(scratch, "{ not json");
            Assert.Empty(ReleaseLane.ExclusionArgs(scratch));
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    [Theory]
    [InlineData("--filter-not-class", "*UiPerfSmokeTests")]
    [InlineData("--filter-class", "*UiPerfSmokeTests")]
    [InlineData("--filter-class", "DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests")]
    [InlineData("--filter-method", "*Foo")]
    [InlineData("--filter-namespace", "DotCharter.Tests.Core")]
    [InlineData("--filter-trait", "Category=Slow")]
    [InlineData("--filter-query", "/*/*/*/*")]
    [InlineData("--filter-uid", "abc")]
    [InlineData("--treenode-filter", "/**")]
    public void Any_explicit_filter_switches_the_default_off(string option, string value)
    {
        string[] args = [.. DotnetTest, option, value, "--report-xunit-trx"];
        Assert.Same(args, DefaultRunFilter.Apply(args));
    }

    [Theory]
    [InlineData("--server")]
    [InlineData("--server", "jsonrpc", "--client-port", "5000")]
    [InlineData("--internal-msbuild-node", "x")]
    [InlineData("-class", "*AudioEvalTests*")]
    [InlineData]
    public void An_ide_or_the_console_runner_is_never_filtered(params string[] args) =>
        Assert.Same(args, DefaultRunFilter.Apply(args));
}
