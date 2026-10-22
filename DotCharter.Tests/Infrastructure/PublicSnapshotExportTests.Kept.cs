namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The export's speed-ups, on the <c>-Push</c> fixture repos (dry runs, no URLs): the
///     scratch DotCharter work tree is KEPT between runs and updated like a checkout, its
///     build outputs are parked outside it while the leak check and gitleaks read it (so
///     both scans see the tracked tree only) and restored for the incremental build; and
///     <c>-Gate</c> reuses a green gate on record for the snapshot tree or its dev tree,
///     still BUILDING the snapshot, unless <c>-ForceGate</c>.
/// </summary>
public sealed partial class PublicSnapshotExportTests
{
    private static void Export_KeepsItsWorkTree_AndParksTheBuildOutputsWhileTheScansRun()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-kept-").FullName;
        try
        {
            var f = NewPushFixture(scratch);
            WriteTree(f.DevJuce, ".gitignore", "bin/\n");
            Commit(f.DevJuce, "ignore bin", ".gitignore");
            WriteTree(f.DevDc, ".gitignore", "bin/\nobj/\n");
            WriteTree(f.DevDc, "docs/stay.md", "Unchanged.\n");
            WriteTree(f.DevDc, "docs/gone.md", "Deleted next.\n");
            Git(f.DevDc, "update-index", "--cacheinfo", $"160000,{Git(f.DevJuce, "rev-parse", "HEAD")},JuceSharp");
            Commit(f.DevDc, "docs", ".gitignore", "docs");

            var first = RunDry(scratch, f, "k");
            Assert.True(first.ExitCode == 0, first.Output);
            Assert.Contains("scratch: fresh work tree", first.Output, StringComparison.Ordinal);
            var tree = Path.Combine(scratch, "k", "DotCharter");

            // What a public build leaves behind, each carrying a name the leak check reports.
            var leak = $"clone SomeeName/DotCharter{Dev}.git\n";
            WriteTree(tree, "DotCharter.UI/bin/Debug/leak.txt", leak);
            WriteTree(tree, "DotCharter.UI/obj/leak.txt", leak);
            WriteTree(tree, "JuceSharp/bin/leak.txt", leak);
            var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(tree, "docs", "stay.md"), old);

            // Non-vacuity: read as it stands, that tree IS a leak finding.
            var unparked = RunPwsh(scratch, $"Import-Module {Quote(Module)} -Force\nforeach ($x in Find-PublicTreeLeaks -Root {Quote(tree)}) {{ \"$($x.Rule)|$($x.Path)\" }}");
            Assert.True(unparked.ExitCode == 0, unparked.Output);
            Assert.Contains("dev-repo|DotCharter.UI/bin/Debug/leak.txt", unparked.Output, StringComparison.Ordinal);
            Assert.Contains("dev-repo|JuceSharp/bin/leak.txt", unparked.Output, StringComparison.Ordinal);

            // Dev moves on: one file changes, one is deleted.
            WriteTree(f.DevDc, "README.md", "Changed.\n");
            Git(f.DevDc, "rm", "-q", "docs/gone.md");
            Commit(f.DevDc, "move on", "README.md");

            var second = RunDry(scratch, f, "k");
            Assert.True(second.ExitCode == 0, second.Output);
            Assert.Contains("leak check: clean", second.Output, StringComparison.Ordinal);
            Assert.Contains("scratch: work tree kept from the last run, 3 build output path(s) parked during the scans and restored", second.Output, StringComparison.Ordinal);
            foreach (var output in new[] { "DotCharter.UI/bin/Debug/leak.txt", "DotCharter.UI/obj/leak.txt", "JuceSharp/bin/leak.txt" })
                Assert.True(File.Exists(Path.Combine(tree, output)), $"{output} was not restored.\n{second.Output}");
            Assert.False(Directory.Exists(Path.Combine(scratch, "k", "build-cache")), "the parking folder outlived the run.");
            // Updated like a checkout: the unchanged file was not rewritten, the rest followed dev.
            Assert.Equal(old, File.GetLastWriteTimeUtc(Path.Combine(tree, "docs", "stay.md")));
            Assert.Equal("Changed.\n", File.ReadAllText(Path.Combine(tree, "README.md")));
            Assert.False(File.Exists(Path.Combine(tree, "docs", "gone.md")), "a file dev deleted survived in the kept tree.");
            Assert.Equal(Git(tree, "rev-parse", "main"), Git(tree, "rev-parse", "HEAD"));

