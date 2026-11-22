using static DotCharter.Tests.Infrastructure.PublicSnapshotExportTests;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     What the public export's exit code and scratch root promise
///     (<c>tools/release/Export-PublicSnapshot.ps1</c> over <c>PublicSnapshot.psm1</c>): every
///     requested build or gate and every executed gitleaks run counts in the final status, dry
///     run included, and two exports never work in one scratch root at once. The module's pure
///     functions run under pwsh; the script is read, never executed here.
/// </summary>
public sealed class PublicSnapshotExportStatusTests
{
    private static string Release(string file) => Path.Combine(TestPaths.RepoRoot(), "tools", "release", file);

    [Fact]
    public void ExportFailures_CountARequestedBuildAndAnExecutedGitleaks_NotOnlyLeaksAndPreflight()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-status-").FullName;
        try
        {
            var run = RunPwsh(scratch, $$"""
                Import-Module {{Quote(Release("PublicSnapshot.psm1"))}} -Force
                function Show([string]$Case, [hashtable]$Splat) { "$Case=" + (@(Get-SnapshotExportFailures @Splat) -join ' | ') }
                Show 'clean' @{ LeakFindings = 0; PreflightProblems = 0 }
                Show 'buildFailed' @{ BuildRequested = $true; BuildExit = 1 }
                Show 'buildPassed' @{ BuildRequested = $true; BuildExit = 0 }
                Show 'buildNoExit' @{ BuildRequested = $true; BuildExit = $null }
                Show 'buildNotRequested' @{ BuildExit = $null }
                Show 'gitleaksFailed' @{ GitleaksExit = 1 }
                Show 'gitleaksClean' @{ GitleaksExit = 0 }
                Show 'gitleaksAbsent' @{ GitleaksExit = $null }
                Show 'leaks' @{ LeakFindings = 2 }
                Show 'preflight' @{ PreflightProblems = 1 }
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            var lines = run.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains('=', StringComparison.Ordinal))
                .ToDictionary(l => l[..l.IndexOf('=', StringComparison.Ordinal)], l => l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..]);

            Assert.Equal("", lines["clean"]);
            Assert.Contains("build/gate failed (exit 1)", lines["buildFailed"], StringComparison.Ordinal);
            Assert.Equal("", lines["buildPassed"]);
            Assert.Contains("build/gate failed (exit unknown)", lines["buildNoExit"], StringComparison.Ordinal);
            Assert.Equal("", lines["buildNotRequested"]);
            Assert.Contains("gitleaks failed or reported findings (exit 1)", lines["gitleaksFailed"], StringComparison.Ordinal);
            Assert.Equal("", lines["gitleaksClean"]);
            // Not installed is not a dry-run failure: -Push refuses it on its own.
            Assert.Equal("", lines["gitleaksAbsent"]);
            Assert.Contains("2 finding(s)", lines["leaks"], StringComparison.Ordinal);
            Assert.Contains("push preflight refused", lines["preflight"], StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void ExportScript_ExitsOnEveryFailure_ItsBuildAndGitleaksIncluded()
    {
        var source = ScriptWithoutComments();
        var exit = source[source.LastIndexOf("Get-SnapshotExportFailures", StringComparison.Ordinal)..];
        Assert.Contains("-BuildExit $buildExit", exit, StringComparison.Ordinal);
        Assert.Contains("-GitleaksExit $gitleaksExit", exit, StringComparison.Ordinal);
        Assert.Contains("-BuildRequested:($Build -or $Gate)", exit, StringComparison.Ordinal);
        Assert.Contains("exit $(if ($failures.Count -gt 0) { 1 } else { 0 })", exit, StringComparison.Ordinal);
        // A build that never ran leaves no stale exit code behind.
        Assert.Contains("$buildExit = $null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScratchLock_RefusesASecondHolder_UntilTheFirstReleasesIt()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-lock-").FullName;
        try
        {
            var root = Path.Combine(scratch, "s1");
            var run = RunPwsh(scratch, $$"""
                Import-Module {{Quote(Release("PublicSnapshot.psm1"))}} -Force
                $first = Enter-SnapshotScratchLock -ScratchRoot {{Quote(root)}}
                try { $second = Enter-SnapshotScratchLock -ScratchRoot {{Quote(root)}}; 'second=acquired'; $second.Dispose() }
                catch { "second=refused: $($_.Exception.Message)" }
                $first.Dispose()
                $third = Enter-SnapshotScratchLock -ScratchRoot {{Quote(root)}}
                'third=acquired'
                $third.Dispose()
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("second=refused: Another export holds the scratch root", run.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("second=acquired", run.Output, StringComparison.Ordinal);
            Assert.Contains("third=acquired", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void ExportScript_TakesTheScratchLock_BeforeItDeletesAnything()
    {
        var source = ScriptWithoutComments();
        var lockAt = source.IndexOf("Enter-SnapshotScratchLock -ScratchRoot $ScratchRoot", StringComparison.Ordinal);
        var firstDelete = source.IndexOf("Remove-Item", StringComparison.Ordinal);
        Assert.True(lockAt >= 0, "Export-PublicSnapshot.ps1 must hold Enter-SnapshotScratchLock for its scratch root.");
        Assert.True(firstDelete > lockAt, "Export-PublicSnapshot.ps1 deletes in its scratch root before it holds the scratch lock.");
        Assert.True(source.IndexOf("New-Item -ItemType Directory -Path $ScratchRoot", StringComparison.Ordinal) > lockAt,
            "Export-PublicSnapshot.ps1 writes its scratch root before it holds the scratch lock.");
    }

    // The help block and every comment line dropped, so prose cannot pass a lint.
    private static string ScriptWithoutComments()
    {
        var text = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Release("Export-PublicSnapshot.ps1")).Replace("\r\n", "\n", StringComparison.Ordinal),
            "<#.*?#>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }
}
