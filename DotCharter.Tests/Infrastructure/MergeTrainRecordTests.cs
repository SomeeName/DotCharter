using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>The actual train must revoke forced evidence and rerun its failed lanes.</summary>
public sealed class MergeTrainRecordTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-train-record-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualTrain_GreenForcedRedNormal_RevokesBeforeTheChild_RerunsAndNeverLands(bool allRed)
    {
        var output = RunTrain(allRed);

        Assert.Contains("FIRST=0", output, StringComparison.Ordinal);
        Assert.Contains("FORCED=1", output, StringComparison.Ordinal);
        Assert.Contains("NORMAL=1", output, StringComparison.Ordinal);
        Assert.Contains("PRE=True", output, StringComparison.Ordinal);
        Assert.Contains(allRed ? "3:gate,release" : "3:release", output, StringComparison.Ordinal);
        Assert.Contains("LANDINGS=0", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ActualTrain_ARevokedReusedGate_IsNeverResavedOrLanded(bool ownLaneGreen, bool revokeAfterSave)
    {
        var output = RunTrain(false, loseReused: true, ownLaneGreen, revokeAfterSave);

        Assert.Contains("NORMAL=1", output, StringComparison.Ordinal);
        Assert.Contains("LANDINGS=0", output, StringComparison.Ordinal);
        Assert.Contains("GATE=False", output, StringComparison.Ordinal);
        Assert.Contains("Reused gate evidence lost", output, StringComparison.Ordinal);
        Assert.Contains("Re-run", output, StringComparison.Ordinal);
        Assert.Contains("NEXT=gate", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrainThatReusesEveryLane_LeavesTheEvidenceUnchanged()
    {
        var output = RunTrain(false, reuseAll: true);

        Assert.Contains("SAME=True", output, StringComparison.Ordinal);
        Assert.Contains("CALLS=1:gate,release", output, StringComparison.Ordinal);
        Assert.DoesNotContain("|2:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("|3:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrainWithEveryLaneReused_StillLandsWithMerge()
    {
        var output = RunTrain(false, reuseAll: true, mergeReused: true);

        Assert.Contains("NORMAL=0", output, StringComparison.Ordinal);
        Assert.Contains("LANDINGS=1", output, StringComparison.Ordinal);
        Assert.Contains("SAME=True", output, StringComparison.Ordinal);
        Assert.DoesNotContain("|3:", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("head")]
    public void RevocationDuringNetworkPreflight_IsCheckedBeforeAnyPush(string preflight)
    {
        var output = RunTrain(false, reuseAll: true, mergeReused: true, preflight: preflight);

        Assert.Contains("NORMAL=1", output, StringComparison.Ordinal);
        Assert.Contains("LANDINGS=0", output, StringComparison.Ordinal);
        Assert.Contains("Reused gate evidence lost", output, StringComparison.Ordinal);
        Assert.Contains("Re-run", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("delay", 0, true)]
    [InlineData("open", 1, true)]
    [InlineData("delay", 0, false)]
    [InlineData("open", 1, false)]
    public void ActualTrain_Confirmation_KeepsTheLandedEvidenceAndReleasesItsLock(string confirmation, int code, bool reuseAll)
    {
        var output = RunTrain(false, ownLaneGreen: true, reuseAll: reuseAll, mergeReused: true, confirmation: confirmation);

        Assert.Contains($"NORMAL={code}", output, StringComparison.Ordinal);
        Assert.Contains("LANDINGS=1", output, StringComparison.Ordinal);
        Assert.Contains("GATE=True", output, StringComparison.Ordinal);
        Assert.Contains("RELEASE=True", output, StringComparison.Ordinal);
        if (reuseAll)
            Assert.Contains("SAME=True", output, StringComparison.Ordinal);
        Assert.Contains("LOCK=False", output, StringComparison.Ordinal);
        Assert.Contains("RELOCK=True", output, StringComparison.Ordinal);
        Assert.Contains("WORKTREE=True", output, StringComparison.Ordinal);
        Assert.Contains(code == 0 ? "COMMENTS=1" : "COMMENTS=0", output, StringComparison.Ordinal);
        if (code != 0)
        {
            Assert.Contains("IS on origin/main", output, StringComparison.Ordinal);
            Assert.Contains("do not re-run", output, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string RunTrain(bool allRed, bool loseReused = false, bool ownLaneGreen = false, bool revokeAfterSave = false, bool reuseAll = false, bool mergeReused = false, string preflight = "", string confirmation = "")
    {
        var output = HeadlessShardsModule.RunModule(_scratch, $$$"""
            $global:Fixture = '{{{_scratch}}}'
            $global:Main = Join-Path $global:Fixture 'repo'
            $global:Train = Join-Path $global:Main '.claude/worktrees/train'
            $source = '{{{TestPaths.RepoRoot()}}}'
            foreach ($file in @('tools/agent/Merge-Train.ps1', 'tools/agent/MergeTrain.psm1', '.claude/skills/dotcharter-worktree/scripts/AgentWorktree.psm1', 'tools/setup/NativeRuntime.psm1', 'tools/agent/DevBuild.psm1', 'tools/release/ReleaseCache.psm1', 'tools/ci/release-lane.json')) {
                $dest = Join-Path $global:Main $file
                [void][IO.Directory]::CreateDirectory((Split-Path $dest -Parent)); [IO.File]::Copy((Join-Path $source $file), $dest, $true)
                $copy = Join-Path $global:Train $file
                [void][IO.Directory]::CreateDirectory((Split-Path $copy -Parent)); [IO.File]::Copy($dest, $copy, $true)
            }
            $native = Join-Path $global:Train 'JuceSharp/src/JuceSharp/runtimes/win-x64/native'
            [void][IO.Directory]::CreateDirectory($native)
            Import-Module (Join-Path $global:Train 'tools/setup/NativeRuntime.psm1') -Force
            $files = @{}
            foreach ($name in @('juce_sharp.dll', 'juce_vst_host.exe')) {
                [IO.File]::WriteAllText((Join-Path $native $name), "fixture $name")
                $files[$name] = Get-FileSha256 (Join-Path $native $name)
            }
            $manifest = @{ repository = 'SomeeName/JuceSharp'; release = 'native-prebuilt'; runtimes = @(@{ commit = ('c' * 40); files = $files }) }
            [IO.File]::WriteAllText((Join-Path $global:Train 'tools/setup/native-runtime.json'), ($manifest | ConvertTo-Json -Depth 5))
            [void][IO.Directory]::CreateDirectory((Join-Path $global:Main '.git'))
            [void][IO.Directory]::CreateDirectory((Join-Path $global:Train '.git'))
            [IO.File]::WriteAllText((Join-Path $global:Train '.git/dotcharter-train-juce'), ('c' * 40))
            $env:DOTCHARTER_RELEASE_CACHE = Join-Path $global:Fixture 'cache'
            $global:ClockTick = 0
            function global:Get-Date { $global:ClockTick++; [DateTime]::new(2020, 1, 1).AddSeconds($global:ClockTick) }
            function global:Start-Sleep { param([int]$Milliseconds) $global:ClockTick += [Math]::Ceiling($Milliseconds / 1000) }
            function global:Get-CimInstance { }
            function global:git {
                $global:LASTEXITCODE = 0
                $argv = @($args)
                $dir = [string]$argv[1]
                if (-not ([IO.Path]::GetFullPath($dir)).StartsWith($global:Fixture + [IO.Path]::DirectorySeparatorChar)) { throw 'git escaped fixture' }
                if ($argv -contains '--git-common-dir') { Join-Path $global:Main '.git'; return }
                if ($argv -contains '--absolute-git-dir') { Join-Path $global:Train '.git'; return }
                if ($argv -contains 'ls-tree') { '160000 commit ' + ('c' * 40) + "`tJuceSharp"; return }
                if ($argv -contains 'config') { if ($argv[-1] -eq 'user.name') { 'Bot' } else { 'bot@users.noreply.github.com' }; return }
                if ($argv -contains 'status') { return }
                if ($argv -contains 'diff') { 'src/change.cs'; return }
                if ($argv -contains 'worktree') { "worktree $global:Train"; return }
                if ($argv -contains 'ls-remote') { if ($global:Round -eq 3 -and '{{{preflight}}}' -eq 'base') { Revoke-ReusedGate }; ('b' * 40) + ' refs/heads/main'; return }
                if ($argv -contains 'push') { $global:Landings++; return }
                if ($argv -contains 'rev-parse') {
                    if ($argv[-1] -like '*:JuceSharp') { 'c' * 40 }
                    elseif ($argv[-1] -eq 'origin/main^{commit}') { 'b' * 40 }
                    else { 'a' * 40 }
                }
            }
            function global:gh {
                $global:LASTEXITCODE = 0
                if ($args[1] -eq 'comment') { $global:Comments++; return }
                if ($args[1] -eq 'view') {
                    if ($global:Round -eq 3 -and $args[-1] -eq 'state,headRefOid' -and '{{{preflight}}}' -eq 'head') { Revoke-ReusedGate }
                    $ready = $global:Landings -gt 0
                    if ($ready -and $args[-1] -eq 'state,mergeCommit') {
                        $global:ConfirmQueries++
                        $ready = '{{{confirmation}}}' -ne 'open' -and ('{{{confirmation}}}' -ne 'delay' -or $global:ConfirmQueries -gt 2)
                    }
                    @{ number = 1; state = $(if ($ready) { 'MERGED' } else { 'OPEN' }); mergeCommit = @{ oid = ('a' * 40) }; baseRefName = 'main'; headRefOid = ('d' * 40); title = 'Feature'; files = @(@{ path = 'src/change.cs' }); headRepositoryOwner = @{ login = 'owner' }; headRefName = 'feature' } | ConvertTo-Json -Depth 5 -Compress
                }
            }
            function global:Revoke-ReusedGate {
                $scope = Get-GateRecordInputs -RepoRoot $global:Train -Configuration Release
                Start-GateRecordRun -Trees @(('a' * 40)) -Kinds gate @scope
            }
            function global:Write-Host {
                if ([string]$args[0] -like '*evidence lost*') { $global:LostMessage = [string]$args[0] }
                if (${{{revokeAfterSave.ToString().ToLowerInvariant()}}} -and $global:Round -eq 3 -and [string]$args[0] -like '== Recorded:*') { Revoke-ReusedGate }
                Microsoft.PowerShell.Utility\Write-Host @args
            }
            $global:Calls = @(); $global:Landings = 0; $global:Precleared = $true
            function global:powershell {
                if ($args -notcontains '-File' -or ([string]$args[2]) -notlike '*Invoke-Gate.ps1') { throw "unexpected child: $args" }
                $lanes = @()
                if ($args -contains '-Gate') { $lanes += 'gate' }
                if ($args -contains '-ReleaseLane') { $lanes += 'release' }
                $global:Calls += "$global:Round`:$($lanes -join ',')"
                if ($global:Round -eq 2) {
                    $scope = Get-GateRecordInputs -RepoRoot $global:Train -Configuration Release
                    $old = Find-GateRecord -Trees @(('a' * 40)) -Kind releaseLane @scope
                    $global:Precleared = -not [bool]$old
                }
                if (${{{loseReused.ToString().ToLowerInvariant()}}} -and -not ${{{revokeAfterSave.ToString().ToLowerInvariant()}}} -and $global:Round -eq 3) { Revoke-ReusedGate }
                $summaries = foreach ($l in $lanes) {
                    $code = if ($global:Round -eq 3 -and ${{{ownLaneGreen.ToString().ToLowerInvariant()}}}) { 0 } elseif ($global:Round -gt 1 -and ($l -eq 'release' -or ${{{allRed.ToString().ToLowerInvariant()}}})) { 2 } else { 0 }
                    "$l exit $code"
                }
                "lanes: $($summaries -join ', ')"
                $global:LASTEXITCODE = if ($global:Round -eq 1 -or ($global:Round -eq 3 -and ${{{ownLaneGreen.ToString().ToLowerInvariant()}}})) { 0 } else { 2 }
            }
            # Each invocation is a real Windows PowerShell child. Its OS exit code is
            # captured even when the actual train throws; no catch fabricates a verdict.
            $env:DC_TRAIN_FIXTURE_ROOT = $global:Fixture
            $functions = foreach ($name in @('Get-Date', 'Start-Sleep', 'Get-CimInstance', 'git', 'gh', 'Revoke-ReusedGate', 'Write-Host', 'powershell')) {
                "function global:$name {`n$((Get-Command $name -CommandType Function).ScriptBlock)`n}"
            }
            $bootstrap = @'
            param([int]$Round, [switch]$Merge, [switch]$ForceGates)
            $ErrorActionPreference = 'Stop'
            $global:Fixture = $env:DC_TRAIN_FIXTURE_ROOT
            $global:Main = Join-Path $global:Fixture 'repo'
            $global:Train = Join-Path $global:Main '.claude/worktrees/train'
            $global:Round = $Round
            $statePath = Join-Path $global:Fixture 'state.json'
            $global:Calls = @(); $global:Landings = 0; $global:Precleared = $true; $global:ClockTick = 0; $global:LostMessage = ''
            $global:Comments = 0; $global:ConfirmQueries = 0
            if (Test-Path $statePath) {
                $state = Get-Content $statePath -Raw | ConvertFrom-Json
                $global:Calls = @($state.Calls); $global:Landings = $state.Landings; $global:Precleared = $state.Precleared; $global:ClockTick = $state.ClockTick; $global:LostMessage = $state.LostMessage
            }
            Import-Module (Join-Path $global:Main 'tools/release/ReleaseCache.psm1') -Force
            '@
            $bootstrap += "`n" + ($functions -join "`n") + "`n" + @'
            try {
                & (Join-Path $global:Main 'tools/agent/Merge-Train.ps1') -Prs 1 -Merge:$Merge -ForceGates:$ForceGates 6>$null
                exit $LASTEXITCODE
            }
            finally {
                $state = @{ Calls = @($global:Calls); Landings = $global:Landings; Precleared = $global:Precleared; ClockTick = $global:ClockTick; LostMessage = $global:LostMessage; Comments = $global:Comments }
                [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 5))
            }
            '@
            $bootstrapPath = Join-Path $global:Fixture 'invoke-train.ps1'
            [IO.File]::WriteAllText($bootstrapPath, $bootstrap, (New-Object Text.UTF8Encoding($true)))
            function Invoke-FixtureTrain([int]$Round, [switch]$Merge, [switch]$ForceGates) {
                $childArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $bootstrapPath, '-Round', $Round)
                if ($Merge) { $childArgs += '-Merge' }; if ($ForceGates) { $childArgs += '-ForceGates' }
                $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
                try { $text = & (Join-Path $PSHOME 'powershell.exe') @childArgs 2>&1; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $saved }
                $global:ChildOutput += ($text | Out-String)
                return $code
            }
            $first = Invoke-FixtureTrain 1
            "FIRST=$first"
            $snapshot = [IO.File]::ReadAllText((Get-GateRecordPath -Tree ('a' * 40)))
            $forced = Invoke-FixtureTrain 2 -ForceGates:(!${{{reuseAll.ToString().ToLowerInvariant()}}})
            "FORCED=$forced"
            $normal = Invoke-FixtureTrain 3 -Merge:${{{(!reuseAll || mergeReused).ToString().ToLowerInvariant()}}}
            "NORMAL=$normal"
            $state = Get-Content (Join-Path $global:Fixture 'state.json') -Raw | ConvertFrom-Json
            $global:Calls = @($state.Calls); $global:Landings = $state.Landings; $global:Precleared = $state.Precleared; $global:LostMessage = $state.LostMessage
            $global:ChildOutput
            "PRE=$global:Precleared"
            "CALLS=$($global:Calls -join '|')"
            "LANDINGS=$global:Landings"
            "COMMENTS=$($state.Comments)"
            "EVIDENCE=$global:LostMessage"
            $scope = Get-GateRecordInputs -RepoRoot $global:Train -Configuration Release
            $r = Find-GateRecord -Trees @(('a' * 40)) -Kind gate @scope
            "GATE=$([bool]$r)"
            "SAME=$($snapshot -eq [IO.File]::ReadAllText((Get-GateRecordPath -Tree ('a' * 40))))"
            Import-Module (Join-Path $global:Main 'tools/agent/MergeTrain.psm1') -Force
            "RELEASE=$([bool](Find-GateRecord -Trees @(('a' * 40)) -Kind releaseLane @scope))"
            $lock = Join-Path $global:Main '.git/dotcharter-merge-train.lock'
            "LOCK=$(Test-Path -LiteralPath $lock)"
            $handle = Enter-MergeTrainLock $lock
            Exit-MergeTrainLock $handle
            'RELOCK=True'
            "WORKTREE=$(Test-Path -LiteralPath $global:Train)"
            $next = Get-MergeTrainLanePlan -GateRecord $r -ReleaseRecord (Find-GateRecord -Trees @(('a' * 40)) -Kind releaseLane @scope) @scope
            "NEXT=$($next.Run -join ',')"
            """, "tools", "release", "ReleaseCache.psm1");

        return output;
    }
}