            // -Clean starts from nothing.
            var clean = RunDry(scratch, f, "k", "-Clean");
            Assert.True(clean.ExitCode == 0, clean.Output);
            Assert.Contains("scratch: fresh work tree", clean.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(tree, "DotCharter.UI", "bin", "Debug", "leak.txt")));
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void ScanGuard_RefusesAWorkTreeHoldingAnUntrackedPath()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-guard-").FullName;
        try
        {
            var repo = Path.Combine(scratch, "repo");
            Git(scratch, "init", "-q", "-b", "main", repo);
            WriteTree(repo, ".gitignore", "bin/\n");
            WriteTree(repo, "a.txt", "a\n");
            Commit(repo, "one", ".");
            string Check() => RunPwsh(scratch, $$"""
                Import-Module {{Quote(Module)}} -Force
                try { Assert-SnapshotTracksEverything -Trees @(@{ Prefix = ''; Git = @('-C', {{Quote(repo)}}); Environment = @{} }); 'clean' }
                catch { $_.Exception.Message }
                """).Output;

            Assert.Contains("clean", Check(), StringComparison.Ordinal);
            WriteTree(repo, "bin/out.dll", "ignored, but not tracked");
            Assert.Contains("Refusing to leak-check a work tree holding 1 untracked path(s)", Check(), StringComparison.Ordinal);
            File.Delete(Path.Combine(repo, "bin", "out.dll"));
            WriteTree(repo, "stray.txt", "not ignored");
            Assert.Contains("stray.txt", Check(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    private static void Gate_ReusesAGreenGateOnRecord_StillBuilds_AndForceGateRunsIt()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-gate-").FullName;
        try
        {
            var f = NewPushFixture(scratch);
            var devTree = Git(f.DevDc, "rev-parse", "main^{tree}");

            // No record: the gate runs, and its green run is recorded for the snapshot tree.
            var fresh = Path.Combine(scratch, "cache-fresh");
            var first = RunDry(scratch, f, "g", "-Gate", fresh);
            Assert.True(first.ExitCode == 0, first.Output);
            Assert.Equal("ran -Gate", File.ReadAllText(f.GateMarker).Trim());
            var publicTree = Git(Path.Combine(scratch, "g", "DotCharter"), "rev-parse", "main^{tree}");
            Assert.Contains($"; recorded for tree {publicTree}", first.Output, StringComparison.Ordinal);
            // ...so the same snapshot again only builds.
            var again = RunDry(scratch, f, "g", "-Gate", fresh);
            Assert.True(again.ExitCode == 0, again.Output);
            Assert.Equal("ran -Build", File.ReadAllText(f.GateMarker).Trim());
            Assert.Contains($"the gate was NOT re-run: reusing the green gate recorded for tree {publicTree}", again.Output, StringComparison.Ordinal);

            // The dev tree on record (the merge train's): the snapshot is built, not re-tested.
            var train = Path.Combine(scratch, "cache-train");
            var save = RunPwsh(scratch, $"$env:DOTCHARTER_RELEASE_CACHE = {Quote(train)}\nImport-Module {Quote(CacheModule)} -Force\n[void](Save-GateRecord -Tree '{devTree}' -Commit 'abc' -Gate -Source 'Merge-Train.ps1 (1, 2)')");
            Assert.True(save.ExitCode == 0, save.Output);
            var reused = RunDry(scratch, f, "g", "-Gate", train);
            Assert.True(reused.ExitCode == 0, reused.Output);
            Assert.Equal("ran -Build", File.ReadAllText(f.GateMarker).Trim());
            Assert.Contains($"build: Invoke-Gate.ps1 -Public -Build exited 0; the gate was NOT re-run: reusing the green gate recorded for tree {devTree} (commit abc), Merge-Train.ps1 (1, 2)",
                reused.Output, StringComparison.Ordinal);
            Assert.Contains("(-ForceGate re-runs it)", reused.Output, StringComparison.Ordinal);

            var forced = RunDry(scratch, f, "g", "-Gate -ForceGate", train);
            Assert.True(forced.ExitCode == 0, forced.Output);
            Assert.Equal("ran -Gate", File.ReadAllText(f.GateMarker).Trim());
            Assert.DoesNotContain("NOT re-run", forced.Output, StringComparison.Ordinal);

            // A red gate records nothing.
            var red = Path.Combine(scratch, "cache-red");
            var failed = RunDry(scratch, f, "g", "-Gate", red, gateExit: 1);
            Assert.Contains("build: Invoke-Gate.ps1 -Public -Gate exited 1", failed.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(red, "gate-results")), "a red gate was recorded.\n" + failed.Output);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    private static string CacheModule => Path.Combine(Root, "tools", "release", "ReleaseCache.psm1");

    // A dry run (no URLs, no push) of the fixture into <scratch>/<root>, with its own release cache.
    private static (int ExitCode, string Output) RunDry(string scratch, PushFixture f, string root, string extra = "",
        string? cache = null, int gateExit = 0)
    {
        if (File.Exists(f.GateMarker)) File.Delete(f.GateMarker);
        var cacheLine = cache is null ? "" : $"$env:DOTCHARTER_RELEASE_CACHE = {Quote(cache)}\n";
        return RunPwsh(scratch, $$"""
            {{cacheLine}}$env:DC_FAKE_GATE_MARKER = {{Quote(f.GateMarker)}}
            $env:DC_FAKE_GATE_EXIT = '{{gateExit}}'
            $env:DC_FAKE_GATE_MOVE = ''
            & {{Quote(ExportScript)}} -DevRepo {{Quote(f.DevDc)}} -DevRef main -DevJuceSharpGitDir {{Quote(Path.Combine(f.DevJuce, ".git"))}} -ScratchRoot {{Quote(Path.Combine(scratch, root))}} {{extra}}
            exit $LASTEXITCODE
            """);
    }
}
