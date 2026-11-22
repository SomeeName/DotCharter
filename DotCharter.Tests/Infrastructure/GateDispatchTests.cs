using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>The actual gate dispatch revokes every full lane, and never plans foreign weighted classes.</summary>
public sealed class GateDispatchTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-gate-dispatch-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData("Gate", "gate")]
    [InlineData("Headless", "headless")]
    [InlineData("ReleaseLane", "releaseLane")]
    public void AFullLaneWithATrailingFilter_RevokesBeforeAndAfterRed(string lane, string kind)
    {
        var output = RunDispatch($"-{lane} -FilterClass '*OnlyThis'", kind, concurrentGreen: true);

        Assert.Contains("PRE=True", output, StringComparison.Ordinal);
        Assert.Contains("EXIT=2", output, StringComparison.Ordinal);
        Assert.Contains("CACHED=False", output, StringComparison.Ordinal);
        Assert.DoesNotContain("FILTERED", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-FilterClass '*OnlyThis'")]
    [InlineData("-Public -FilterClass '*OnlyThis'")]
    public void AFilteredOnlyRun_LeavesFullLaneEvidenceAlone(string args)
    {
        var output = RunDispatch(args, "gate", concurrentGreen: false);

        Assert.Contains("EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("CACHED=True", output, StringComparison.Ordinal);
        Assert.Contains("FILTERED", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherProject_UsesOneUnshardedGate_AndAnUnexpectedEmptyRunStaysRed()
    {
        var output = HeadlessShardsModule.RunModule(_scratch, $$$"""
            Import-Module '{{{Path.Combine(TestPaths.RepoRoot(), "tools", "ci", "HeadlessShards.psm1")}}}' -Force
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{{Gate}}}', [ref]$tokens, [ref]$errors)
            foreach ($fn in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) { Invoke-Expression $fn.Extent.Text }
            function Import-Module { }
            $repoRoot = '{{{TestPaths.RepoRoot()}}}'; $Project = 'Second.Tests/Second.Tests.csproj'
            $script:GateWeightsPath = Join-Path $repoRoot 'tools/ci/gate-shard-weights.json'
            $ReleaseLanePath = Join-Path $repoRoot 'tools/ci/release-lane.json'
            $GateExclusion = '*UiPerfSmokeTests'; $Shards = 4; $logDir = '{{{_scratch}}}'; $testBase = @('test', '--project', $Project)
            $UpdateShardWeights = $true; $MaxFailures = 2; $global:WeightWrites = 0
            function Write-GateShardWeights { $global:WeightWrites++ }
            function Start-TestShards {
                param($Plan)
                $global:CapturedPlan = @($Plan)
                foreach ($p in $Plan) {
                    $empty = @($p.CommandArgs) -contains '--filter-class'
                    $path = Join-Path '{{{_scratch}}}' "$($p.Index).trx"
                    $total = if ($empty) { 0 } else { 1 }
                    [IO.File]::WriteAllText($path, "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Results>$(if ($total) { '<UnitTestResult testName="Second.Tests.Check.Works" outcome="Passed" />' })</Results><ResultSummary><Counters total='$total' executed='$total' passed='$total' failed='0' /></ResultSummary></TestRun>")
                    [pscustomobject]@{ Index = $p.Index; Classes = @($p.Classes); Weight = 0; ExitCode = $(if ($empty) { 8 } else { 0 }); TrxPath = $path; LogPath = $path; Elapsed = [TimeSpan]::Zero; PeakBytes = 0; PeakSumBytes = 0 }
                }
            }
            function Show-ShardLines { }; function Show-ClassDurations { $script:LastDurations = @() }
            $run = Start-GateShardedRun 'other'
            "SHARDS=$($global:CapturedPlan.Count)"
            "EXIT=$(Complete-GateShardedRun $run)"
            "FOREIGN=$([bool]@($global:CapturedPlan | Where-Object { $_.CommandArgs -contains '--filter-class' }).Count)"
            function Invoke-TestRun { 8 }
            $Shards = 1
            "EMPTY=$(Invoke-GateRun 'empty')"
            "WEIGHT_WRITES=$global:WeightWrites"
            """, "tools", "ci", "GateShards.psm1");

        Assert.Contains("SHARDS=1", output, StringComparison.Ordinal);
        Assert.Contains("EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("FOREIGN=False", output, StringComparison.Ordinal);
        Assert.Contains("EMPTY=8", output, StringComparison.Ordinal);
        Assert.Contains("WEIGHT_WRITES=0", output, StringComparison.Ordinal);
    }

    private string RunDispatch(string args, string kind, bool concurrentGreen)
    {
        var fixture = Path.Combine(_scratch, "repo");
        return HeadlessShardsModule.RunModule(_scratch, $$$"""
            $fixture = '{{{fixture}}}'; $source = '{{{TestPaths.RepoRoot()}}}'
            foreach ($file in @('tools/agent/DevBuild.psm1', 'tools/ci/GateShards.psm1', 'tools/ci/HeadlessShards.psm1', 'tools/ci/release-lane.json', 'tools/release/ReleaseCache.psm1')) {
                $dest = Join-Path $fixture $file; [void][IO.Directory]::CreateDirectory((Split-Path $dest -Parent)); [IO.File]::Copy((Join-Path $source $file), $dest, $true)
            }
            $env:DOTCHARTER_RELEASE_CACHE = '{{{Path.Combine(_scratch, "cache")}}}'
            Import-Module (Join-Path $fixture 'tools/release/ReleaseCache.psm1') -Force
            $global:DispatchScope = Get-GateRecordInputs -RepoRoot $fixture -Configuration Release
            $a = @{ Tree = ('a' * 40); Source = 'old'; Configuration = 'Release'; InputKey = $global:DispatchScope.InputKey }
            $a['{{{(kind == "releaseLane" ? "ReleaseLane" : kind)}}}'] = $true
            [void](Save-GateRecord @a)
            $global:ConcurrentGreen = ${{{concurrentGreen.ToString().ToLowerInvariant()}}}
            function global:Full-Dispatch {
                $found = Find-GateRecord -Trees @(('a' * 40)) -Kind '{{{kind}}}' @global:DispatchScope
                "PRE=$(-not [bool]$found)" | Write-Host
                if ($global:ConcurrentGreen) { [void](Save-GateRecord @a) }
                2
            }
            function global:git { $global:LASTEXITCODE = 0; if ($args -contains 'rev-parse') { 'a' * 40 } }
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{{Gate}}}', [ref]$tokens, [ref]$errors)
            $text = [IO.File]::ReadAllText('{{{Gate}}}')
            $bodies = @{
                'Invoke-Build' = 'function Invoke-Build { 0 }'
                'Invoke-GateRun' = 'function Invoke-GateRun { Full-Dispatch }'
                'Invoke-ReleaseLaneRun' = 'function Invoke-ReleaseLaneRun { Full-Dispatch }'
                'Invoke-HeadlessShardedRun' = 'function Invoke-HeadlessShardedRun { Full-Dispatch }'
                'Invoke-TestRun' = 'function Invoke-TestRun { Write-Host FILTERED; 0 }'
            }
            foreach ($fn in @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) | Sort-Object { $_.Extent.StartOffset } -Descending)) {
                if ($bodies.ContainsKey($fn.Name)) { $text = $text.Remove($fn.Extent.StartOffset, $fn.Extent.EndOffset - $fn.Extent.StartOffset).Insert($fn.Extent.StartOffset, $bodies[$fn.Name]) }
            }
            $path = Join-Path $fixture 'tools/agent/Invoke-Gate.ps1'
            [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($true)))
            & $path {{{args}}} -NoBuild -Sequential -Configuration Release -ResultsDir 'results'
            "EXIT=$LASTEXITCODE"
            "CACHED=$([bool](Find-GateRecord -Trees @(('a' * 40)) -Kind '{{{kind}}}' @global:DispatchScope))"
            """, "tools", "release", "ReleaseCache.psm1");
    }

    private static string Gate => Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "Invoke-Gate.ps1");
}
