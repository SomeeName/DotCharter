using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>The gated commits land in one ref update against a local bare remote.</summary>
public sealed class MergeTrainLandingTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-landing-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Landing_PushesTheExactTestedTipOnce_WithConfiguredIdentityAndGithubMessages(int count)
    {
        using var result = Run(count);
        var row = result.RootElement;

        Assert.Equal("landed", row.GetProperty("Outcome").GetString());
        Assert.Equal(row.GetProperty("Tip").GetString(), row.GetProperty("Remote").GetString());
        Assert.Equal(1, row.GetProperty("Pushes").GetInt32());
        Assert.Equal(0, row.GetProperty("GhMerges").GetInt32());
        Assert.Equal(count, row.GetProperty("Comments").GetArrayLength());
        for (var i = 0; i < count; i++)
        {
            Assert.Equal($"Merge pull request #{i + 1} from owner/feature-{i + 1}\n\nFeature {i + 1}",
                row.GetProperty("Messages")[i].GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal("Configured Bot|bot@users.noreply.github.com|Configured Bot|bot@users.noreply.github.com",
                row.GetProperty("Identities")[i].GetString());
        }

        Assert.Equal("intruder@example.invalid", row.GetProperty("RestoredEmail").GetString());
        Assert.DoesNotContain("--force", row.GetProperty("Calls").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Landing_ABaseMoveAtThePushBoundary_RejectsTheWholeTrain(int count)
    {
        using var result = Run(count, moveBase: true);
        var row = result.RootElement;

        Assert.Contains("base moved", row.GetProperty("Outcome").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(row.GetProperty("Side").GetString(), row.GetProperty("Remote").GetString());
        Assert.Empty(row.GetProperty("Comments").EnumerateArray());
    }

    [Fact]
    public void Landing_AHeadThatMovedSinceGating_ChangesNoBaseAndCommentsOnNothing()
    {
        using var result = Run(2, moveHead: true);
        var row = result.RootElement;

        Assert.Contains("head", row.GetProperty("Outcome").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(row.GetProperty("Base").GetString(), row.GetProperty("Remote").GetString());
        Assert.Empty(row.GetProperty("Comments").EnumerateArray());
    }

    [Fact]
    public void Landing_ARealConfiguredEmail_IsRefusedBeforeAnythingCanLand()
    {
        using var result = Run(1, realEmail: true);
        var row = result.RootElement;

        Assert.Contains("noreply", row.GetProperty("Outcome").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(row.GetProperty("Base").GetString(), row.GetProperty("Remote").GetString());
        Assert.Empty(row.GetProperty("Comments").EnumerateArray());
    }

    [Fact]
    public void Landing_AConfiguredEmailChangedAfterGating_IsStillRefused()
    {
        using var result = Run(1, realAtLanding: true);
        var row = result.RootElement;

        Assert.Contains("noreply", row.GetProperty("Outcome").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(row.GetProperty("Base").GetString(), row.GetProperty("Remote").GetString());
        Assert.Empty(row.GetProperty("Comments").EnumerateArray());
    }

    [Fact]
    public void Landing_ConfirmsEveryPrBeforeAnySuccessComment()
    {
        using var result = Run(2, openPr: true);
        var row = result.RootElement;

        Assert.Contains("confirm", row.GetProperty("Outcome").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not confirm #2 MERGED", row.GetProperty("Outcome").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("#1", row.GetProperty("Outcome").GetString(), StringComparison.Ordinal);
        Assert.Contains("IS on origin/main", row.GetProperty("Outcome").GetString(), StringComparison.Ordinal);
        Assert.Contains("do not re-run", row.GetProperty("Outcome").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(60, row.GetProperty("DelaySeconds").GetDouble());
        Assert.Equal(row.GetProperty("Tip").GetString(), row.GetProperty("Remote").GetString());
        Assert.Empty(row.GetProperty("Comments").EnumerateArray());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void Landing_WaitsForDelayedConfirmations_AndTransientFailures_WithoutEarlyComments(int count, bool transient)
    {
        using var result = Run(count, confirmDelay: true, transient: transient);
        var row = result.RootElement;

        Assert.Equal("landed", row.GetProperty("Outcome").GetString());
        Assert.Equal(row.GetProperty("Tip").GetString(), row.GetProperty("Remote").GetString());
        Assert.Equal(1, row.GetProperty("Pushes").GetInt32());
        Assert.Equal(Enumerable.Range(1, count), row.GetProperty("Comments").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(0, row.GetProperty("EarlyComments").GetInt32());
        Assert.True(row.GetProperty("DelaySeconds").GetDouble() > 0);
    }

    private JsonDocument Run(int count, bool moveBase = false, bool moveHead = false, bool realEmail = false, bool openPr = false, bool realAtLanding = false, bool confirmDelay = false, bool transient = false)
    {
        var run = PublicSnapshotExportTests.RunPowerShell(_scratch, $$$"""
            Import-Module '{{{Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "MergeTrain.psm1")}}}' -Force
            $global:FixtureRoot = '{{{_scratch.Replace("'", "''", StringComparison.Ordinal)}}}'
            $global:FixtureWork = Join-Path $global:FixtureRoot 'work'
            $global:FixtureRemote = Join-Path $global:FixtureRoot 'remote.git'
            $env:GIT_INDEX_FILE = $null; $env:GIT_OBJECT_DIRECTORY = $null; $env:GIT_ALTERNATE_OBJECT_DIRECTORIES = $null
            function global:Invoke-FixtureGit([string]$Dir, [string[]]$Argv) {
                if (-not ([IO.Path]::GetFullPath($Dir)).StartsWith($global:FixtureRoot + [IO.Path]::DirectorySeparatorChar) -and $Dir -ne $global:FixtureRoot) { throw 'fixture escaped its root' }
                $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
                try { $out = & git -C $Dir @Argv 2>&1; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $saved }
                [pscustomobject]@{ Out = ($out | Out-String).Trim(); Code = $code }
            }
            function global:Must-FixtureGit([string]$Dir, [string[]]$Argv) {
                $r = Invoke-FixtureGit $Dir $Argv
                if ($r.Code -ne 0) { throw "fixture git failed: $($r.Out)" }
                $r.Out
            }
            $null = Must-FixtureGit $global:FixtureRoot @('init', '--bare', '-q', $global:FixtureRemote)
            $null = Must-FixtureGit $global:FixtureRoot @('init', '-q', '-b', 'main', $global:FixtureWork)
            $null = Must-FixtureGit $global:FixtureWork @('config', 'user.name', 'Configured Bot')
            $null = Must-FixtureGit $global:FixtureWork @('config', 'user.email', 'bot@users.noreply.github.com')
            $null = Must-FixtureGit $global:FixtureWork @('config', 'commit.gpgsign', 'false')
            [IO.File]::WriteAllText((Join-Path $global:FixtureWork 'base.txt'), 'base')
            $null = Must-FixtureGit $global:FixtureWork @('add', '.')
            $null = Must-FixtureGit $global:FixtureWork @('commit', '-qm', 'base')
            $global:FixtureBase = Must-FixtureGit $global:FixtureWork @('rev-parse', 'HEAD')
            $null = Must-FixtureGit $global:FixtureWork @('remote', 'add', 'origin', $global:FixtureRemote)
            $null = Must-FixtureGit $global:FixtureWork @('push', '-q', 'origin', 'HEAD:refs/heads/main')
            $global:FixturePrs = @()
            foreach ($n in 1..{{{count}}}) {
                $null = Must-FixtureGit $global:FixtureWork @('switch', '-q', '--detach', $global:FixtureBase)
                [IO.File]::WriteAllText((Join-Path $global:FixtureWork "feature-$n.txt"), "feature $n")
                $null = Must-FixtureGit $global:FixtureWork @('add', '.')
                $null = Must-FixtureGit $global:FixtureWork @('commit', '-qm', "feature $n")
                $head = Must-FixtureGit $global:FixtureWork @('rev-parse', 'HEAD')
                $null = Must-FixtureGit $global:FixtureWork @('push', '-q', 'origin', "$head`:refs/heads/feature-$n")
                $global:FixturePrs += [pscustomobject]@{ Number = $n; Head = $head; Owner = 'owner'; Branch = "feature-$n"; Title = "Feature $n"; MergeCommit = '' }
            }
            $null = Must-FixtureGit $global:FixtureWork @('switch', '-q', '--detach', $global:FixtureBase)
            $global:FixturePushes = 0; $global:FixtureGhMerges = 0; $global:FixtureComments = @(); $global:FixtureSide = ''
            $global:FixtureCalls = @(); $global:MoveFixtureBase = ${{{moveBase.ToString().ToLowerInvariant()}}}
            $global:ConfirmTime = [DateTime]::new(2020, 1, 1); $global:ConfirmSleep = 0
            $global:FixtureChecks = @{}; $global:FixtureConfirmed = @{}; $global:FixtureEarlyComments = 0
            function global:Move-FixtureBase {
                if (-not $global:MoveFixtureBase -or $global:FixtureSide) { return }
                $tree = Must-FixtureGit $global:FixtureWork @('rev-parse', "$global:FixtureBase`^{tree}")
                $global:FixtureSide = Must-FixtureGit $global:FixtureWork @('commit-tree', $tree, '-p', $global:FixtureBase, '-m', 'other merge')
                $null = Must-FixtureGit $global:FixtureWork @('push', '-q', 'origin', "$global:FixtureSide`:refs/heads/other")
                $null = Must-FixtureGit $global:FixtureRemote @('update-ref', 'refs/heads/main', $global:FixtureSide, $global:FixtureBase)
            }
            $git = {
                param([string[]]$Argv)
                $global:FixtureCalls += $Argv -join ' '
                if ($Argv[0] -eq 'push') { $global:FixturePushes++; Move-FixtureBase }
                Invoke-FixtureGit $global:FixtureWork $Argv
            }
            $gh = {
                param([string[]]$Argv)
                if ($Argv[0] -eq 'api') {
                    if ($Argv[1] -like '*/commits/*') {
                        $commit = ($Argv[1] -split '/')[-1]
                        return @{ Code = 0; Out = (Must-FixtureGit $global:FixtureWork @('rev-parse', "$commit`^1")) }
                    }
                    return @{ Code = 0; Out = (Must-FixtureGit $global:FixtureRemote @('rev-parse', 'refs/heads/main')) }
                }
                $p = $global:FixturePrs[[int]$Argv[2] - 1]
                if ($Argv[1] -eq 'merge') {
                    $global:FixtureGhMerges++; Move-FixtureBase
                    $head = Must-FixtureGit $global:FixtureRemote @('rev-parse', "refs/heads/feature-$($p.Number)")
                    if ($head -ne $p.Head) { return @{ Code = 1; Out = 'head moved' } }
                    $now = Must-FixtureGit $global:FixtureRemote @('rev-parse', 'refs/heads/main')
                    $tree = Must-FixtureGit $global:FixtureWork @('rev-parse', "$($p.MergeCommit)`^{tree}")
                    $merge = Must-FixtureGit $global:FixtureWork @('commit-tree', $tree, '-p', $now, '-p', $p.Head, '-m', 'untested gh merge')
                    $null = Must-FixtureGit $global:FixtureWork @('push', '-q', 'origin', "$merge`:refs/heads/main")
                    return @{ Code = 0; Out = '' }
                }
                if ($Argv[1] -eq 'comment') {
                    if ($global:FixtureConfirmed.Count -ne {{{count}}}) { $global:FixtureEarlyComments++ }
                    $global:FixtureComments += $p.Number; return @{ Code = 0; Out = '' }
                }
                $confirming = $Argv[-1] -eq 'state,mergeCommit'
                if ($confirming) {
                    $global:FixtureChecks[$p.Number] = [int]$global:FixtureChecks[$p.Number] + 1
                    if (${{{transient.ToString().ToLowerInvariant()}}} -and $global:FixtureChecks[$p.Number] -eq 1) { return @{ Code = 1; Out = 'temporary API failure' } }
                }
                $now = Must-FixtureGit $global:FixtureRemote @('rev-parse', 'refs/heads/main')
                $head = Must-FixtureGit $global:FixtureRemote @('rev-parse', "refs/heads/feature-$($p.Number)")
                $ancestor = Invoke-FixtureGit $global:FixtureWork @('merge-base', '--is-ancestor', $head, $now)
                $ready = -not $confirming -or -not ${{{confirmDelay.ToString().ToLowerInvariant()}}} -or $global:FixtureChecks[$p.Number] -gt (2 * $p.Number)
                $state = if ($ancestor.Code -eq 0 -and $ready -and -not (${{{openPr.ToString().ToLowerInvariant()}}} -and $p.Number -eq 2)) { 'MERGED' } else { 'OPEN' }
                if ($confirming -and $state -eq 'MERGED') { $global:FixtureConfirmed[$p.Number] = $true }
                $json = [pscustomobject]@{ state = $state; headRefOid = $head; mergeCommit = [pscustomobject]@{ oid = $now } } | ConvertTo-Json -Compress
                @{ Code = 0; Out = $json }
            }
            if (${{{realEmail.ToString().ToLowerInvariant()}}}) { $null = Must-FixtureGit $global:FixtureWork @('config', 'user.email', 'unsafe@example.invalid') }
            $env:GIT_AUTHOR_EMAIL = 'intruder@example.invalid'; $env:GIT_COMMITTER_EMAIL = 'intruder@example.invalid'
            $env:GIT_AUTHOR_NAME = 'Intruder'; $env:GIT_COMMITTER_NAME = 'Intruder'
            $tip = ''; $messages = @(); $identities = @()
            $outcome = try {
                foreach ($p in $global:FixturePrs) {
                    $m = New-MergeTrainCommit -Pr $p -Git $git
                    if ($m.Code -ne 0) { throw $m.Out }
                    $p.MergeCommit = $m.Commit; $tip = $m.Commit
                    $messages += Must-FixtureGit $global:FixtureWork @('show', '-s', '--format=%B', $tip)
                    $identities += Must-FixtureGit $global:FixtureWork @('show', '-s', '--format=%an|%ae|%cn|%ce', $tip)
                }
                if (${{{moveHead.ToString().ToLowerInvariant()}}}) {
                    $p = $global:FixturePrs[0]
                    $null = Must-FixtureGit $global:FixtureWork @('switch', '-q', '--detach', $p.Head)
                    [IO.File]::WriteAllText((Join-Path $global:FixtureWork 'moved.txt'), 'moved')
                    $null = Must-FixtureGit $global:FixtureWork @('add', '.')
                    $null = Must-FixtureGit $global:FixtureWork @('commit', '-qm', 'moved head')
                    $null = Must-FixtureGit $global:FixtureWork @('push', '-q', 'origin', 'HEAD:refs/heads/feature-1')
                }
                if (${{{realAtLanding.ToString().ToLowerInvariant()}}}) { $null = Must-FixtureGit $global:FixtureWork @('config', 'user.email', 'unsafe@example.invalid') }
                $timing = @{}
                if ((Get-Command Invoke-MergeTrainMerges).Parameters.ContainsKey('ConfirmationClock')) {
                    $timing = @{ ConfirmationClock = { $global:ConfirmTime }; ConfirmationDelay = { param([double]$Seconds) $global:ConfirmSleep += $Seconds; $global:ConfirmTime = $global:ConfirmTime.AddSeconds($Seconds) } }
                }
                $null = Invoke-MergeTrainMerges -Prs $global:FixturePrs -Repo 'owner/repo' -ExpectedBase $global:FixtureBase -Tip $tip -Remote 'origin' -Git $git -Gh $gh -Comment 'tested' @timing 6>$null
                'landed'
            } catch { $_.Exception.Message }
            [pscustomobject]@{ Outcome = $outcome; Tip = $tip; Base = $global:FixtureBase; Side = $global:FixtureSide
                Remote = (Must-FixtureGit $global:FixtureRemote @('rev-parse', 'refs/heads/main')); Pushes = $global:FixturePushes
                GhMerges = $global:FixtureGhMerges; Comments = @($global:FixtureComments); Messages = @($messages); Identities = @($identities)
                Calls = ($global:FixtureCalls -join "`n"); RestoredEmail = $env:GIT_AUTHOR_EMAIL
                DelaySeconds = $global:ConfirmSleep; EarlyComments = $global:FixtureEarlyComments } | ConvertTo-Json -Depth 5 -Compress
            """, "powershell");
        Assert.True(run.ExitCode == 0, run.Output);
        return JsonDocument.Parse(run.Output.Trim());
    }
}
