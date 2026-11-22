using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     How a headless run is graded from what each PROCESS left behind (#611), in
///     <c>tools/ci/HeadlessShards.psm1</c>, shared by <c>tools/ci/Invoke-HeadlessUiCi.ps1</c>
///     and <c>tools/release/New-Release.ps1</c>: an exit code its results do not explain,
///     an isolated re-run that turns up a new failure, a case the harness fault hits again,
///     a discovery that failed part-way, and a child process with no console window.
///     Executed against the module itself under Windows PowerShell 5.1.
/// </summary>
public sealed class HeadlessGradingTests : IDisposable
{
    private const string ClassA = "DotCharter.Tests.Perf.AlphaUiPerfSmokeTests";
    private const string ClassB = "DotCharter.Tests.Perf.BravoUiPerfSmokeTests";
    private const string ClassC = "DotCharter.Tests.Perf.CharlieUiPerfSmokeTests";

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-headless-grading-" + Guid.NewGuid().ToString("N"));

    public HeadlessGradingTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    /// <summary>Microsoft.Testing.Platform: 0 passed, 2 a test failed, 8 zero tests ran; anything else is incomplete.</summary>
    [Theory]
    [InlineData("0", 5, 0, true)]
    [InlineData("2", 5, 1, true)]
    [InlineData("2", 5, 0, false)]
    [InlineData("3", 5, 0, false)]
    [InlineData("7", 4, 0, false)]
    [InlineData("8", 2, 0, true)]
    [InlineData("8", 0, 0, false)]
    [InlineData("124", 0, 0, false)]
    [InlineData("$null", 5, 0, false)]
    public void ExitAccepted_OnlyWhenTheResultsExplainTheExitCode(string exitCode, int total, int failed, bool expected) =>
        Assert.Equal(expected.ToString(), Run($"Test-HeadlessExitAccepted {exitCode} {total} {failed}"));

    /// <summary>A shard that crashed after writing only passes is incomplete, not green: its classes are re-run.</summary>
    [Fact]
    public void IncompleteClasses_AreThoseOfShardsWhoseExitTheirTrxDoesNotExplain()
    {
        var clean = WriteTrx(total: 3, failed: 0);
        var crashedAfterPasses = WriteTrx(total: 2, failed: 0);
        var failing = WriteTrx(total: 4, failed: 1);

        var output = Run($$"""
            $run = [pscustomobject]@{ Shards = @(
                [pscustomobject]@{ Classes = @('{{ClassA}}'); ExitCode = 0; TrxPath = '{{clean}}' },
                [pscustomobject]@{ Classes = @('{{ClassB}}'); ExitCode = 7; TrxPath = '{{crashedAfterPasses}}' },
                [pscustomobject]@{ Classes = @('{{ClassC}}'); ExitCode = 2; TrxPath = '{{failing}}' }) }
            $single = [pscustomobject]@{ ExitCode = 3; Total = 9; Failed = 0 }
            $singleOk = [pscustomobject]@{ ExitCode = 0; Total = 9; Failed = 0 }
            "sharded=$((Get-HeadlessIncompleteClasses $run @('{{ClassA}}', '{{ClassB}}', '{{ClassC}}')) -join ',')"
            "single=$((Get-HeadlessIncompleteClasses $single @('{{ClassA}}', '{{ClassB}}')) -join ',')"
            "single-ok=$((Get-HeadlessIncompleteClasses $singleOk @('{{ClassA}}')) -join ',')"
            """);

        Assert.Equal($"sharded={ClassB}\nsingle={ClassA},{ClassB}\nsingle-ok=", Normalize(output));
    }

    /// <summary>
    ///     A class that reported nothing usable must report in the retry; a retry whose own exit
    ///     is unexplained blocks; a case the harness fault hits again is unverified, never green.
    /// </summary>
    [Fact]
    public void Retry_BlocksOnSilentClassesAndAnUnexplainedExit_AndKeepsRepeatHarnessFaultsUnverified()
    {
        var output = Run($$"""
            $harness = @([pscustomobject]@{ Name = '{{ClassA}}.Opens'; Class = '{{ClassA}}'; IsHarness = $true; Frame = '' })
            $again = [pscustomobject]@{ Name = '{{ClassA}}.Opens'; Class = '{{ClassA}}'; IsHarness = $true; Frame = '' }
            $retry = [pscustomobject]@{ Failures = @($again); RanClasses = @('{{ClassA}}'); ExitCode = 2; Total = 3; Failed = 1 }
            $g = Resolve-HeadlessRetry $retry $harness @('{{ClassC}}')
            "product=$(@($g.Product | ForEach-Object { $_.Name }) -join ',')"
            "unverified=$(@($g.Unverified) -join ',')"
            $crashed = [pscustomobject]@{ Failures = @(); RanClasses = @('{{ClassA}}', '{{ClassC}}'); ExitCode = 7; Total = 5; Failed = 0 }
            $g2 = Resolve-HeadlessRetry $crashed $harness @('{{ClassC}}')
            "crashed=$(@($g2.Product | ForEach-Object { $_.Name }) -join ',')"
            """);

        Assert.Equal(
            $"product={ClassC}\nunverified={ClassA}.Opens\ncrashed=retry process (exit 7)",
            Normalize(output));
    }

    /// <summary>
    ///     The #611 row 2 case: A fails in the full run and passes alone, while B, green in the
    ///     full run, fails in that same isolated class run. A is order-dependent; B blocks.
    /// </summary>
    [Fact]
    public void Isolated_ANewFailureBlocks_WhileTheCandidateThatPassedIsOrderDependent()
    {
        var output = Run($$"""
            $a = [pscustomobject]@{ Name = '{{ClassA}}.First'; Class = '{{ClassA}}'; Message = 'x'; Frame = 'at A.cs:line 1'; IsHarness = $false }
            $b = [pscustomobject]@{ Name = '{{ClassA}}.Second'; Class = '{{ClassA}}'; Message = 'y'; Frame = 'at A.cs:line 9'; IsHarness = $false }
            $isolated = [pscustomobject]@{ Failures = @($b); PassedTests = @('{{ClassA}}.First'); RanClasses = @('{{ClassA}}'); ExitCode = 2; Total = 2; Failed = 1 }
            $g = Resolve-HeadlessIsolatedRun @($a) $isolated
            "product=$(@($g.Product | ForEach-Object { $_.Name }) -join ',')"
            "order-dependent=$(@($g.OrderDependent | ForEach-Object { $_.Name }) -join ',')"
            """);

        Assert.Equal($"product={ClassA}.Second\norder-dependent={ClassA}.First", Normalize(output));
    }

    /// <summary>An isolated process that crashed clears nothing, and a candidate that did not pass there (skipped, absent) stays.</summary>
    [Fact]
    public void Isolated_AnUnexplainedExitOrAMissingPassClearsNothing()
    {
        var output = Run($$"""
            $a = [pscustomobject]@{ Name = '{{ClassA}}.First'; Class = '{{ClassA}}'; Message = 'x'; Frame = 'at A.cs:line 1'; IsHarness = $false }
            $crashed = [pscustomobject]@{ Failures = @(); PassedTests = @('{{ClassA}}.First'); RanClasses = @('{{ClassA}}'); ExitCode = 7; Total = 1; Failed = 0 }
            $g = Resolve-HeadlessIsolatedRun @($a) $crashed
            "crashed=$(@($g.Product | ForEach-Object { $_.Name }) -join ',')|$(@($g.OrderDependent).Count)"
            $skipped = [pscustomobject]@{ Failures = @(); PassedTests = @('{{ClassA}}.Other'); RanClasses = @('{{ClassA}}'); ExitCode = 0; Total = 2; Failed = 0 }
            $g2 = Resolve-HeadlessIsolatedRun @($a) $skipped
            "not-passed=$(@($g2.Product | ForEach-Object { $_.Name }) -join ',')|$(@($g2.OrderDependent).Count)"
            """);

        Assert.Equal(
            $"crashed={ClassA}.First,isolated process (exit 7)|0\nnot-passed={ClassA}.First|0",
            Normalize(output));
    }

    /// <summary>A discovery that exits non-zero is refused, however many classes it printed first.</summary>
    [Fact]
    public void Discovery_ThatExitsNonZero_IsRefused_AndKeepsItsLog()
    {
        var log = Path.Combine(_scratch, "discovery.log");
        var output = Run($$"""
            function global:dotnet {
                Write-Output '  {{ClassA}}.Opens'
                Write-Output 'error: the test host crashed while listing'
                $global:LASTEXITCODE = 3
            }
            $partial = try { $null = Get-HeadlessDiscovery @('test') @('--filter-class', '*UiPerfSmokeTests') '{{log}}'; 'accepted' } catch { "refused: $($_.Exception.Message)" }
            function global:dotnet {
                Write-Output '  {{ClassA}}.Opens'
                Write-Output '  {{ClassB}}.Measures'
                $global:LASTEXITCODE = 0
            }
            $full = Get-HeadlessDiscovery @('test') @('--filter-class', '*UiPerfSmokeTests') '{{log}}'
            $partial
            "full=$(@($full.Keys | Sort-Object) -join ',')"
            """);

        var lines = Normalize(output).Split('\n');
        Assert.StartsWith("refused: Test discovery exited 3", lines[0]);
        Assert.Contains(log, lines[0]);
        Assert.Equal($"full={ClassA},{ClassB}", lines[^1]);
    }

    [Fact]
    public void Retry_AClassReportingAnotherCaseDoesNotVerifyTheMissingHarnessCase()
    {
        var output = Run($$"""
            $h = [pscustomobject]@{ Name = '{{ClassA}}.Missing'; Class = '{{ClassA}}'; IsHarness = $true }
            $retry = [pscustomobject]@{ Failures = @(); PassedTests = @('{{ClassA}}.Other'); RanClasses = @('{{ClassA}}'); ExitCode = 0; Total = 1; Failed = 0 }
            $g = Resolve-HeadlessRetry $retry @($h) @()
            $g.Unverified -join ','
            """);

        Assert.Equal($"{ClassA}.Missing", output);
    }

    /// <summary>The CI script's retry and isolated runs start with no console window of their own.</summary>
    [Fact]
    public void ChildProcesses_StartWithoutAWindow_AndTheCiScriptUsesThem()
    {
        var output = Run("""
            $psi = New-HeadlessProcessStartInfo 'dotnet' @('test', '--filter-class', 'A B', '--results-directory', 'C:\x y\') 'C:\repo'
            "$($psi.CreateNoWindow)|$($psi.UseShellExecute)|$($psi.RedirectStandardOutput)|$($psi.WorkingDirectory)|$($psi.Arguments)"
            """);

        Assert.Equal(@"True|False|True|C:\repo|test --filter-class ""A B"" --results-directory ""C:\x y\\""", output);

        var ci = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "ci", "Invoke-HeadlessUiCi.ps1"));
        Assert.DoesNotContain("ProcessStartInfo]::new()", ci, StringComparison.Ordinal);
        Assert.Contains("New-HeadlessProcessStartInfo 'dotnet'", ci, StringComparison.Ordinal);
    }

    /// <summary>Both graders go through the module, so neither can drift back to its own copy.</summary>
    [Theory]
    [InlineData("ci", "Invoke-HeadlessUiCi.ps1")]
    [InlineData("release", "New-Release.ps1")]
    public void BothGraders_GradeThroughTheModule(string folder, string script)
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", folder, script));

        Assert.Contains("Get-HeadlessIncompleteClasses $run", text, StringComparison.Ordinal);
        Assert.Contains("Resolve-HeadlessRetry $retry $harness $mustReport", text, StringComparison.Ordinal);
        Assert.Contains("Resolve-HeadlessIsolatedRun $product $isolated", text, StringComparison.Ordinal);
        Assert.Contains("-NotePropertyName ExitCode", text, StringComparison.Ordinal);
    }

    private string WriteTrx(int total, int failed)
    {
        var path = Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(path,
            $"""<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results /><ResultSummary outcome="Completed"><Counters total="{total}" executed="{total}" passed="{total - failed}" failed="{failed}" /></ResultSummary></TestRun>""");
        return path;
    }

    private static string Normalize(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string Run(string body) => HeadlessShardsModule.Run(_scratch, body);
}
