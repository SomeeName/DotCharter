using static DotCharter.Tests.Infrastructure.PublicSnapshotExportTests;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/release/ReleaseGates.psm1</c>, the release run's pure gate decisions: which of
///     the four gates (default gate, headless en-US pass, localized layout sweeps, release
///     lane) reuse a green record of this exact tree and which run, whether DotCharter.Tests
///     is built at all, and how the localized sweeps that run side by side share the shard
///     budget. <c>ReleaseCacheTests</c> keeps <c>New-Release.ps1</c> calling it.
/// </summary>
public sealed class ReleaseGatesTests
{
    private static string Module => Path.Combine(TestPaths.RepoRoot(), "tools", "release", "ReleaseGates.psm1");

    [Fact]
    public void GatePlan_ReusesOnlyRecordsOfACleanTree_AndForceAndSkipWin()
    {
        var run = RunWindowsPowerShell($$"""
            $ErrorActionPreference = 'Stop'
            Import-Module {{Quote(Module)}} -Force
            $r = [pscustomobject]@{ Tree = ('a' * 40); Source = 'train' }
            function Show([string]$Name, $Plan) { "$Name=$(@($Plan.Steps | ForEach-Object { "$($_.Name):$($_.Action)" }) -join ','),build=$($Plan.NeedsTestBuild)" }
            Show 'all-recorded' (Get-ReleaseGatePlan -CleanTree $true -GateRecord $r -HeadlessRecord $r -CulturesRecord $r -ReleaseLaneRecord $r)
            Show 'train-tree' (Get-ReleaseGatePlan -CleanTree $true -GateRecord $r -ReleaseLaneRecord $r)
            Show 'dirty' (Get-ReleaseGatePlan -CleanTree $false -GateRecord $r -HeadlessRecord $r -CulturesRecord $r -ReleaseLaneRecord $r)
            Show 'force-headless' (Get-ReleaseGatePlan -CleanTree $true -GateRecord $r -HeadlessRecord $r -CulturesRecord $r -ReleaseLaneRecord $r -ForceHeadless)
            Show 'force-gate' (Get-ReleaseGatePlan -CleanTree $true -GateRecord $r -HeadlessRecord $r -CulturesRecord $r -ReleaseLaneRecord $r -ForceGate)
            Show 'force-lane' (Get-ReleaseGatePlan -CleanTree $true -GateRecord $r -HeadlessRecord $r -CulturesRecord $r -ReleaseLaneRecord $r -ForceReleaseLane)
            Show 'skip-headless' (Get-ReleaseGatePlan -CleanTree $true -GateRecord $r -ReleaseLaneRecord $r -SkipHeadless)
            "record=$((Get-ReleaseGatePlan -CleanTree $true -GateRecord $r).Steps[0].Record.Source)"
            """);
        Assert.True(run.ExitCode == 0, run.Output);
        var lines = Lines(run.Output);

        // Exactly the tree the last train AND an earlier release run tested: nothing runs, nothing builds.
        Assert.Contains("all-recorded=gate:reuse,headless:reuse,headlessCultures:reuse,releaseLane:reuse,build=False", lines);
        // The v1.4.0 case: the train recorded the gate + lane only; the headless suite runs.
        Assert.Contains("train-tree=gate:reuse,headless:run,headlessCultures:run,releaseLane:reuse,build=True", lines);
        // A tree that is not clean HEAD is not what any record tested.
        Assert.Contains("dirty=gate:run,headless:run,headlessCultures:run,releaseLane:run,build=True", lines);
        Assert.Contains("force-headless=gate:reuse,headless:run,headlessCultures:run,releaseLane:reuse,build=True", lines);
        Assert.Contains("force-gate=gate:run,headless:reuse,headlessCultures:reuse,releaseLane:reuse,build=True", lines);
        Assert.Contains("force-lane=gate:reuse,headless:reuse,headlessCultures:reuse,releaseLane:run,build=True", lines);
        // -SkipHeadlessUi skips the suite, never the lane or the gate.
        Assert.Contains("skip-headless=gate:reuse,headless:skip,headlessCultures:skip,releaseLane:reuse,build=False", lines);
        Assert.Contains("record=train", lines);
    }

