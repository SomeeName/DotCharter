namespace DotCharter.Tests.Infrastructure;

/// <summary>
/// Pins <see cref="DefaultRunFilter"/>: a plain <c>dotnet test</c> leaves the
/// headless-UI suite out, and every invocation that names ANY filter (the gate,
/// the headless shards, CI, the release preflight) or that is not
/// <c>dotnet test</c> at all (an IDE, the in-process console runner) reaches the
/// test framework unchanged. The discovered counts behind this are in
/// <c>DotCharter.Tests/README.md</c>.
/// </summary>
public sealed class DefaultRunFilterTests
{
    private static readonly string[] DotnetTest = ["--server", "dotnettestcli", "--dotnet-test-pipe", "pipe-1"];

    [Fact]
    public void A_plain_dotnet_test_excludes_the_headless_suite() =>
        Assert.Equal([.. DotnetTest, "--filter-not-class", "*UiPerfSmokeTests"], DefaultRunFilter.Apply(DotnetTest));

    [Fact]
    public void Listing_tests_gets_the_same_default_as_running_them() =>
        Assert.Equal([.. DotnetTest, "--list-tests", "--filter-not-class", "*UiPerfSmokeTests"],
            DefaultRunFilter.Apply([.. DotnetTest, "--list-tests"]));

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
