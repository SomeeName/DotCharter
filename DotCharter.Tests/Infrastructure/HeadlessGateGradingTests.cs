using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>Executes the local gate's actual completion/recovery bodies with synthetic shard evidence.</summary>
public sealed class HeadlessGateGradingTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-gate-grade-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACrashCannotBeErasedByRecoveringOnlyTheHarnessClass(bool retryCrash)
    {
        var initial = Trx("initial", failures: retryCrash ? 2 : 1);
        var crash = Trx("crash", failures: 0);
        var retry = Trx("retry", failures: 1);
        var clean = Trx("clean", failures: 0);
        var script = Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "Invoke-Gate.ps1");
        var output = HeadlessShardsModule.Run(_scratch, $$$"""
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{{script}}}', [ref]$tokens, [ref]$errors)
            foreach ($name in @('Complete-HeadlessShardedRun', 'Invoke-HeadlessRaceReruns')) {
                $fn = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
                Invoke-Expression $fn.Extent.Text
            }
            $repoRoot = '{{{TestPaths.RepoRoot()}}}'; $RaceReruns = 2; $UpdateShardWeights = $false; $MaxFailures = 3
            $global:Attempts = 0; $global:Recoveries = 0
            function Show-ShardLines { }; function Show-TrxResults { 2 }
            function Save-HeadlessFailureEvidence { '' }; function Read-HeadlessShardWeights { @{} }
            function Merge-HeadlessTrx { '{{{initial}}}' }
            function Invoke-HeadlessShards {
                $global:Attempts++
                if ($global:Attempts -eq 1) {
                    [pscustomobject]@{ TrxPath = '{{{retry}}}'; ExitCode = 7; Classes = @('DotCharter.Tests.Perf.AlphaUiPerfSmokeTests', 'DotCharter.Tests.Perf.BravoUiPerfSmokeTests'); Elapsed = [TimeSpan]::Zero; LogPath = 'crashed' }
                } else {
                    [pscustomobject]@{ TrxPath = '{{{clean}}}'; ExitCode = 0; Classes = @('DotCharter.Tests.Perf.AlphaUiPerfSmokeTests'); Elapsed = [TimeSpan]::Zero; LogPath = 'clean' }
                }
            }
            $discovered = @{ 'DotCharter.Tests.Perf.AlphaUiPerfSmokeTests' = 1; 'DotCharter.Tests.Perf.BravoUiPerfSmokeTests' = 1 }
            if (${{{retryCrash.ToString().ToLowerInvariant()}}}) {
                $script:HeadlessEvidenceDir = $null
                $exit = Invoke-HeadlessRaceReruns 'headless' '{{{initial}}}' @() $discovered '' '{{{_scratch}}}' @('test') 2
                "RESULT exit=$exit;attempts=$global:Attempts"
            } else {
                function Invoke-HeadlessRaceReruns { $global:Recoveries++; 0 }
                $runs = @(
                    [pscustomobject]@{ Index = 1; Classes = @('DotCharter.Tests.Perf.AlphaUiPerfSmokeTests'); ExitCode = 2; TrxPath = '{{{initial}}}'; LogPath = 'harness'; PeakSumBytes = 0 },
                    [pscustomobject]@{ Index = 2; Classes = @('DotCharter.Tests.Perf.BravoUiPerfSmokeTests'); ExitCode = 7; TrxPath = '{{{crash}}}'; LogPath = 'crash'; PeakSumBytes = 0 })
                $run = [pscustomobject]@{ Label = 'headless'; Runs = $runs; Results = '{{{_scratch}}}'; Discovered = $discovered; Started = Get-Date; WeightsPath = ''; Base = @('test'); DiscoveryLog = 'discovery' }
                $exit = Complete-HeadlessShardedRun $run
                "RESULT exit=$exit;recoveries=$global:Recoveries"
            }
            """);

        Assert.DoesNotContain("RESULT exit=0", output, StringComparison.Ordinal);
        Assert.Contains(retryCrash ? ";attempts=1" : ";recoveries=0", output, StringComparison.Ordinal);
    }

    private string Trx(string name, int failures)
    {
        var alpha = "DotCharter.Tests.Perf.AlphaUiPerfSmokeTests.Opens";
        var bravo = "DotCharter.Tests.Perf.BravoUiPerfSmokeTests.Opens";
        var names = failures == 2 ? new[] { alpha, bravo } : [alpha];
        var results = string.Concat(names.Select((n, i) => failures > i
            ? $"<UnitTestResult testName=\"{n}\" outcome=\"Failed\"><Output><ErrorInfo><Message>System.InvalidProgramException : You've caused dispatcher loop</Message><StackTrace>at Avalonia.Threading.Dispatcher.ResetForUnitTests()</StackTrace></ErrorInfo></Output></UnitTestResult>"
            : $"<UnitTestResult testName=\"{n}\" outcome=\"Passed\" />"));
        if (failures < 2) results += $"<UnitTestResult testName=\"{bravo}\" outcome=\"Passed\" />";
        var path = Path.Combine(_scratch, name + ".trx");
        File.WriteAllText(path, $"""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results><ResultSummary><Counters total="2" executed="2" passed="{2 - failures}" failed="{failures}" /></ResultSummary></TestRun>""");
        return path;
    }
}
