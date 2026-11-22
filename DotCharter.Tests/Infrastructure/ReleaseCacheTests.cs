using System.Diagnostics;
using System.Text;
using static DotCharter.Tests.Infrastructure.PublicSnapshotExportTests;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/release/ReleaseCache.psm1</c>, the caches that live OUTSIDE every tree so a
///     fresh scratch export does not start cold: the minimal ffmpeg and yt-dlp keyed by their
///     pins, and the green gate / headless runs recorded per git tree id. The module runs
///     under pwsh on fixture folders and repos; the record functions also under Windows
///     PowerShell 5.1, which <c>tools/agent/Merge-Train.ps1</c> runs in. The cases at the
///     bottom keep <c>New-Release.ps1</c> and <c>Merge-Train.ps1</c> CALLING it.
/// </summary>
public sealed class ReleaseCacheTests
{
    private static string Module => Path.Combine(TestPaths.RepoRoot(), "tools", "release", "ReleaseCache.psm1");
    private static string ReleaseDir => Path.Combine(TestPaths.RepoRoot(), "tools", "release");

    private static string Script(string name) => File.ReadAllText(Path.Combine(ReleaseDir, name));

    [Fact]
    public void FfmpegPinKey_IgnoresLineEndings_AndMovesWithEveryPinnedInput()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-release-cache-key-").FullName;
        try
        {
            WriteTree(scratch, "lf/Build-MinimalFfmpeg.ps1", "$Versions = 1\n");
            WriteTree(scratch, "lf/ffmpeg-min/components.json", "{ \"ffmpegVersion\": \"8.1.3\" }\n");
            WriteTree(scratch, "lf/ffmpeg-min/build.sh", "make\n");
            WriteTree(scratch, "crlf/Build-MinimalFfmpeg.ps1", "$Versions = 1\r\n");
            WriteTree(scratch, "crlf/ffmpeg-min/components.json", "{ \"ffmpegVersion\": \"8.1.3\" }\r\n");
            WriteTree(scratch, "crlf/ffmpeg-min/build.sh", "make\r\n");
            WriteTree(scratch, "script/Build-MinimalFfmpeg.ps1", "$Versions = 2\n");
            WriteTree(scratch, "script/ffmpeg-min/components.json", "{ \"ffmpegVersion\": \"8.1.3\" }\n");
            WriteTree(scratch, "script/ffmpeg-min/build.sh", "make\n");
            WriteTree(scratch, "inventory/Build-MinimalFfmpeg.ps1", "$Versions = 1\n");
            WriteTree(scratch, "inventory/ffmpeg-min/components.json", "{ \"ffmpegVersion\": \"8.1.4\" }\n");
            WriteTree(scratch, "inventory/ffmpeg-min/build.sh", "make\n");
            WriteTree(scratch, "added/Build-MinimalFfmpeg.ps1", "$Versions = 1\n");
            WriteTree(scratch, "added/ffmpeg-min/components.json", "{ \"ffmpegVersion\": \"8.1.3\" }\n");
            WriteTree(scratch, "added/ffmpeg-min/build.sh", "make\n");
            WriteTree(scratch, "added/ffmpeg-min/msys2-packages.json", "[]\n");

            var run = RunPowerShell(scratch, $$"""
                Import-Module {{Quote(Module)}} -Force
                foreach ($d in 'lf', 'crlf', 'script', 'inventory', 'added') { "$d=$(Get-FfmpegPinKey -ReleaseDir (Join-Path {{Quote(scratch)}} $d))" }
                "repo=$(Get-FfmpegPinKey -ReleaseDir {{Quote(ReleaseDir)}})"
                """, "powershell.exe");
            Assert.True(run.ExitCode == 0, run.Output);
            var keys = Lines(run.Output).Where(l => l.Contains('=', StringComparison.Ordinal))
                .ToDictionary(l => l[..l.IndexOf('=', StringComparison.Ordinal)], l => l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..]);

