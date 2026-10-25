namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The release tooling's guards against the v1.2.0 release's three surprises: a public and
///     a plugin pack of one version never share a folder; the export's kept scratch tree fetches
///     the previous public snapshot without recursing into its JuceSharp checkout and parks its
///     build outputs even when one output sits inside another; and a JuceSharp tree that changed
///     since its public snapshot cannot be released under the same <c>&lt;Version&gt;</c>.
/// </summary>
public sealed partial class PublicSnapshotExportTests
{
    private static string ReleaseVersionModule => Path.Combine(Root, "tools", "release", "ReleaseVersion.psm1");
    private static string VersionBumpScript => Path.Combine(Root, "tools", "release", "Test-JuceSharpVersionBump.ps1");

    [Fact]
    public void ReleaseOutput_EachModeHasItsFolder_AndAFolderHoldingTheOtherModesPackIsRefused()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-release-out-").FullName;
        try
        {
            var shared = Path.Combine(scratch, "shared");
            var unmarked = Path.Combine(scratch, "unmarked");
            Directory.CreateDirectory(shared);
            WriteTree(unmarked, "DotCharter-v1.2.0-win-x64.zip", "an older pack\n");
            var run = RunPwsh(scratch, $$"""
                Import-Module {{Quote(ReleaseVersionModule)}} -Force
                "public=$(Get-ReleaseOutputDir -RepoRoot 'R' -Version '1.2.0')"
                "full=$(Get-ReleaseOutputDir -RepoRoot 'R' -Version '1.2.0' -WithPlugins)"
                Set-ReleaseOutputMode -OutputDir {{Quote(shared)}}
                Set-ReleaseOutputMode -OutputDir {{Quote(shared)}}
                "same mode again: ok"
                try { Set-ReleaseOutputMode -OutputDir {{Quote(shared)}} -WithPlugins; 'full over public: WRITTEN' } catch { "full over public: $($_.Exception.Message)" }
                try { Set-ReleaseOutputMode -OutputDir {{Quote(unmarked)}}; 'unmarked: WRITTEN' } catch { "unmarked: $($_.Exception.Message)" }
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("public=" + Path.Combine("R", "artifacts", "release", "v1.2.0-public"), run.Output, StringComparison.Ordinal);
            Assert.Contains("full=" + Path.Combine("R", "artifacts", "release", "v1.2.0-full"), run.Output, StringComparison.Ordinal);
            Assert.Contains("same mode again: ok", run.Output, StringComparison.Ordinal);
            Assert.Contains("full over public: " + shared + " holds a public pack; refusing to write a full pack over it.", run.Output, StringComparison.Ordinal);
            Assert.Contains("unmarked: " + unmarked + " holds a pack with no .release-mode marker", run.Output, StringComparison.Ordinal);
            Assert.Equal("public", File.ReadAllText(Path.Combine(shared, ".release-mode")).Trim());
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void ReleaseScript_DefaultsToTheModesFolder_AndMarksIt()
    {
        var script = File.ReadAllText(Path.Combine(Root, "tools", "release", "New-Release.ps1"));
        Assert.Contains("$OutputDir = Get-ReleaseOutputDir -RepoRoot $RepoRoot -Version $Version -WithPlugins:$WithPlugins", script, StringComparison.Ordinal);
        Assert.Contains("Set-ReleaseOutputMode -OutputDir $OutputDir -WithPlugins:$WithPlugins", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifacts/release/v$Version\"", script, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The kept scratch tree's JuceSharp is a plain checkout of a submodule whose objects
    ///     are not there. A fetch of public commits that move its gitlink recursed into it on
    ///     demand and died with "Could not access submodule 'JuceSharp'".
    /// </summary>
    [Fact]
    public void ScratchFetch_NeverRecursesIntoTheKeptJuceSharp()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-fetch-").FullName;
        try
        {
            const string modules = "[submodule \"JuceSharp\"]\n\tpath = JuceSharp\n\turl = ../nowhere.git\n";
            var pub = Path.Combine(scratch, "pub");
            Git(scratch, "init", "-q", "-b", "main", pub);
            WriteTree(pub, "README.md", "older\n");
            Commit(pub, "older snapshot", "README.md");
            // The gitlink CHANGES in a fetched commit: that is what an on-demand recursion follows.
            WriteTree(pub, ".gitmodules", modules);
            Git(pub, "update-index", "--add", "--cacheinfo", "160000,1111111111111111111111111111111111111111,JuceSharp");
            Commit(pub, "public snapshot", ".gitmodules");

            string KeptTree(string name)
            {
                var tree = Path.Combine(scratch, name);
                Git(scratch, "init", "-q", "-b", "main", tree);
                WriteTree(tree, ".gitmodules", modules);
                WriteTree(tree, "JuceSharp/src/f.txt", "a plain checkout\n");
                Git(tree, "config", "submodule.JuceSharp.url", "../nowhere.git");
                Git(tree, "update-index", "--add", "--cacheinfo", "160000,2222222222222222222222222222222222222222,JuceSharp");
                Commit(tree, "kept tree", ".gitmodules");
                return tree;
            }

            // Non-vacuity, in a tree of its own (once a fetch has the objects there is nothing
            // left to recurse for): a plain fetch of the same commits fails as the release did.
            var plain = RunPwsh(scratch, $"git -C {Quote(KeptTree("plain"))} fetch -q --no-tags {Quote(pub)} '+refs/heads/main:refs/remotes/plain/main' 2>&1; \"exit=$LASTEXITCODE\"");
            Assert.Contains("Could not access submodule 'JuceSharp'", plain.Output, StringComparison.Ordinal);
            var kept = KeptTree("kept");

            var run = RunPwsh(scratch, $"Import-Module {Quote(Module)} -Force\nGet-PreviousSnapshot -Repo {Quote(kept)} -Url {Quote(pub)}");
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains(Git(pub, "rev-parse", "main"), run.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    /// <summary>
    ///     Two views of one work tree: the root's lists <c>sub/bin/</c>, the sub view's lists
    ///     <c>bin/</c> inside it. The root's move takes the sub view's path with it, and the second
    ///     move found nothing to move; parking skips it and both come back.
    /// </summary>
    [Fact]
    public void Parking_SkipsAnOutputThatMovedWithItsParent()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-park-").FullName;
        try
        {
            var root = Path.Combine(scratch, "tree");
            Git(scratch, "init", "-q", "-b", "main", root);
            WriteTree(root, ".gitignore", "sub/bin/\n");
            Commit(root, "root", ".gitignore");
            var subGit = Path.Combine(scratch, "sub.git");
            Git(scratch, "init", "-q", "--bare", subGit);
            WriteTree(root, "sub/.gitignore", "bin/\n");
            WriteTree(root, "sub/bin/native/juce_sharp.dll", "not a real binary\n");
            var cache = Path.Combine(scratch, "cache");

            var run = RunPwsh(scratch, $$"""
                Import-Module {{Quote(Module)}} -Force
                $trees = @(
                    @{ Prefix = ''; Git = @('-C', {{Quote(root)}}); Environment = @{} },
                    @{ Prefix = 'sub/'; Git = @("--git-dir={{subGit}}", "--work-tree={{Path.Combine(root, "sub")}}"); Environment = @{} })
                $parked = Move-SnapshotBuildOutputs -Root {{Quote(root)}} -Cache {{Quote(cache)}} -Trees $trees
                "parked=$($parked -join ',')"
                $restored = Restore-SnapshotBuildOutputs -Root {{Quote(root)}} -Cache {{Quote(cache)}}
                "restored=$($restored -join ',')"
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("parked=sub/bin", run.Output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, "sub", "bin", "native", "juce_sharp.dll")), run.Output);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    private static void ReleasePreflight_RefusesAChangedJuceSharpUnderItsPublicVersion()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-juce-version-").FullName;
        try
        {
            var f = NewPushFixture(scratch);
            var pushed = RunPush(scratch, f, "p1");
            Assert.True(pushed.ExitCode == 0, pushed.Output);

            var unchanged = RunVersionCheck(scratch, f);
            Assert.True(unchanged.ExitCode == 0, unchanged.Output);
            Assert.Contains("JuceSharp 9.8.7: the version moves with the tree", unchanged.Output, StringComparison.Ordinal);

            // JuceSharp moves on, its version does not.
            WriteTree(f.DevJuce, "README.md", "Issues: the tracker, and more\n");
            Commit(f.DevJuce, "native work 2", "README.md");
            PointDevAtJuceSharp(f);
            var stale = RunVersionCheck(scratch, f);
            Assert.Equal(1, stale.ExitCode);
            Assert.Contains("JuceSharp changed since its last public snapshot", stale.Output, StringComparison.Ordinal);
            Assert.Contains("its <Version> is still 9.8.7: bump it in JuceSharp/Directory.Build.props first", stale.Output, StringComparison.Ordinal);

            WriteTree(f.DevJuce, "Directory.Build.props", Csproj("9.8.8"));
            Commit(f.DevJuce, "bump", "Directory.Build.props");
            PointDevAtJuceSharp(f);
            var bumped = RunVersionCheck(scratch, f);
            Assert.True(bumped.ExitCode == 0, bumped.Output);
            Assert.Contains("JuceSharp 9.8.8: the version moves with the tree", bumped.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void ReleaseScript_RunsTheVersionCheckInPreflight() =>
        Assert.Contains("Test-JuceSharpVersionBump.ps1') -DevRepo $RepoRoot -DevRef HEAD",
            File.ReadAllText(Path.Combine(Root, "tools", "release", "New-Release.ps1")), StringComparison.Ordinal);

    private static void PointDevAtJuceSharp(PushFixture f)
    {
        Git(f.DevDc, "update-index", "--cacheinfo", $"160000,{Git(f.DevJuce, "rev-parse", "HEAD")},JuceSharp");
        Commit(f.DevDc, "bump JuceSharp");
    }

    private static (int ExitCode, string Output) RunVersionCheck(string scratch, PushFixture f) => RunPwsh(scratch, $"""
        & {Quote(VersionBumpScript)} -DevRepo {Quote(f.DevDc)} -DevRef main -DevJuceSharpGitDir {Quote(Path.Combine(f.DevJuce, ".git"))} -PublicJuceSharpUrl {Quote(f.PublicJuce)}
        exit $LASTEXITCODE
        """);
}
