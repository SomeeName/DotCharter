using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/agent/DevBuild.psm1</c> (#153): the agent scripts' dev builds run
///     with the SDK's implicit SourceLink off, so a new commit (an agent's own in
///     a worktree, a train's merge commit) does not recompile every project.
///     The module is executed under Windows PowerShell 5.1; the two scripts are
///     linted for calling it, and the release scripts for never doing so.
/// </summary>
public sealed class DevBuildModuleTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-devbuild-" + Guid.NewGuid().ToString("N"));

    public DevBuildModuleTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => DotCharter.Tone.Infrastructure.Storage.BestEffortDelete.Directory(_scratch);

    [Fact]
    public void Enable_TurnsSourceLinkOffWhenUnset() =>
        Assert.Equal("true", Run("$env:SuppressImplicitGitSourceLink = $null\nEnable-IncrementalDevBuild\n$env:SuppressImplicitGitSourceLink"));

    [Fact]
    public void Enable_KeepsAValueTheCallerSet() =>
        Assert.Equal("false", Run("$env:SuppressImplicitGitSourceLink = 'false'\nEnable-IncrementalDevBuild\n$env:SuppressImplicitGitSourceLink"));

    [Theory]
    [InlineData("Invoke-Gate.ps1")]
    [InlineData("Merge-Train.ps1")]
    public void DevBuildScripts_ImportTheModuleAndEnableItBeforeBuilding(string script)
    {
        var code = Code(Read("tools", "agent", script));
        Assert.Contains("'DevBuild.psm1'", code);
        Assert.Matches(@"(?m)^\s*Enable-IncrementalDevBuild\s*$", code);
        Assert.DoesNotContain("SuppressImplicitGitSourceLink", code);
    }

    [Fact]
    public void InvokeGate_EnablesItInsideTheBuildFunction()
    {
        var code = Code(Read("tools", "agent", "Invoke-Gate.ps1"));
        var build = code.IndexOf("function Invoke-Build", StringComparison.Ordinal);
        var enable = code.IndexOf("Enable-IncrementalDevBuild", build, StringComparison.Ordinal);
        var dotnet = code.IndexOf("& dotnet @buildArgs", build, StringComparison.Ordinal);
        Assert.True(build >= 0 && enable > build && enable < dotnet,
            "Invoke-Build must call Enable-IncrementalDevBuild before its `dotnet build`.");
    }

    // ---- the CPU plan (#171): the decision is pure, so every case injects its inputs ----

    [Theory]
    [InlineData("-Processors 16", "half|FF00|4|default")]
    [InlineData("-Processors 24", "half|FF00|4|default")]
    [InlineData("-Processors 16 -AllCores", "all|FFFF|8|-AllCores")]
    [InlineData("-Processors 24 -AllCores", "all|FFFFFF|8|-AllCores")]
    [InlineData("-Processors 8", "unchanged|0|4|8 logical")]
    [InlineData("-Processors 8 -AllCores", "unchanged|0|4|8 logical")]
    public void CpuPlan_DefaultsToHalfAndGoesWideOnlyOnRequest(string args, string expected)
    {
        var plan = Run($"$p = Get-DevCpuPlan {args}\n'{{0}}|{{1:X}}|{{2}}|{{3}}' -f $p.Name, $p.Mask, $p.Shards, $p.Reason");
        var (name, mask, shards, reason) = (expected.Split('|')[0], expected.Split('|')[1], expected.Split('|')[2], expected.Split('|')[3]);
        var got = plan.Split('|');
        Assert.Equal([name, mask, shards], got[..3]);
        Assert.Contains(reason, got[3]);
    }

    [Fact]
    public void CpuPlan_HasNoGameDetectionAndNoHalfCoresSwitch()
    {
        var module = Code(Read("tools", "agent", "DevBuild.psm1"));
        foreach (var gone in new[] { "Read-GameProcessList", "game-processes", "GameProcesses", "RunningProcesses", "HalfCores" })
            Assert.DoesNotContain(gone, module);
    }

    [Theory]
    [InlineData("Invoke-Gate.ps1")]
    [InlineData("Merge-Train.ps1")]
    public void DevBuildScripts_PinThroughTheSharedPlanAndKeepNoMaskOfTheirOwn(string script)
    {
        var code = Code(Read("tools", "agent", script));
        Assert.Matches(@"(?m)Set-DevCpuPlan\s+-AllCores:\$AllCores\s*$", code);
        Assert.DoesNotContain("HalfCores", code);
        Assert.DoesNotContain("ProcessorAffinity", code);
        Assert.DoesNotContain("0xFF00", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MergeTrain_PassesItsChoiceToEveryChildGate()
    {
        var code = Code(Read("tools", "agent", "Merge-Train.ps1"));
        var calls = Regex.Matches(code, @"-File \$gate[^\r\n]*");
        Assert.Equal(4, calls.Count);
        Assert.All(calls, c => Assert.Contains("@cpuSwitch", c.Value));
        Assert.Contains("@(if ($cpuPlan.Name -eq 'all') { '-AllCores' })", code);
    }

    [Theory]
    [InlineData("New-Release.ps1")]
    [InlineData("Export-PublicSnapshot.ps1")]
    public void ReleaseScripts_KeepTheRealVersionStamp(string script)
    {
        var code = Code(Read("tools", "release", script));
        Assert.DoesNotContain("Enable-IncrementalDevBuild", code);
        Assert.DoesNotContain("SuppressImplicitGitSourceLink", code);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts]));

    private static string Code(string text) => string.Join('\n', Regex.Replace(text, "<#.*?#>", "", RegexOptions.Singleline)
        .Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    private string Run(string body) => HeadlessShardsModule.RunModule(_scratch, body, "tools", "agent", "DevBuild.psm1");
}