    [Fact]
    public void CultureShardSplit_RunsEveryCultureInFull_WithinTwiceTheShardBudget()
    {
        var run = RunWindowsPowerShell($$"""
            $ErrorActionPreference = 'Stop'
            Import-Module {{Quote(Module)}} -Force
            function Show([int]$Shards, [string[]]$Cultures) { "$Shards=$(@(Get-CultureShardSplit -Shards $Shards -Cultures $Cultures | ForEach-Object { "$($_.Culture):$($_.Shards)" }) -join ',')" }
            Show 4 @('de-DE', 'ja-JP')
            Show 8 @('de-DE', 'ja-JP')
            Show 4 @('de-DE', 'ja-JP', 'fr-FR')
            Show 1 @('de-DE', 'ja-JP', 'fr-FR')
            Show 3 @('de-DE')
            "capped=$(@(Get-CultureShardSplit -Shards 4 -Cultures @('de-DE', 'ja-JP') -MaxTestHosts 5 | ForEach-Object { "$($_.Culture):$($_.Shards)" }) -join ',')"
            """);
        Assert.True(run.ExitCode == 0, run.Output);
        var lines = Lines(run.Output);

        // Both cultures in full: the measured win (5:50 one after the other, 3:19 side by side).
        Assert.Contains("4=de-DE:4,ja-JP:4", lines);
        Assert.Contains("8=de-DE:8,ja-JP:8", lines);
        // Never more than 2 x -Shards testhosts by default: a third culture shares the budget.
        Assert.Contains("4=de-DE:3,ja-JP:3,fr-FR:2", lines);
        Assert.Contains("1=de-DE:1,ja-JP:1,fr-FR:1", lines);
        Assert.Contains("3=de-DE:3", lines);
        Assert.Contains("capped=de-DE:3,ja-JP:2", lines);
    }

