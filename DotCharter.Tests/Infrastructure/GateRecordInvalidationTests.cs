using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>A requested rerun revokes older green evidence before anything can fail or crash.</summary>
public sealed class GateRecordInvalidationTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-evidence-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Green_ThenForcedRed_ThenNormal_ReexecutesTheFailedLanesAndCannotLand(bool allRed)
    {
        var output = Run($$$"""
            Import-Module '{{{Module("agent", "MergeTrain.psm1")}}}' -Force
            $tree = 'a' * 40
            [void](Save-GateRecord -Tree $tree -Gate -Headless -ReleaseLane -Configuration Release -InputKey pin -Source first)
            Start-GateRecordRun -Trees @($tree) -Kinds @('gate', 'headless', 'releaseLane') -Configuration Release -InputKey pin
            if (-not ${{{allRed.ToString().ToLowerInvariant()}}}) {
                [void](Save-GateRecord -Tree $tree -Gate -Headless -Configuration Release -InputKey pin -Source forced)
            }
            $p = Get-MergeTrainLanePlan -WantHeadless $true -Configuration Release -InputKey pin `
                -GateRecord (Find-GateRecord -Trees @($tree) -Kind gate -Configuration Release -InputKey pin) `
                -HeadlessRecord (Find-GateRecord -Trees @($tree) -Kind headless -Configuration Release -InputKey pin) `
                -ReleaseRecord (Find-GateRecord -Trees @($tree) -Kind releaseLane -Configuration Release -InputKey pin)
            "RUN=$($p.Run -join ',')"
            "LANDINGS=$(if ($p.Run.Count -eq 0) { 1 } else { 0 })"
            """);

        Assert.Equal($"RUN={(allRed ? "gate,headless,release" : "release")}\nLANDINGS=0", Normalize(output));
    }

    [Fact]
    public void Revocation_CoversBothExportAliases_AndPreservesOtherConfigurations()
    {
        var output = Run("""
            $a = 'a' * 40; $b = 'b' * 40; $c = 'c' * 40
            foreach ($t in @($a, $b)) { [void](Save-GateRecord -Tree $t -Gate -Configuration Release -InputKey pin -Source train) }
            [void](Save-GateRecord -Tree $c -Gate -Configuration Debug -InputKey pin -Source debug)
            Start-GateRecordRun -Trees @($a, $b, $c) -Kinds @('gate') -Configuration Release -InputKey pin
            "aliases=$([bool](Find-GateRecord -Trees @($a, $b) -Kind gate -Configuration Release -InputKey pin))"
            "debug=$([bool](Find-GateRecord -Trees @($c) -Kind gate -Configuration Debug -InputKey pin))"
            "wrong-config=$([bool](Find-GateRecord -Trees @($c) -Kind gate -Configuration Release -InputKey pin))"
            """);

        Assert.Equal("aliases=False\ndebug=True\nwrong-config=False", Normalize(output));
    }

    [Fact]
    public void CultureEvidence_DoesNotCombineDifferentConfigurations()
    {
        var output = Run("""
            $t = 'a' * 40
            [void](Save-GateRecord -Tree $t -HeadlessCultures 'de-DE' -Configuration Release -InputKey pin -Source de)
            [void](Save-GateRecord -Tree $t -HeadlessCultures 'ja-JP' -Configuration Debug -InputKey pin -Source ja)
            "mixed=$([bool](Find-GateRecord -Trees @($t) -Kind headlessCultures -Cultures @('de-DE', 'ja-JP') -Configuration Debug -InputKey pin))"
            [void](Save-GateRecord -Tree $t -HeadlessCultures 'de-DE' -Configuration Debug -InputKey pin -Source de)
            "same=$([bool](Find-GateRecord -Trees @($t) -Kind headlessCultures -Cultures @('de-DE', 'ja-JP') -Configuration Debug -InputKey pin))"
            """);

        Assert.Equal("mixed=False\nsame=True", Normalize(output));
    }

    [Fact]
    public void ReleasePlan_RevokesForcedEvidenceBeforeTheTestBuildCanThrow()
    {
        var output = Run($$$"""
            Import-Module '{{{Module("release", "ReleaseGates.psm1")}}}' -Force
            $RepoRoot = '{{{TestPaths.RepoRoot()}}}'
            $scope = Get-GateRecordInputs -RepoRoot $RepoRoot -Configuration Release
            $tree = 'a' * 40
            [void](Save-GateRecord -Tree $tree -Gate -Headless -ReleaseLane -HeadlessCultures @('de-DE', 'ja-JP') @scope -Source old)
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{{Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1")}}}', [ref]$tokens, [ref]$errors)
            $fn = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-ReleaseGates' }, $true)
            Invoke-Expression $fn.Extent.Text
            function Get-CleanTreeId { 'a' * 40 }; function Get-GateRecordTrees { @('a' * 40) }
            function Build-ReleaseTestProject {
                $global:BeforeBuildCleared = -not [bool](Find-GateRecord -Trees @($tree) -Kind gate @scope)
                [void](Save-GateRecord -Tree $tree -Gate -Headless -ReleaseLane -HeadlessCultures @('de-DE', 'ja-JP') @scope -Source concurrent-green)
                throw 'build-sentinel'
            }
            $ForceGate = $true; $ForceHeadlessUi = $true; $ForceReleaseLane = $true; $SkipHeadlessUi = $false
            $HeadlessSweepCultures = @('de-DE', 'ja-JP')
            try { Invoke-ReleaseGates '{{{_scratch}}}' } catch { "ERROR=$($_.Exception.Message)" }
            "PRE=$global:BeforeBuildCleared"
            foreach ($kind in @('gate', 'headless', 'releaseLane')) { "$kind=$([bool](Find-GateRecord -Trees @($tree) -Kind $kind @scope))" }
            "cultures=$([bool](Find-GateRecord -Trees @($tree) -Kind headlessCultures -Cultures @('de-DE', 'ja-JP') @scope))"
            """);

        Assert.Contains("ERROR=build-sentinel", output, StringComparison.Ordinal);
        Assert.Contains("PRE=True", output, StringComparison.Ordinal);
        Assert.Contains("gate=False", output, StringComparison.Ordinal);
        Assert.Contains("headless=False", output, StringComparison.Ordinal);
        Assert.Contains("releaseLane=False", output, StringComparison.Ordinal);
        Assert.Contains("cultures=False", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectLocalGate_RevokesEvidenceEvenThoughItWritesNoGreenRecord()
    {
        var trx = Path.Combine(_scratch, "red.trx");
        File.WriteAllText(trx, """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="N.C.Fails" outcome="Failed" /></Results><ResultSummary><Counters total="1" executed="1" passed="0" failed="1" /></ResultSummary></TestRun>""");
        var output = Run($$$"""
            $scope = Get-GateRecordInputs -RepoRoot '{{{TestPaths.RepoRoot()}}}' -Configuration Debug
            [void](Save-GateRecord -Tree ('a' * 40) -Gate @scope -Source old)
            function global:git {
                if ($args -contains 'status') { $global:LASTEXITCODE = 0; return }
                if ($args -contains '--show-toplevel') { $global:LASTEXITCODE = 0; '{{{TestPaths.RepoRoot()}}}'; return }
                if ($args -contains 'rev-parse') { $global:LASTEXITCODE = 0; 'a' * 40; return }
                throw "Unexpected real-repo git request: $args"
            }
            function global:dotnet {
                $dir = ''; $name = ''
                for ($i = 0; $i -lt $args.Count; $i++) {
                    if ($args[$i] -eq '--results-directory') { $dir = $args[$i + 1] }
                    if ($args[$i] -eq '--report-xunit-trx-filename') { $name = $args[$i + 1] }
                }
                [IO.File]::Copy('{{{trx}}}', (Join-Path $dir $name), $true)
                $global:LASTEXITCODE = 2
            }
            & '{{{Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "Invoke-Gate.ps1")}}}' -Gate -NoBuild -Shards 1 -ResultsDir '{{{Path.GetRelativePath(TestPaths.RepoRoot(), Path.Combine(_scratch, "gate-results"))}}}'
            "EXIT=$LASTEXITCODE"
            "CACHED=$([bool](Find-GateRecord -Trees @(('a' * 40)) -Kind gate @scope))"
            """);

        Assert.Contains("EXIT=2", output, StringComparison.Ordinal);
        Assert.Contains("CACHED=False", output, StringComparison.Ordinal);
    }

    private static string Normalize(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Module(string area, string file) => Path.Combine(TestPaths.RepoRoot(), "tools", area, file);

    private string Run(string body) => HeadlessShardsModule.RunModule(_scratch,
        $"$env:DOTCHARTER_RELEASE_CACHE = '{Path.Combine(_scratch, "cache")}'\n{body}", "tools", "release", "ReleaseCache.psm1");
}
