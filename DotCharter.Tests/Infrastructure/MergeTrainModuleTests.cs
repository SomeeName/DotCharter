using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/agent/MergeTrain.psm1</c> — the decisions
///     <c>tools/agent/Merge-Train.ps1</c> makes from data alone (#668): whether a
///     train's change is docs only, and so takes the fast path (the build plus
///     the docs lints) instead of the full gate; which lanes a green record of the
///     tested tree covers (#614); which test hosts are the train's own to kill;
///     and the lock and the merges, which must hold under a race (#610).
///     Executed against the module itself under Windows PowerShell 5.1.
/// </summary>
public sealed class MergeTrainModuleTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-merge-train-" + Guid.NewGuid().ToString("N"));

    public MergeTrainModuleTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData("README.md|docs/agents/workflow.md|DotCharter.UI/AGENTS.md", true)]
    [InlineData("docs/images/before.png|docs/RSCP-FORMAT-GUARANTEES.md", true)]
    [InlineData("docs/agents/workflow.md|tools/agent/Merge-Train.ps1", false)]
    [InlineData("README.md|DotCharter.Core/Models/Note.cs", false)]
    [InlineData("DotCharter.Localization/Resources.resx", false)]
    [InlineData("docs/agents/workflow.md|DotCharter.Localization/Resources.de-DE.resx", false)]
    [InlineData("docs/agents/ui/dialogs.md|DotCharter.UI/Views/Dialogs/AboutDialog.axaml", false)]
    [InlineData("", false)]
    public void DocsOnly_IsEveryPathMarkdownOrUnderDocs(string paths, bool expected) =>
        Assert.Equal(expected.ToString(), Run($"Test-MergeTrainDocsOnly {PsArray(paths)}"));

    [Theory]
    [InlineData("docs/agents/ui/dialogs.md|DotCharter.UI/Views/Dialogs/AboutDialog.axaml", true)]
    [InlineData("DotCharter.Tone.UI/Rig/AmpFxRigView.axaml", true)]
    [InlineData("DotCharter.UI/Views/Shell/MainWindowPrewarm.cs", true)]
    [InlineData("README.md|DotCharter.Core/Models/Note.cs", false)]
    public void TouchesViews_IsAnyAxamlOrViewsFolder(string paths, bool expected) =>
        Assert.Equal(expected.ToString(), Run($"Test-MergeTrainTouchesViews {PsArray(paths)}"));

    /// <summary>
    ///     Only test hosts, and only inside THIS worktree: <c>train</c> must not
    ///     claim <c>train-20261006-021523</c> (another checkout) by prefix, and a
    ///     <c>dotnet.exe</c> build node inside it is not a test host.
    /// </summary>
    [Fact]
    public void OwnProcesses_AreTestHostsInsideTheTrainWorktree_OnADirectoryBoundary()
    {
        const string wt = @"C:\repo\.claude\worktrees\train";
        var output = Run($$"""
            $p = @(
              [pscustomobject]@{ Name = 'DotCharter.Tests.exe'; ExecutablePath = '{{wt}}\DotCharter.Tests\bin\Debug\net10.0\DotCharter.Tests.exe'; CommandLine = '' },
              [pscustomobject]@{ Name = 'testhost.exe'; ExecutablePath = 'C:\Program Files\dotnet\testhost.exe'; CommandLine = 'testhost.exe --source C:/repo/.claude/worktrees/train/JuceSharp/x.dll' },
              [pscustomobject]@{ Name = 'DotCharter.Tests.exe'; ExecutablePath = '{{wt}}-20261006-021523\DotCharter.Tests\bin\DotCharter.Tests.exe'; CommandLine = '' },
              [pscustomobject]@{ Name = 'JuceSharp.Tests.exe'; ExecutablePath = 'C:\repo\JuceSharp\tests\JuceSharp.Tests.exe'; CommandLine = '' },
              [pscustomobject]@{ Name = 'dotnet.exe'; ExecutablePath = 'C:\Program Files\dotnet\dotnet.exe'; CommandLine = 'dotnet build {{wt}}\DotCharter.sln' }
            )
            (@(Select-MergeTrainOwnProcesses $p '{{wt}}') | ForEach-Object { $_.Name }) -join '|'
            """);

        Assert.Equal("DotCharter.Tests.exe|testhost.exe", output);
    }

    /// <summary>The fast path's filter names real classes: a renamed lint would otherwise drop out of it silently.</summary>
    [Fact]
    public void DocsOnlyClasses_StartWithDocReferenceLintTests_AndEachNamesARealTestClass()
    {
        var classes = Run("(Get-MergeTrainDocsOnlyClasses) -join '|'").Split('|');

        Assert.Equal("*DocReferenceLintTests", classes[0]);
        var testRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");
        foreach (var pattern in classes)
        {
            Assert.StartsWith("*", pattern);
            var name = pattern.TrimStart('*');
            Assert.True(
                Directory.EnumerateFiles(testRoot, name + ".cs", SearchOption.AllDirectories).Any(),
                $"MergeTrain.psm1's docs fast path names {pattern}, but DotCharter.Tests has no {name}.cs.");
        }
    }

    /// <summary>#325: the train runs every lane in ONE Invoke-Gate.ps1 call and reads each
    /// lane's verdict back from its output — the concurrent `lanes:` line, or, after a
    /// sequential run that stops at the first red lane, the summary lines that were
    /// printed. Columns: gate ok, headless ok, release ok, headless ran, release ran.</summary>
    [Theory]
    [InlineData("lanes: gate exit 0, headless exit 0, release exit 0", 0, true, "True,True,True,True,True")]
    [InlineData("lanes: gate exit 2, release exit 0", 2, false, "False,True,True,False,True")]
    [InlineData("lanes: gate exit 0, headless exit 3, release exit 0", 3, true, "True,False,True,True,True")]
    [InlineData("lanes: gate exit 0, release exit 1", 1, false, "True,True,False,False,True")]
    [InlineData("gate: 9 passed, 0 failed|headless: 1 passed, 1 failed", 3, true, "True,False,True,True,False")]
    [InlineData("gate: 8 passed, 1 failed", 2, false, "False,True,True,False,False")]
    [InlineData("gate: 9 passed, 0 failed|release: 2 passed, 0 failed", 0, false, "True,True,True,False,True")]
    [InlineData("build FAILED", 1, false, "False,True,True,False,False")]
    public void LaneVerdicts_ComeFromTheLanesLine_OrFromTheSequentialRunsSummaries(string lines, int exit, bool headless, string expected) =>
        Assert.Equal(expected, Run(
            $"$v = Get-MergeTrainLaneVerdicts {PsArray(lines)} {exit} ${headless.ToString().ToLowerInvariant()}; " +
            "'{0},{1},{2},{3},{4}' -f $v.GateOk, $v.HeadlessOk, $v.ReleaseOk, $v.RanHeadless, $v.RanRelease"));

    /// <summary>#614: a lane the tested tree already has a green record for is not run again; it
    /// counts as green and covered. Everything reused = no lane run, all green.</summary>
    [Theory]
    [InlineData("release: 1 passed, 1 failed", 1, "gate", false, "True,True,False,False,True")]
    [InlineData("headless: 3 passed, 1 failed", 3, "gate,release", true, "True,False,True,True,True")]
    [InlineData("", 0, "gate,headless,release", true, "True,True,True,True,True")]
    [InlineData("build FAILED", 1, "gate", true, "True,False,True,True,False")]
    public void LaneVerdicts_CountReusedLanesAsGreen(string lines, int exit, string reused, bool headless, string expected) =>
        Assert.Equal(expected, Run(
            $"$v = Get-MergeTrainLaneVerdicts {PsArray(lines)} {exit} ${headless.ToString().ToLowerInvariant()} {PsArray(reused.Replace(',', '|'))}; " +
            "'{0},{1},{2},{3},{4}' -f $v.GateOk, $v.HeadlessOk, $v.ReleaseOk, $v.RanHeadless, $v.RanRelease"));

    /// <summary>#614: only lanes without a green record run; -ForceGates runs them all; a red
    /// train records exactly the lanes that ran green.</summary>
    [Fact]
    public void LanePlan_RunsOnlyLanesWithoutAGreenRecord_AndARedTrainKeepsItsGreenLanes()
    {
        var output = Run("""
            $r = [pscustomobject]@{ Tree = ('a' * 40); Source = 'train' }
            function Show([string]$Name, $Plan) { "$Name=run:$($Plan.Run -join ',');reused:$($Plan.Reused -join ',');args:$($Plan.Args -join ',')" }
            Show 'none' (Get-MergeTrainLanePlan -WantHeadless $true)
            Show 'gate+release' (Get-MergeTrainLanePlan -WantHeadless $true -GateRecord $r -ReleaseRecord $r)
            Show 'no-headless' (Get-MergeTrainLanePlan -WantHeadless $false -GateRecord $r -HeadlessRecord $r)
            Show 'force' (Get-MergeTrainLanePlan -WantHeadless $false -GateRecord $r -ReleaseRecord $r -Force $true)
            $red = Get-MergeTrainLaneVerdicts @('lanes: gate exit 0, headless exit 2, release exit 0') 2 $true
            "green=$((Get-MergeTrainGreenLanes $red) -join ',')"
            $redGate = Get-MergeTrainLaneVerdicts @('gate: 8 passed, 1 failed') 2 $true
            "green-red-gate=$((Get-MergeTrainGreenLanes $redGate) -join ',')"
            """).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Contains("none=run:gate,headless,release;reused:;args:-Gate,-Headless,-ReleaseLane", output);
        Assert.Contains("gate+release=run:headless;reused:gate,release;args:-Headless", output);
        Assert.Contains("no-headless=run:release;reused:gate;args:-ReleaseLane", output);
        Assert.Contains("force=run:gate,release;reused:;args:-Gate,-ReleaseLane", output);
        Assert.Contains("green=gate,release", output);
        // A sequential run that stopped at the red gate ran nothing else: nothing to keep.
        Assert.Contains("green-red-gate=", output);
    }

    /// <summary>#610: the lock is ONE exclusive open, so a second train cannot take it while
    /// the first holds it, and closing it deletes the file.</summary>
    [Fact]
    public void TrainLock_RefusesASecondHolder_AndIsGoneOnceReleased()
    {
        var lockPath = Path.Combine(_scratch, "dotcharter-merge-train.lock");
        var output = Run($$"""
            $first = Enter-MergeTrainLock '{{lockPath}}'
            $second = try { $null = Enter-MergeTrainLock '{{lockPath}}'; 'second-acquired' } catch { 'second-refused' }
            Exit-MergeTrainLock $first
            $after = Test-Path -LiteralPath '{{lockPath}}'
            $again = Enter-MergeTrainLock '{{lockPath}}'
            Exit-MergeTrainLock $again
            "$second|exists-after-release=$after"
            """);

        Assert.Equal("second-refused|exists-after-release=False", output);
    }

    /// <summary>The native bare-repository cases live in MergeTrainLandingTests; these
    /// injection cases also run where native receive-pack is unavailable.</summary>
    [Theory]
    [InlineData(1, "ok", 1, 1)]
    [InlineData(2, "ok", 1, 2)]
    [InlineData(1, "boundary", 1, 0)]
    [InlineData(2, "boundary", 1, 0)]
    [InlineData(2, "head", 0, 0)]
    [InlineData(2, "base", 0, 0)]
    [InlineData(2, "unconfirmed", 1, 0)]
    [InlineData(1, "email", 0, 0)]
    [InlineData(1, "push-error", 1, 0)]
    public void Landing_UsesOneExactTipPush_AndCommentsOnlyAfterAllConfirmations(int count, string scenario, int pushes, int comments)
    {
        var output = Run($$$"""
            $global:TrainCalls = @(); $global:TrainPushed = $false
            $global:TrainBase = '{{{(scenario == "base" ? new string('c', 40) : new string('b', 40))}}}'
            $git = {
                param([string[]]$a)
                $global:TrainCalls += 'git ' + ($a -join ' ')
                if ($a[0] -eq 'config') {
                    $v = if ($a[-1] -eq 'user.name') { 'Bot' } else { '{{{(scenario == "email" ? "unsafe@example.invalid" : "bot@users.noreply.github.com")}}}' }
                    return @{ Code = 0; Out = $v }
                }
                if ($a[0] -eq 'ls-remote') { return @{ Code = 0; Out = "$global:TrainBase refs/heads/main" } }
                if ($a[0] -eq 'push') {
                    if ('{{{scenario}}}' -eq 'boundary') { $global:TrainBase = 'c' * 40; return @{ Code = 1; Out = 'non-fast-forward' } }
                    $global:TrainPushed = $true; $global:TrainBase = 'a' * 40
                    if ('{{{scenario}}}' -eq 'push-error') { return @{ Code = 1; Out = 'reply lost' } }
                    return @{ Code = 0; Out = '' }
                }
                throw "unexpected git: $a"
            }
            $gh = {
                param([string[]]$a)
                $global:TrainCalls += 'gh ' + ($a -join ' ')
                if ($a[0] -eq 'api') { return @{ Code = 0; Out = $global:TrainBase } }
                if ($a[1] -eq 'merge') { $global:TrainPushed = $true; return @{ Code = 0; Out = '' } }
                if ($a[1] -eq 'comment') { return @{ Code = 0; Out = '' } }
                $state = if ($global:TrainPushed -and -not ('{{{scenario}}}' -eq 'unconfirmed' -and $a[2] -eq '2')) { 'MERGED' } else { 'OPEN' }
                $head = if ('{{{scenario}}}' -eq 'head') { 'moved' } else { "head$($a[2])" }
                @{ Code = 0; Out = (@{ state = $state; headRefOid = $head; mergeCommit = @{ oid = ('a' * 40) } } | ConvertTo-Json -Compress) }
            }
            $prs = @(1..{{{count}}} | ForEach-Object { [pscustomobject]@{ Number = $_; Head = "head$_" } })
            $global:ConfirmTime = [DateTime]::new(2020, 1, 1)
            $timing = @{}
            if ((Get-Command Invoke-MergeTrainMerges).Parameters.ContainsKey('ConfirmationClock')) {
                $timing = @{ ConfirmationClock = { $global:ConfirmTime }; ConfirmationDelay = { param([double]$Seconds) $global:ConfirmTime = $global:ConfirmTime.AddSeconds($Seconds) } }
            }
            $outcome = try { $null = Invoke-MergeTrainMerges -Prs $prs -Repo o/r -ExpectedBase ('b' * 40) -Tip ('a' * 40) -Git $git -Gh $gh -Comment tested @timing 6>$null; 'landed' } catch { $_.Exception.Message }
            "OUTCOME=$outcome"
            "PUSHES=$(@($global:TrainCalls | Where-Object { $_ -like 'git push *' }).Count)"
            "COMMENTS=$(@($global:TrainCalls | Where-Object { $_ -like 'gh pr comment *' }).Count)"
            $global:TrainCalls -join "`n"
            """);

        Assert.Contains($"PUSHES={pushes}", output, StringComparison.Ordinal);
        Assert.Contains($"COMMENTS={comments}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("gh pr merge", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", output, StringComparison.Ordinal);
        if (pushes > 0)
            Assert.Contains($"git push --porcelain origin {new string('a', 40)}:refs/heads/main", output, StringComparison.Ordinal);
        Assert.Contains(scenario switch
        {
            "ok" => "OUTCOME=landed",
            "boundary" or "base" => "base moved",
            "head" => "head moved",
            "email" => "noreply",
            _ => "confirm"
        }, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_UsesConfiguredIdentityAndGithubMessage_ThenRestoresEnvironment()
    {
        var output = Run("""
            $env:GIT_AUTHOR_EMAIL = 'unsafe@example.invalid'; $env:GIT_COMMITTER_NAME = 'Wrong'
            $git = {
                param([string[]]$a)
                if ($a[0] -eq 'config') { return @{ Code = 0; Out = $(if ($a[-1] -eq 'user.name') { 'Bot' } else { 'bot@users.noreply.github.com' }) } }
                if ($a[0] -eq '-c') {
                    $global:CommitMessage = $a[6]
                    $global:CommitIdentity = "$env:GIT_AUTHOR_NAME|$env:GIT_AUTHOR_EMAIL|$env:GIT_COMMITTER_NAME|$env:GIT_COMMITTER_EMAIL"
                }
                @{ Code = 0; Out = ('a' * 40) }
            }
            $p = [pscustomobject]@{ Number = 42; Head = 'head'; Owner = 'owner'; Branch = 'topic'; Title = 'The title' }
            $null = New-MergeTrainCommit $p $git
            $global:CommitMessage
            $global:CommitIdentity
            "restored=$env:GIT_AUTHOR_EMAIL|$env:GIT_COMMITTER_NAME"
            """);

        Assert.Contains("Merge pull request #42 from owner/topic\n\nThe title", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Bot|bot@users.noreply.github.com|Bot|bot@users.noreply.github.com", output, StringComparison.Ordinal);
        Assert.Contains("restored=unsafe@example.invalid|Wrong", output, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneVerdicts_AMissingRequestedSummary_IsIncompleteEvenWhenTheProcessExitsZero() =>
        Assert.Equal("False", Run("(Get-MergeTrainLaneVerdicts @('lanes: gate exit 0') 0 $true).Complete"));

    /// <summary>Configuration and lane-list contents are inputs to every cached lane, including partial green runs.</summary>
    [Fact]
    public void LanePlan_RefusesRecordsFromAnotherConfigurationOrLaneList()
    {
        var output = Run("""
            $r = [pscustomobject]@{ Tree = ('a' * 40); Configuration = 'Release'; InputKey = 'list-v1' }
            $debug = [pscustomobject]@{ Tree = ('a' * 40); Configuration = 'Debug'; InputKey = 'list-v1' }
            $oldList = [pscustomobject]@{ Tree = ('a' * 40); Configuration = 'Release'; InputKey = 'list-v0' }
            $legacy = [pscustomobject]@{ Tree = ('a' * 40) }
            foreach ($record in @($r, $debug, $oldList, $legacy)) {
                $p = Get-MergeTrainLanePlan -WantHeadless $true -GateRecord $record -HeadlessRecord $record -ReleaseRecord $record -Configuration Release -InputKey list-v1
                "run=$($p.Run -join ',');reused=$($p.Reused -join ',')"
            }
            """);

        Assert.Equal("run=;reused=gate,headless,release\n" +
                     string.Join('\n', Enumerable.Repeat("run=gate,headless,release;reused=", 3)),
            output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void LaneRecords_RoundTripConfigurationAndLaneInputs_AndChangingEitherRunsTheLane()
    {
        var cache = Path.Combine(_scratch, "cache");
        var laneFile = Path.Combine(_scratch, "release-lane.json");
        File.WriteAllText(laneFile, "list-v1");
        var output = Run($$"""
            Import-Module '{{Path.Combine(TestPaths.RepoRoot(), "tools", "release", "ReleaseCache.psm1")}}' -Force
            $env:DOTCHARTER_RELEASE_CACHE = '{{cache}}'
            $key = Get-PinKey -Root '{{_scratch}}' -RelativePaths 'release-lane.json'
            [void](Save-GateRecord -Tree ('a' * 40) -Gate -ReleaseLane -Source train -Configuration Release -InputKey $key)
            $r = Find-GateRecord -Trees @(('a' * 40)) -Kind gate
            $p = Get-MergeTrainLanePlan -GateRecord $r -Configuration Release -InputKey $key
            "same=$($p.Reused -join ',')"
            $p = Get-MergeTrainLanePlan -GateRecord $r -Configuration Debug -InputKey $key
            "debug=$($p.Reused -join ',')"
            [IO.File]::WriteAllText('{{laneFile}}', 'list-v2')
            $p = Get-MergeTrainLanePlan -GateRecord $r -Configuration Release -InputKey (Get-PinKey -Root '{{_scratch}}' -RelativePaths 'release-lane.json')
            "changed-list=$($p.Reused -join ',')"
            $other = Find-GateRecord -Trees @(('b' * 40)) -Kind gate
            "other-tree=$([bool]$other)"
            """);

        Assert.Equal("same=gate\ndebug=\nchanged-list=\nother-tree=False", output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, false, "install pinned")]
    [InlineData(false, false, true, "install pinned")]
    [InlineData(true, false, false, "stop")]
    [InlineData(true, false, true, "stop")]
    [InlineData(false, true, false, "keep")]
    [InlineData(true, true, false, "keep")]
    public void NativePairPlan_TrainRequiresBothPinnedHashes(bool attempted, bool matching, bool onlyDll, string expected)
    {
        var output = Run($$"""
            Import-Module '{{Path.Combine(TestPaths.RepoRoot(), ".claude", "skills", "dotcharter-worktree", "scripts", "AgentWorktree.psm1")}}' -Force
            $entry = [pscustomobject]@{ commit = ('a' * 40); files = @{ 'juce_sharp.dll' = ('b' * 64); 'juce_vst_host.exe' = ('c' * 64) } }
            $hashes = @{}
            if (${{matching}}) { $hashes = $entry.files.Clone() }
            if (${{onlyDll}}) { $hashes['juce_sharp.dll'] = $entry.files['juce_sharp.dll'] }
            $p = Get-JuceNativePairPlan -Gitlink ('a' * 40) -CopiedHead ('a' * 40) -SiblingFound $true -ManifestEntry $entry -Hashes $hashes -Train -InstallAttempted:${{attempted}}
            "$($p.Action)|$($p.Reason)"
            """);
        Assert.StartsWith(expected + "|", output);
        if (expected == "stop") Assert.Contains("juce_vst_host.exe", output);
    }

    [Fact]
    public void NativePairPlan_TrainStopsWithoutPin() =>
        Assert.StartsWith("stop|No native runtime pin for JuceSharp ", Run($$"""
            Import-Module '{{Path.Combine(TestPaths.RepoRoot(), ".claude", "skills", "dotcharter-worktree", "scripts", "AgentWorktree.psm1")}}' -Force
            $p = Get-JuceNativePairPlan -Gitlink ('a' * 40) -Hashes @{} -Train
            "$($p.Action)|$($p.Reason)"
            """));

    [Fact]
    public void Script_ChecksTestedTreesNativePairBeforeEveryGate()
    {
        var code = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "Merge-Train.ps1"));
        var tip = code.IndexOf("$testedTip = $tipResult.Out", StringComparison.Ordinal);
        var check = code.IndexOf("Get-JuceNativePairPlan", tip, StringComparison.Ordinal);
        var gate = code.IndexOf("& powershell -NoProfile -File $gate", StringComparison.Ordinal);
        Assert.True(check > tip && gate > check, "the tested tree's native pair must be checked before every gate");
        var preflight = code[tip..gate];
        Assert.Contains("Get-JuceSharpGitlink $wt", preflight);
        Assert.Contains("Get-JuceNativePairState -RepoRoot $wt", preflight);
        Assert.Contains("-Train", preflight);
        Assert.Contains("-InstallAttempted", preflight);
        Assert.Contains("-File $installer -Force", preflight);
        Assert.Contains("throw $nativePlan.Reason", preflight);
        Assert.DoesNotContain("if ($rebuild)", preflight);
    }

    private static string PsArray(string joined) =>
        "@(" + string.Join(", ", joined.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => $"'{p.Replace("'", "''")}'")) + ")";

    private string Run(string body) => HeadlessShardsModule.RunModule(_scratch, body, "tools", "agent", "MergeTrain.psm1");
}