    /// <summary>
    ///     The release pins itself through the shared CPU plan (CPUs 8-15 unless -AllCores),
    ///     takes its shard count from it, and hands -AllCores on to every gate it starts.
    /// </summary>
    [Fact]
    public void NewRelease_PinsThroughTheSharedCpuPlan_AndPassesItToEveryGate()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));

        Assert.Matches(@"(?m)^\$CpuPlan = Set-DevCpuPlan -AllCores:\$AllCores\s*$", script);
        Assert.Contains("if ($HeadlessShards -le 0) { $HeadlessShards = $CpuPlan.Shards }", script, StringComparison.Ordinal);
        Assert.Contains("if ($AllCores) { $GateCpuSwitch = @('-AllCores') }", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessorAffinity", script, StringComparison.Ordinal);
        var gateCalls = System.Text.RegularExpressions.Regex.Matches(script, @"Invoke-Gate\.ps1'\), '-[^\r\n]*");
        Assert.Equal(2, gateCalls.Count);
        Assert.All(gateCalls, c => Assert.EndsWith("+ $GateCpuSwitch", c.Value.TrimEnd(), StringComparison.Ordinal));
    }

    /// <summary>
    ///     #614: a red en-US pass stops the release before any localized sweep starts; #611: a
    ///     pass that left a case unverified (the harness fault hit it again) is never recorded green.
    /// </summary>
    [Fact]
    public void HeadlessGate_ARedMainPassBlocksTheSweeps_AndAnUnverifiedCaseBlocksTheRecord()
    {
        var run = RunWindowsPowerShell($$"""
            $ErrorActionPreference = 'Stop'
            Import-Module {{Quote(Module)}} -Force
            $runStep = [pscustomobject]@{ Name = 'headless'; Action = 'run' }
            $reuseStep = [pscustomobject]@{ Name = 'headlessCultures'; Action = 'reuse' }
            "red-main=$(Get-HeadlessSweepAction -SweepStep $runStep -MainFailures 2)"
            "red-main-reuse=$(Get-HeadlessSweepAction -SweepStep $reuseStep -MainFailures 1)"
            "green-main=$(Get-HeadlessSweepAction -SweepStep $runStep -MainFailures 0)"
            "green-main-reuse=$(Get-HeadlessSweepAction -SweepStep $reuseStep -MainFailures 0)"
            "record-clean=$(Test-HeadlessGateRecordable -Step $runStep -Unverified @())"
            "record-unverified=$(Test-HeadlessGateRecordable -Step $runStep -Unverified @('A.B.Case'))"
            "record-reused=$(Test-HeadlessGateRecordable -Step $reuseStep -Unverified @())"
            """);
        Assert.True(run.ExitCode == 0, run.Output);
        var lines = Lines(run.Output);

        Assert.Contains("red-main=blocked", lines);
        Assert.Contains("red-main-reuse=blocked", lines);
        Assert.Contains("green-main=run", lines);
        Assert.Contains("green-main-reuse=reuse", lines);
        Assert.Contains("record-clean=True", lines);
        Assert.Contains("record-unverified=False", lines);
        Assert.Contains("record-reused=False", lines);


    }

    /// <summary>Executes the release gate's real function bodies, with process launch and cache writes replaced by fakes.</summary>
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 2)]
    public void HeadlessGate_StopsBeforeSweeps_AndDoesNotCacheUnverifiedRuns(bool red, bool unverified, int expectedRecords)
    {
        var run = RunWindowsPowerShell($$"""
            Import-Module {{Quote(Module)}} -Force
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile({{Quote(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"))}}, [ref]$tokens, [ref]$errors)
            foreach ($name in @('Invoke-HeadlessUiGate', 'Stop-HeadlessUiGate')) {
                $fn = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
                Invoke-Expression $fn.Extent.Text
            }
            $global:Sweeps = 0; $global:Records = 0; $global:Stopped = $false
            $global:IsRed = ${{red.ToString().ToLowerInvariant()}}
            $global:IsUnverified = ${{unverified.ToString().ToLowerInvariant()}}
            $HeadlessSweepCultures = @('de-DE', 'ja-JP'); $HeadlessShards = 1
            function Write-Step { }; function Write-Ok { }; function Write-Warn2 { }; function Write-HeadlessFailure { }
            function Invoke-HeadlessUiPass {
                $p = @(); $u = @()
                if ($global:IsRed) { $p = @([pscustomobject]@{ Name = 'A.Case'; Message = 'assertion failed' }) }
                if ($global:IsUnverified) { $u = @('A.Case') }
                [pscustomobject]@{ Run = [pscustomobject]@{ Passed = 1; Skipped = 0; LogPath = 'kept.log' }; Product = $p; Unverified = $u }
            }
            function Start-HeadlessUiShardedRun { $global:Sweeps++; [pscustomobject]@{ Runs = @(); Label = 'sweep'; Culture = 'de-DE' } }
            function Complete-HeadlessUiShardedRun { [pscustomobject]@{ Passed = 1 } }
            function Wait-TestShards { }; function Save-GateRecord { $global:Records++; 'fake-record' }
            function Get-GateRecordInputs { @{ Configuration = 'Release'; InputKey = 'fixture-pin' } }
            function git { 'fake-commit' }
            $step = [pscustomobject]@{ Action = 'run' }
            try { Invoke-HeadlessUiGate -ResultsDir $env:TEMP -MainStep $step -SweepStep $step -Tree ('a' * 40) } catch { $global:Stopped = $true }
            "RESULT stopped=$global:Stopped;sweeps=$global:Sweeps;records=$global:Records"
            """);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains($"RESULT stopped={red};sweeps={(red ? 0 : 2)};records={expectedRecords}", run.Output, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) RunWindowsPowerShell(string body)
    {
        var scratch = Directory.CreateTempSubdirectory("dc-release-gates-ps5-").FullName;
        try
        {
            return (0, HeadlessShardsModule.RunModule(scratch, body, "tools", "release", "ReleaseGates.psm1"));
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
}