            Assert.Matches("^[0-9a-f]{64}$", keys["lf"]);
            Assert.Matches("^[0-9a-f]{64}$", keys["repo"]);
            Assert.Equal(keys["lf"], keys["crlf"]);
            Assert.NotEqual(keys["lf"], keys["script"]);
            Assert.NotEqual(keys["lf"], keys["inventory"]);
            Assert.NotEqual(keys["lf"], keys["added"]);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void RedistCache_RoundTrips_AndAnIncompleteEntryIsNoHit()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-release-cache-redist-").FullName;
        try
        {
            var key = new string('e', 64);
            WriteTree(scratch, "built/ffmpeg.exe", "exe bytes");
            WriteTree(scratch, "built/ffmpeg-LICENSE.txt", "LGPL");
            WriteTree(scratch, "built/ffmpeg-BUILDINFO.txt", "configure line");
            var run = RunPwsh(scratch, $$"""
                $env:DOTCHARTER_RELEASE_CACHE = {{Quote(Path.Combine(scratch, "cache"))}}
                Import-Module {{Quote(Module)}} -Force
                $files = @('ffmpeg.exe', 'ffmpeg-LICENSE.txt', 'ffmpeg-BUILDINFO.txt')
                "miss=$(Copy-RedistFromCache -Tool ffmpeg -Key '{{key}}' -Destination {{Quote(Path.Combine(scratch, "tree1"))}} -Files $files)"
                $dir = Save-RedistToCache -Tool ffmpeg -Key '{{key}}' -Source {{Quote(Path.Combine(scratch, "built"))}} -Files $files
                "dir=$dir"
                "hit=$(Copy-RedistFromCache -Tool ffmpeg -Key '{{key}}' -Destination {{Quote(Path.Combine(scratch, "tree2"))}} -Files $files)"
                Remove-Item (Join-Path $dir 'ffmpeg-BUILDINFO.txt')
                "partial=$(Copy-RedistFromCache -Tool ffmpeg -Key '{{key}}' -Destination {{Quote(Path.Combine(scratch, "tree3"))}} -Files $files)"
                try { Save-RedistToCache -Tool ffmpeg -Key ('{{key}}'.Replace('e', 'f')) -Source {{Quote(Path.Combine(scratch, "built"))}} -Files @('ffmpeg.exe', 'missing.txt') | Out-Null; 'saved-incomplete' }
                catch { 'refused-incomplete' }
                "left=$(@(Get-ChildItem (Split-Path $dir -Parent) -Name) -join ',')"
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            var lines = Lines(run.Output);

            Assert.Contains("miss=False", lines);
            Assert.Contains($"dir={Path.Combine(scratch, "cache", "redist", "ffmpeg", key)}", lines);
            Assert.Contains("hit=True", lines);
            Assert.Equal("configure line", File.ReadAllText(Path.Combine(scratch, "tree2", "ffmpeg-BUILDINFO.txt")));
            Assert.Contains("partial=False", lines);
            Assert.False(Directory.Exists(Path.Combine(scratch, "tree3")), "an incomplete entry copied something.");
            // A save that cannot copy every file leaves no entry, not even a partial folder.
            Assert.Contains("refused-incomplete", lines);
            Assert.Contains($"left={key}", lines);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    /// <summary>The record functions run in both shells: Merge-Train.ps1 is Windows PowerShell 5.1.</summary>
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public void GateRecords_MergePerTree_AndAreFoundOnlyForThatTree(string shell)
    {
        var scratch = Directory.CreateTempSubdirectory("dc-release-cache-gate-").FullName;
        try
        {
            var tree = new string('1', 40);
            var other = new string('2', 40);
            var output = RunShell(shell, $$"""
                $ErrorActionPreference = 'Stop'
                $env:DOTCHARTER_RELEASE_CACHE = {{Quote(Path.Combine(scratch, "cache"))}}
                Import-Module {{Quote(Module)}} -Force
                "none=$([bool](Find-GateRecord -Trees @('{{tree}}') -Kind gate))"
                [void](Save-GateRecord -Tree '{{tree}}' -Commit 'c1' -Gate -Source 'train A')
                "gate=$((Find-GateRecord -Trees @('{{tree}}') -Kind gate).Source)"
                "headless-before=$([bool](Find-GateRecord -Trees @('{{tree}}') -Kind headless))"
                [void](Save-GateRecord -Tree '{{tree}}' -Commit 'c1' -Headless -Source 'release B')
                "gate-kept=$((Find-GateRecord -Trees @('{{tree}}') -Kind gate).Source)"
                "headless=$((Find-GateRecord -Trees @('{{tree}}') -Kind headless).Source)"
                "other=$([bool](Find-GateRecord -Trees @('{{other}}') -Kind gate))"
                "second=$((Find-GateRecord -Trees @('{{other}}', '{{tree}}') -Kind gate).Tree)"
                try { [void](Save-GateRecord -Tree 'HEAD' -Gate -Source x); 'saved-bad-tree' } catch { 'refused-bad-tree' }
                "cultures-before=$([bool](Find-GateRecord -Trees @('{{tree}}') -Kind headlessCultures -Cultures 'de-DE'))"
                [void](Save-GateRecord -Tree '{{tree}}' -Commit 'c1' -HeadlessCultures @('de-DE') -Source 'release C')
                "de-only=$([bool](Find-GateRecord -Trees @('{{tree}}') -Kind headlessCultures -Cultures 'de-DE'))"
                "de-ja-partial=$([bool](Find-GateRecord -Trees @('{{tree}}') -Kind headlessCultures -Cultures @('de-DE', 'ja-JP')))"
                [void](Save-GateRecord -Tree '{{tree}}' -Commit 'c1' -HeadlessCultures @('ja-JP') -Source 'release D')
                "de-ja=$((Find-GateRecord -Trees @('{{tree}}') -Kind headlessCultures -Cultures @('de-DE', 'ja-JP')).Source)"
                "headless-kept=$((Find-GateRecord -Trees @('{{tree}}') -Kind headless).Source)"
                try { [void](Find-GateRecord -Trees @('{{tree}}') -Kind headlessCultures); 'found-no-cultures' } catch { 'refused-no-cultures' }
                """);
            if (output is null) return;
            var lines = Lines(output);

            Assert.Contains("none=False", lines);
            Assert.Contains("gate=train A", lines);
            Assert.Contains("headless-before=False", lines);
            Assert.Contains("gate-kept=train A", lines);
            Assert.Contains("headless=release B", lines);
            Assert.Contains("other=False", lines);
            Assert.Contains($"second={tree}", lines);
            Assert.Contains("refused-bad-tree", lines);
            // The layout sweeps are recorded per culture and found only when EVERY asked culture is on record.
            Assert.Contains("cultures-before=False", lines);
            Assert.Contains("de-only=True", lines);
            Assert.Contains("de-ja-partial=False", lines);
            Assert.Contains("de-ja=release D", lines);
            Assert.Contains("headless-kept=release B", lines);
            Assert.Contains("refused-no-cultures", lines);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void CleanTreeId_IsHeadsTree_OnlyWhileNothingIsChangedOrUntracked()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-release-cache-clean-").FullName;
        try
        {
            var repo = Path.Combine(scratch, "repo");
            Git(scratch, "init", "-q", "-b", "main", repo);
            WriteTree(repo, ".gitignore", "bin/\n");
            WriteTree(repo, "a.txt", "a\n");
            Commit(repo, "one", ".");
            var tree = Git(repo, "rev-parse", "HEAD^{tree}");
            string Id() => Lines(RunPwsh(scratch, $"Import-Module {Quote(Module)} -Force\n\"id=$(Get-CleanTreeId -Repo {Quote(repo)})\"").Output)
                .Single(l => l.StartsWith("id=", StringComparison.Ordinal))[3..];

            Assert.Equal(tree, Id());
            WriteTree(repo, "bin/out.dll", "build output");
            Assert.Equal(tree, Id());
            WriteTree(repo, "stray.cs", "class Stray {}");
            Assert.Equal("", Id());
            File.Delete(Path.Combine(repo, "stray.cs"));
            WriteTree(repo, "a.txt", "changed\n");
            Assert.Equal("", Id());
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    /// <summary>
    ///     New-Release.ps1 run from a scratch export finds the signing key beside the DEV
    ///     checkout, and the dev tree's records stand for the public tree it was cut into.
    /// </summary>
    [Fact]
    public void ExportOrigin_PointsTheLocalDirAndTheRecordsAtTheDevCheckout()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-release-cache-origin-").FullName;
        try
        {
            var dev = Path.Combine(scratch, "projects", "DotCharter");
            Git(scratch, "init", "-q", "-b", "main", dev);
            var export = Path.Combine(scratch, "export", "DotCharter");
            Directory.CreateDirectory(export);
            var devTree = new string('3', 40);
            var publicTree = new string('4', 40);
            var run = RunPowerShell(scratch, $$"""
                Import-Module {{Quote(Module)}} -Force
                "dev=$(Get-ReleaseLocalDir -RepoRoot {{Quote(dev)}})"
                [void](Write-ExportOrigin -RepoRoot {{Quote(export)}} -DevCheckout {{Quote(dev)}} -DevCommit c -DevTree '{{devTree}}' -PublicTree '{{publicTree}}')
                "unmarked=$(@(Get-GateRecordTrees -RepoRoot {{Quote(export)}} -Tree '{{publicTree}}') -join ',')"
                Set-Content -LiteralPath {{Quote(Path.Combine(scratch, "export", ".dotcharter-public-export"))}} -Value scratch
                "export=$(Get-ReleaseLocalDir -RepoRoot {{Quote(export)}})"
                "trees=$(@(Get-GateRecordTrees -RepoRoot {{Quote(export)}} -Tree '{{publicTree}}') -join ',')"
                "edited=$(@(Get-GateRecordTrees -RepoRoot {{Quote(export)}} -Tree '{{new string('5', 40)}}') -join ',')"
                """, "powershell.exe");
            Assert.True(run.ExitCode == 0, run.Output);
            var lines = Lines(run.Output);
            var local = Path.Combine(scratch, "projects", "DotCharter-local");

            Assert.Contains($"dev={local}", lines);
            // Without the export marker the origin file is not trusted.
            Assert.Contains($"unmarked={publicTree}", lines);
            Assert.Contains($"export={local}", lines);
            Assert.Contains($"trees={publicTree},{devTree}", lines);
            // A scratch tree edited after the export is not the dev tree's stand-in.
            Assert.Contains($"edited={new string('5', 40)}", lines);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    /// <summary>
    ///     The ffmpeg is looked for in the tree, then in the cache, and built only after
    ///     both; the cache's copy is held to the same version + inventory checks; a valid
    ///     ffmpeg fills the cache. yt-dlp is downloaded into the cache and the tree's copy
    ///     is held to the pin again.
    /// </summary>
    [Fact]
    public void NewRelease_TakesTheRedistToolsFromTheCacheBeforeBuilding_AndFillsIt()
    {
        var script = Script("New-Release.ps1");
        var start = IndexOf(script, "function Ensure-RedistTools");
        var body = script[start..IndexOf(script, "function Test-TonelabPin")];

        Assert.True(IndexOf(body, "Copy-RedistFromCache -Tool 'ffmpeg' -Key $ffmpegKey") < IndexOf(body, "& $FfmpegBuildScript -OutDir $RedistDir"),
            "the cache must be tried before the ~10 minute build.");
        Assert.Contains("(Copy-RedistFromCache -Tool 'ffmpeg' -Key $ffmpegKey -Destination $RedistDir -Files $FfmpegRedistFiles)) {\n            $state = Get-FfmpegState $ffmpeg",
            body, StringComparison.Ordinal);
        Assert.True(IndexOf(body, "& $FfmpegBuildScript -OutDir $RedistDir") < IndexOf(body, "Save-RedistToCache -Tool 'ffmpeg'"),
            "a built ffmpeg must fill the cache.");
        Assert.Contains("$ffmpegKey = Get-FfmpegPinKey -ReleaseDir $PSScriptRoot", body, StringComparison.Ordinal);
        Assert.Contains("Ok      = [bool]($line -and $line -match $FfmpegVersionPattern -and $missing.Count -eq 0)", script, StringComparison.Ordinal);
        Assert.Contains("$cachedYtDlp = Join-Path (Get-RedistCacheDir -Tool 'yt-dlp' -Key $YtDlpPin.sha256) 'yt-dlp.exe'", body, StringComparison.Ordinal);
        Assert.True(IndexOf(body, "Copy-Item -LiteralPath $cachedYtDlp -Destination $ytDlp -Force")
                    < IndexOf(body, "Save-PinnedFile -Urls @($YtDlpPin.url) -Dest $ytDlp -Sha256 $YtDlpPin.sha256"),
            "the tree's yt-dlp copy must be held to the pin after it is copied.");
    }

    /// <summary>
    ///     The release gates (default gate, headless en-US pass, localized layout sweeps,
    ///     release lane) each reuse a record of this CLEAN tree through the pure
    ///     Get-ReleaseGatePlan, build the tests only when one runs, record what they ran,
    ///     and run the sweeps of every culture side by side; all before the publishes.
    /// </summary>
    [Fact]
    public void NewRelease_Gates_ReuseRecordsOfThisTree_AndRecordWhatTheyRan()
    {
        var script = Script("New-Release.ps1");
        var gates = script[IndexOf(script, "function Invoke-ReleaseGates")..IndexOf(script, "if ($SelfTest) { Invoke-SelfTest; exit 0 }")];

        Assert.Contains("[switch]$ForceGate,", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$ForceHeadlessUi,", script, StringComparison.Ordinal);
        Assert.Contains("$tree = Get-CleanTreeId -Repo $RepoRoot", gates, StringComparison.Ordinal);
        Assert.Contains("Find-GateRecord -Trees $trees -Kind gate @scope\n", gates, StringComparison.Ordinal);
        Assert.Contains("Find-GateRecord -Trees $trees -Kind headless @scope\n", gates, StringComparison.Ordinal);
        Assert.Contains("Find-GateRecord -Trees $trees -Kind headlessCultures -Cultures $HeadlessSweepCultures", gates, StringComparison.Ordinal);
        Assert.Contains("Find-GateRecord -Trees $trees -Kind releaseLane", gates, StringComparison.Ordinal);
        Assert.Contains("-ForceGate:$ForceGate -ForceHeadless:$ForceHeadlessUi -ForceReleaseLane:$ForceReleaseLane -SkipHeadless:$SkipHeadlessUi", gates, StringComparison.Ordinal);
        Assert.Contains("if ($plan.NeedsTestBuild) { Build-ReleaseTestProject -ResultsDir $ResultsDir }", gates, StringComparison.Ordinal);
        Assert.Contains("& dotnet build $script:HeadlessTestProject", script, StringComparison.Ordinal);

        // Fail fast: the default gate first, then the headless suite, then the lane.
        var gate = IndexOf(gates, "Invoke-DefaultGate -Step $step['gate']");
        var headless = IndexOf(gates, "Invoke-HeadlessUiGate -ResultsDir $ResultsDir -MainStep");
        var lane = IndexOf(gates, "Invoke-ReleaseLaneGate -ResultsDir $ResultsDir -Step");
        Assert.True(gate < headless && headless < lane, "the gates run broadest-first.");

        // Each runs only when its plan step says so, and a green run is recorded.
        Assert.Contains("'-Gate', '-NoBuild'", gates, StringComparison.Ordinal);
        Assert.Contains("-Gate -Source 'New-Release.ps1 default gate'", gates, StringComparison.Ordinal);
        Assert.Contains("if ($Tree -and (Test-HeadlessGateRecordable -Step $MainStep -Unverified $mainUnverified)) {", gates, StringComparison.Ordinal);
        Assert.Contains("if ($Tree -and (Test-HeadlessGateRecordable -Step $SweepStep -Unverified $sweepUnverified)) {", gates, StringComparison.Ordinal);
        Assert.Contains("-HeadlessCultures $HeadlessSweepCultures -Source 'New-Release.ps1 layout sweeps'", gates, StringComparison.Ordinal);
        Assert.Contains("-ReleaseLane -Source 'New-Release.ps1 release lane'", gates, StringComparison.Ordinal);

        // The sweeps START together, share the shard budget, and are graded after ONE wait.
        var split = IndexOf(gates, "Get-CultureShardSplit -Shards $HeadlessShards -Cultures $HeadlessSweepCultures");
        var start = IndexOf(gates, "Start-HeadlessUiShardedRun -Filter $HeadlessLayoutFilter");
        var wait = IndexOf(gates, "$null = Wait-TestShards -Runs @($pending | ForEach-Object { $_.Runs })");
        var grade = IndexOf(gates, "-Run (Complete-HeadlessUiShardedRun $sweep)");
        Assert.True(split < start && start < wait && wait < grade, "the sweeps must start before the one shared wait.");
        // ONE wait, after the loop that starts every culture: a wait inside it would run them one after the other again.
        Assert.Contains("            })\n        $null = Wait-TestShards -Runs @($pending | ForEach-Object { $_.Runs })", gates.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(gates, "Wait-TestShards"));

        // Every gate runs before the publishes.
        Assert.True(IndexOf(script, "Invoke-ReleaseGates -ResultsDir (Join-Path $OutputDir 'tests')") < IndexOf(script, "# --- Clean Release build"),
            "the gates run before the clean release build.");
        Assert.Contains("$SigningKeyPath = Join-Path (Get-ReleaseLocalDir -RepoRoot $RepoRoot) 'release-signing/dotcharter-release-ed25519.key'", script, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The preflight runs cheapest first (git, then pinned hashes, then the checks that
    ///     build or restore, then the ones that may rebuild or download), and all of it
    ///     before the first gate: a stale pin costs seconds, not a gate run (v1.4.0 lost
    ///     a run to the licence inventory).
    /// </summary>
    [Fact]
    public void NewRelease_Preflight_RunsCheapestFirst_AndBeforeAnyGate()
    {
        var script = Script("New-Release.ps1");
        var main = script[IndexOf(script, "Write-Step 'Preflight checks'")..];
        string[] order =
        [
            "Find-UnimportedPublicCommits.ps1') -DevRepo",
            "Test-JuceSharpVersionBump.ps1') -DevRepo",
            "$tonelabCommit = Test-TonelabPin",
            "$vcSource = Install-VcRedist",
            "$signingPublicKey = (& dotnet run $SignTool pubkey $SigningKeyPath",
            "Update-DependencyInventory.ps1') -Check -Restore",
            "Initialize-VoicePacks\n",
            "$redist = Ensure-RedistTools",
            "Write-Ok 'preflight done'",
            "Invoke-ReleaseGates -ResultsDir",
        ];
        var at = order.Select(n => IndexOf(main, n)).ToArray();
        for (var i = 1; i < at.Length; i++)
            Assert.True(at[i - 1] < at[i], $"`{order[i - 1]}` must come before `{order[i]}`.");
    }

    /// <summary>The merge train records only a GREEN full-gate train, after the verdict.</summary>
    [Fact]
    public void MergeTrain_RecordsOnlyAGreenFullGateTrain()
    {
        var train = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "Merge-Train.ps1"));

        var red = IndexOf(train, "if ($verdict -ne 'GREEN') {");
        var record = IndexOf(train, "$recordPath = Save-GateRecord @recordArgs");
        Assert.True(red < record && record < IndexOf(train, "# ---- 4. merge in the tested order"), "the record must follow the red-train exit.");
        Assert.Contains("if (-not $docsOnly) {\n        Import-Module (Join-Path $PSScriptRoot '..\\release\\ReleaseCache.psm1') -Force", train, StringComparison.Ordinal);
        Assert.Contains("Gate = ($executedGreenLanes -contains 'gate'); Headless = ($executedGreenLanes -contains 'headless');", train, StringComparison.Ordinal);
        Assert.Contains("$trainTree = Get-CleanTreeId -Repo $wt", train, StringComparison.Ordinal);
    }

    private static int IndexOf(string text, string needle)
    {
        var at = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"`{needle}` is gone.");
        return at;
    }

    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

    // Runs $script under pwsh or Windows PowerShell; null (and a skip) when that shell is missing.
    private static string? RunShell(string shell, string script)
    {
        if (shell == "pwsh")
        {
            var run = RunPwsh(Path.GetTempPath(), script);
            Assert.True(run.ExitCode == 0, run.Output);
            return run.Output;
        }

        var file = Path.Combine(Directory.CreateTempSubdirectory("dc-release-cache-ps5-").FullName, "case.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var info = new ProcessStartInfo(shell);
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
            info.ArgumentList.Add(argument);
        try
        {
            // #608: both pipes drain concurrently under one cap (a deadlock detector for a hung child, never a budget).
            var result = BoundedProcess.TryRun(info, TimeSpan.FromMinutes(2), shell + " (release cache)");
            if (result is null)
            {
                Assert.Skip($"{shell} is not on PATH.");
                return null;
            }

            Assert.True(result.ExitCode == 0, $"{shell} exited {result.ExitCode}.{Environment.NewLine}{result.StandardError}{Environment.NewLine}{result.StandardOutput}");
            return result.StandardOutput;
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }
}
