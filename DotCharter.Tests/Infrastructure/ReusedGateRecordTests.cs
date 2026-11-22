using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>Reuse is a read of completed evidence, never permission to recreate it.</summary>
public sealed class ReusedGateRecordTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-reused-record-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData("gone")]
    [InlineData("configuration")]
    [InlineData("inputs")]
    [InlineData("tree")]
    [InlineData("cultures")]
    public void Revalidation_RequiresTheOriginalTreeAndEveryLaneInput(string change)
    {
        var output = Run($$$"""
            $scope = @{ Configuration = 'Release'; InputKey = 'pin' }
            $kind = if ('{{{change}}}' -eq 'cultures') { 'headlessCultures' } else { 'gate' }
            $a = @{ Tree = ('a' * 40); Source = 'old' }
            if ($kind -eq 'gate') { $a.Gate = $true } else { $a.HeadlessCultures = @('de-DE', 'ja-JP') }
            [void](Save-GateRecord @a @scope)
            $record = Find-GateRecord -Trees @(('a' * 40)) -Kind $kind -Cultures @('de-DE', 'ja-JP') @scope
            $reused = @{}; $reused[$kind] = $record
            switch ('{{{change}}}') {
                'gone' { Start-GateRecordRun -Trees @(('a' * 40)) -Kinds $kind @scope }
                'configuration' { [void](Save-GateRecord -Tree ('a' * 40) -Gate -Configuration Debug -InputKey pin -Source other) }
                'inputs' { [void](Save-GateRecord -Tree ('a' * 40) -Gate -Configuration Release -InputKey other -Source other) }
                'tree' {
                    Start-GateRecordRun -Trees @(('a' * 40)) -Kinds gate @scope
                    [void](Save-GateRecord -Tree ('b' * 40) -Gate @scope -Source alias)
                }
                'cultures' {
                    Start-GateRecordRun -Trees @(('a' * 40)) -Kinds $kind @scope
                    [void](Save-GateRecord -Tree ('a' * 40) -HeadlessCultures 'de-DE' @scope -Source partial)
                }
            }
            $lost = @()
            if (Get-Command Get-LostGateRecordKinds -ErrorAction SilentlyContinue) { $lost = @(Get-LostGateRecordKinds -Records $reused -Cultures @('de-DE', 'ja-JP') @scope) }
            "LOST=$($lost -join ',')"
            """);

        Assert.Contains(change == "cultures" ? "LOST=headlessCultures" : "LOST=gate", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewCompletedGreenWithTheSameTreeAndInputs_RemainsValid()
    {
        var output = Run("""
            $scope = @{ Configuration = 'Release'; InputKey = 'pin' }
            [void](Save-GateRecord -Tree ('a' * 40) -Gate @scope -Source first)
            $reused = @{ gate = (Find-GateRecord -Trees @(('a' * 40)) -Kind gate @scope) }
            [void](Save-GateRecord -Tree ('a' * 40) -Gate @scope -Source later)
            $lost = @()
            if (Get-Command Get-LostGateRecordKinds -ErrorAction SilentlyContinue) { $lost = @(Get-LostGateRecordKinds -Records $reused @scope) }
            "LOST=$($lost.Count)"
            """);

        Assert.Contains("LOST=0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealReleaseGatePlan_RejectsAReusedGateRevokedDuringItsOwnLane()
    {
        var output = Run($$$"""
            $RepoRoot = '{{{TestPaths.RepoRoot()}}}'
            Import-Module (Join-Path $RepoRoot 'tools/release/ReleaseGates.psm1') -Force
            $scope = Get-GateRecordInputs -RepoRoot $RepoRoot -Configuration Release
            [void](Save-GateRecord -Tree ('a' * 40) -Gate @scope -Source first)
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/release/New-Release.ps1'), [ref]$tokens, [ref]$errors)
            $fn = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-ReleaseGates' }, $true)
            Invoke-Expression $fn.Extent.Text
            function Get-CleanTreeId { 'a' * 40 }; function Get-GateRecordTrees { @('a' * 40) }
            function Build-ReleaseTestProject { }; function Write-Ok { }; function Write-Warn2 { }
            function Invoke-DefaultGate { }
            function Invoke-ReleaseLaneGate {
                Start-GateRecordRun -Trees @(('a' * 40)) -Kinds gate @scope
                [void](Save-GateRecord -Tree ('a' * 40) -ReleaseLane @scope -Source own)
                $script:ReleaseAcceptedKinds += 'releaseLane'
            }
            $HeadlessSweepCultures = @('de-DE', 'ja-JP'); $SkipHeadlessUi = $true
            $outcome = try { Invoke-ReleaseGates '{{{_scratch}}}'; 'accepted' } catch { $_.Exception.Message }
            "OUTCOME=$outcome"
            "GATE=$([bool](Find-GateRecord -Trees @(('a' * 40)) -Kind gate @scope))"
            """);

        Assert.Contains("Reused gate evidence lost", output, StringComparison.Ordinal);
        Assert.Contains("Re-run", output, StringComparison.Ordinal);
        Assert.Contains("GATE=False", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("preflight", 0)]
    [InlineData("between", 1)]
    [InlineData("valid", 2)]
    public void Export_NetworkPlansPrecedeTheFinalCheck_BeforeEachRefUpdate(string revokeAt, int pushes)
    {
        var output = Run($$$"""
            $scope = @{ Configuration = 'Release'; InputKey = 'pin' }
            [void](Save-GateRecord -Tree ('a' * 40) -Gate @scope -Source completed)
            $reusedRecords = @{ gate = (Find-GateRecord -Trees @(('a' * 40)) -Kind gate @scope) }
            $global:Pushes = 0; $global:Order = @()
            $global:OriginalCheck = (Get-Command Assert-GateRecordsCurrent).ScriptBlock
            function Assert-GateRecordsCurrent {
                param($Records, $Configuration, $InputKey)
                $global:Order += 'check'
                & $global:OriginalCheck -Records $Records -Configuration $Configuration -InputKey $InputKey
            }
            function Get-PushPlans {
                $global:Order += 'preflight'
                if ('{{{revokeAt}}}' -eq 'preflight') { Start-GateRecordRun -Trees @(('a' * 40)) -Kinds gate @scope }
                foreach ($n in 1..2) { [pscustomobject]@{ Label = "fixture-$n"; Repo = '{{{_scratch}}}'; Url = 'fixture'; Commit = ('a' * 40); Refspecs = @('main'); Problems = @() } }
            }
            function Git {
                $global:Order += 'push'; $global:Pushes++
                if ('{{{revokeAt}}}' -eq 'between' -and $global:Pushes -eq 1) { Start-GateRecordRun -Trees @(('a' * 40)) -Kinds gate @scope }
            }
            function Stop-Push { throw 'unexpected preflight problem' }
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{{Path.Combine(TestPaths.RepoRoot(), "tools", "release", "Export-PublicSnapshot.ps1")}}}', [ref]$tokens, [ref]$errors)
            $branch = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$Push' }, $true))[-1]
            $Push = $true
            $outcome = try { Invoke-Expression $branch.Extent.Text; 'landed' } catch { $_.Exception.Message }
            "OUTCOME=$outcome"
            "PUSHES=$global:Pushes"
            "ORDER=$($global:Order -join '|')"
            """);

        Assert.Contains($"PUSHES={pushes}", output, StringComparison.Ordinal);
        if (revokeAt == "valid")
        {
            Assert.Contains("OUTCOME=landed", output, StringComparison.Ordinal);
            Assert.Contains("ORDER=preflight|check|push|check|push", output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Reused gate evidence lost", output, StringComparison.Ordinal);
        }
    }

    private string Run(string body) => HeadlessShardsModule.RunModule(_scratch,
        $"$env:DOTCHARTER_RELEASE_CACHE = '{Path.Combine(_scratch, "cache")}'\n{body}", "tools", "release", "ReleaseCache.psm1");
}
