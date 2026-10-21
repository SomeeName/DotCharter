using static DotCharter.Tests.Infrastructure.PublicSnapshotExportTests;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The public → dev sync guard (<c>tools/release/PublicSync.psm1</c>, #77): a commit merged
///     on a public repo must be imported into dev (a <c>Public-Commit:</c> trailer) before the
///     next snapshot, or <c>Export-PublicSnapshot.ps1</c> refuses — dry run included — because
///     the snapshot is the dev tree and would overwrite it. <c>Import-PublicCommit.ps1</c> does
///     the import with the reverse rename map and <c>git apply -3</c>. Fixture repos under pwsh,
///     like <see cref="PublicSnapshotExportTests" />; private names assembled from pieces.
/// </summary>
public sealed class PublicSyncGuardTests
{
    private const string Dev = "-" + "Dev";
    private static string Root => TestPaths.RepoRoot();
    private static string Release(string file) => Path.Combine(Root, "tools", "release", file);

    [Fact]
    public void Export_RefusesWhileAPublicCommitHasNoDevTrailer_AndPassesOnceEachHasOne()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-sync-").FullName;
        try
        {
            var (devDc, devJuce) = NewDevRepos(scratch);
            var emptyDc = Path.Combine(scratch, "empty-dc.git");
            var emptyJuce = Path.Combine(scratch, "empty-juce.git");
            Git(scratch, "init", "-q", "--bare", emptyDc);
            Git(scratch, "init", "-q", "--bare", emptyJuce);
            var first = RunExport(scratch, devDc, devJuce, "s1", emptyDc, emptyJuce);
            Assert.True(first.ExitCode == 0, first.Output);
            Assert.Contains("public sync guard: not run (no public URL, so no previous snapshot)", first.Output, StringComparison.Ordinal);

            // The public repos after release 1 (clones serve as the public URLs), then a contributor's commit on each. The JuceSharp
            // one even borrows the snapshot message: only the exporter's identity makes a snapshot.
            var publicDc = Path.Combine(scratch, "public-dc");
            var publicJuce = Path.Combine(scratch, "public-juce");
            Git(scratch, "clone", "-q", Path.Combine(scratch, "s1", "DotCharter"), publicDc);
            Git(scratch, "clone", "-q", Path.Combine(scratch, "s1", "JuceSharp.git"), publicJuce);
            WriteTree(publicDc, "CONTRIBUTING.md", "Be nice.\n");
            var dcContribution = Contribute(publicDc, "Add a contributing guide", "CONTRIBUTING.md");
            WriteTree(publicJuce, "NOTES.md", "notes\n");
            var juceContribution = Contribute(publicJuce, "Update to v9.9.9", "NOTES.md");

            var refused = RunExport(scratch, devDc, devJuce, "s2", publicDc, publicJuce);
            Assert.True(refused.ExitCode != 0, refused.Output);
            Assert.Contains("public sync guard: 2 public commit(s)", refused.Output, StringComparison.Ordinal);
            Assert.Contains($"Import-PublicCommit.ps1 -Repo DotCharter -Commit {dcContribution}", refused.Output, StringComparison.Ordinal);
            Assert.Contains($"Import-PublicCommit.ps1 -Repo JuceSharp -Commit {juceContribution}", refused.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(scratch, "s2", "DotCharter", "README.md")), "a refused export checked out a snapshot");

            // The watch's script sees the same two, from public data and the dev history alone.
            var watch = RunPwsh(scratch, $"& {Quote(Release("Find-UnimportedPublicCommits.ps1"))} -DevRepo {Quote(devDc)} -DevRef main " +
                                         $"-PublicDotCharterUrl {Quote(publicDc)} -PublicJuceSharpUrl {Quote(publicJuce)}; exit $LASTEXITCODE");
            Assert.True(watch.ExitCode == 0, watch.Output);
            Assert.Contains("JuceSharp: 1 public commit(s) not imported", watch.Output, StringComparison.Ordinal);
            Assert.Contains("DotCharter: 1 public commit(s) not imported", watch.Output, StringComparison.Ordinal);

            // A trailer for DotCharter, and a mere MENTION of the JuceSharp one in prose: only a
            // real trailer counts, so JuceSharp is still missing.
            CommitEmpty(devDc, $"import the guide\n\nPublic-Commit: JuceSharp@{juceContribution} is next.\n\nPublic-Commit: DotCharter@{dcContribution}");
            var half = RunExport(scratch, devDc, devJuce, "s3", publicDc, publicJuce);
            Assert.True(half.ExitCode != 0, half.Output);
            Assert.Contains("public sync guard: 1 public commit(s)", half.Output, StringComparison.Ordinal);
            Assert.Contains($"JuceSharp@{juceContribution}", half.Output, StringComparison.Ordinal);
            Assert.DoesNotContain($"DotCharter@{dcContribution}", half.Output, StringComparison.Ordinal);

            // The release preflight (New-Release.ps1) runs the same check with -FailOnMissing: a
            // non-zero exit while one is missing.
            var preflight = RunFinder(scratch, devDc, devJuce, publicDc, publicJuce, "-FailOnMissing");
            Assert.True(preflight.ExitCode == 1, preflight.Output);
            Assert.Contains($"JuceSharp@{juceContribution}", preflight.Output, StringComparison.Ordinal);

            // The JuceSharp trailer in the dev JuceSharp history (the gitlink's) counts too. Both
            // versions move on: their tags are public already (the push preflight, #83).
            WriteTree(devJuce, "Directory.Build.props", Csproj("9.8.8"));
            Commit(devJuce, $"skip the notes\n\nPublic-Commit: JuceSharp@{juceContribution}", "Directory.Build.props");
            Git(devDc, "update-index", "--cacheinfo", $"160000,{Git(devJuce, "rev-parse", "HEAD")},JuceSharp");
            WriteTree(devDc, "DotCharter.UI/DotCharter.UI.csproj", Csproj("1.2.4"));
            Commit(devDc, "bump JuceSharp", "DotCharter.UI/DotCharter.UI.csproj");
            var passed = RunExport(scratch, devDc, devJuce, "s4", publicDc, publicJuce);
            Assert.True(passed.ExitCode == 0, passed.Output);
            Assert.Contains("public sync guard: every public commit since the last snapshot is in dev", passed.Output, StringComparison.Ordinal);
            // The local check reads the dev JuceSharp history like the export, so it passes too.
            var preflightPassed = RunFinder(scratch, devDc, devJuce, publicDc, publicJuce, "-FailOnMissing");
            Assert.True(preflightPassed.ExitCode == 0, preflightPassed.Output);
            Assert.Contains("JuceSharp: 0 public commit(s) not imported", preflightPassed.Output, StringComparison.Ordinal);
            // The new snapshot sits on the contributor's commit, so the NEXT export starts after it.
            Assert.Equal(dcContribution, Git(Path.Combine(scratch, "s4", "DotCharter"), "log", "-1", "--format=%P", "main"));
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void Import_AppliesAPublicCommit_ThroughTheReverseRenameMap_WithTrailers()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-import-").FullName;
        try
        {
            var (devDc, _) = NewDevRepos(scratch);
            // Dev moved on since the snapshot in a line the public commit does not touch.
            WriteTree(devDc, "docs/guide.md", $"# Guide\nClone SomeeName/DotCharter{Dev} first.\nStep one.\nStep two.\nDev-only tail.\n");
            Commit(devDc, "dev guide", "docs/guide.md");

            // The public repo: the snapshot (renamed, without the dev tail), then a contributor's
            // fix to the line right under the renamed one.
            var publicDc = Path.Combine(scratch, "public-dc");
            Git(scratch, "init", "-q", "-b", "main", publicDc);
            WriteTree(publicDc, "docs/guide.md", "# Guide\nClone SomeeName/DotCharter first.\nStep one.\nStep two.\n");
            Git(publicDc, "add", "--", ".");
            Git(publicDc, "-c", "user.name=Owner Name", "-c", "user.email=owner@example.com", "commit", "-q", "-m", "Initial public commit of DotCharter");
            WriteTree(publicDc, "docs/guide.md", "# Guide\nClone SomeeName/DotCharter first.\nStep 1, fixed.\nStep two.\n");
            var contribution = Contribute(publicDc, "Fix step one", "docs/guide.md");

            var run = RunPwsh(scratch, $"& {Quote(Release("Import-PublicCommit.ps1"))} -Repo DotCharter -Commit {contribution} " +
                                       $"-DevRepo {Quote(devDc)} -Base main -PublicUrl {Quote(publicDc)} -NoPullRequest; exit $LASTEXITCODE");
            Assert.True(run.ExitCode == 0, run.Output);

            Assert.Equal($"import/public-dotcharter-{contribution[..12]}", Git(devDc, "branch", "--show-current"));
            // The dev name survives, the dev tail survives, the public fix lands.
            Assert.Equal($"# Guide\nClone SomeeName/DotCharter{Dev} first.\nStep 1, fixed.\nStep two.\nDev-only tail.\n",
                File.ReadAllText(Path.Combine(devDc, "docs", "guide.md")).Replace("\r\n", "\n"));
            // Committed, nothing left over (the fixture's gitlink is never checked out, so it is ignored).
            Assert.Equal("", Git(devDc, "status", "--porcelain", "--ignore-submodules=all"));
            Assert.Equal($"DotCharter@{contribution}", Git(devDc, "log", "-1", "--format=%(trailers:key=Public-Commit,valueonly)"));
            Assert.Equal("Contributor <contributor@example.com>", Git(devDc, "log", "-1", "--format=%(trailers:key=Co-authored-by,valueonly)"));
            Assert.StartsWith($"Import public DotCharter commit {contribution[..12]}: Fix step one", Git(devDc, "log", "-1", "--format=%s"), StringComparison.Ordinal);

            // …and the guard is satisfied by that branch.
            var noJuce = Path.Combine(scratch, "none.git");
            Git(scratch, "init", "-q", "--bare", noJuce);
            var check = RunPwsh(scratch, $"& {Quote(Release("Find-UnimportedPublicCommits.ps1"))} -DevRepo {Quote(devDc)} -DevRef HEAD " +
                                         $"-PublicDotCharterUrl {Quote(publicDc)} -PublicJuceSharpUrl {Quote(noJuce)}; exit $LASTEXITCODE");
            Assert.True(check.ExitCode == 0, check.Output);
            Assert.Contains("DotCharter: 0 public commit(s) not imported", check.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void Watch_And_Import_RecogniseASnapshotByTheOneSnapshotIdentity()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-identity-").FullName;
        try
        {
            var (devDc, _) = NewDevRepos(scratch);
            var identity = SnapshotIdentity();
            // Public history: a root snapshot by someone else, a contribution, then a snapshot by
            // the configured identity that is NOT a root commit. Only the shared identity
            // definition (public-snapshot-identity.json) makes that last one a snapshot.
            var publicDc = Path.Combine(scratch, "public-dc");
            Git(scratch, "init", "-q", "-b", "main", publicDc);
            WriteTree(publicDc, "README.md", "one\n");
            Git(publicDc, "add", "--", ".");
            Git(publicDc, "-c", "user.name=Earlier Owner", "-c", "user.email=earlier@example.com", "commit", "-q", "-m", "Initial public commit of DotCharter");
            WriteTree(publicDc, "README.md", "two\n");
            Contribute(publicDc, "Contribution", "README.md");
            WriteTree(publicDc, "README.md", "three\n");
            Git(publicDc, "add", "--", "README.md");
            Git(publicDc, "-c", $"user.name={identity.Name}", "-c", $"user.email={identity.Email}", "commit", "-q", "-m", "Update to v2.0.0");
            var snapshot = Git(publicDc, "rev-parse", "HEAD");

            var noJuce = Path.Combine(scratch, "none.git");
            Git(scratch, "init", "-q", "--bare", noJuce);
            var watch = RunPwsh(scratch, $"& {Quote(Release("Find-UnimportedPublicCommits.ps1"))} -DevRepo {Quote(devDc)} -DevRef main " +
                                         $"-PublicDotCharterUrl {Quote(publicDc)} -PublicJuceSharpUrl {Quote(noJuce)} -FailOnMissing; exit $LASTEXITCODE");
            Assert.True(watch.ExitCode == 0, watch.Output);
            Assert.Contains("DotCharter: 0 public commit(s) not imported", watch.Output, StringComparison.Ordinal);

            var import = RunPwsh(scratch, $"& {Quote(Release("Import-PublicCommit.ps1"))} -Repo DotCharter -Commit {snapshot} " +
                                          $"-DevRepo {Quote(devDc)} -Base main -PublicUrl {Quote(publicDc)} -NoPullRequest; exit $LASTEXITCODE");
            Assert.True(import.ExitCode != 0, import.Output);
            Assert.Contains($"{snapshot} is a public snapshot", import.Output, StringComparison.Ordinal);
            Assert.Equal("main", Git(devDc, "branch", "--show-current"));
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void ReleasePreflight_RunsTheLocalSyncCheck_AndStopsOnAMissingImport()
    {
        // Actions is off on the dev repos, so the scheduled watch never runs: the release
        // preflight is the local reminder (#83). Comments stripped, so a comment cannot pass it.
        var source = string.Join('\n', File.ReadAllLines(Release("New-Release.ps1")).Where(l => !l.TrimStart().StartsWith('#')));
        var preflight = source[source.IndexOf("Write-Step 'Preflight checks'", StringComparison.Ordinal)..source.IndexOf("Write-Ok 'preflight done'", StringComparison.Ordinal)];
        var call = System.Text.RegularExpressions.Regex.Match(preflight,
            @"& pwsh -NoProfile -File \(Join-Path \$PSScriptRoot 'Find-UnimportedPublicCommits\.ps1'\)[^\n]*-FailOnMissing[^\n]*\n\s*if \(\$LASTEXITCODE -ne 0\) \{ throw ");
        Assert.True(call.Success, "New-Release.ps1's preflight must run Find-UnimportedPublicCommits.ps1 -FailOnMissing and throw on a non-zero exit.");
    }

    internal static (string Name, string Email) SnapshotIdentity()
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Release("public-snapshot-identity.json")));
        return (json.RootElement.GetProperty("name").GetString()!, json.RootElement.GetProperty("email").GetString()!);
    }

    private static (int ExitCode, string Output) RunFinder(string scratch, string devDc, string devJuce, string publicDc, string publicJuce, string extra) =>
        RunPwsh(scratch, $"& {Quote(Release("Find-UnimportedPublicCommits.ps1"))} -DevRepo {Quote(devDc)} -DevRef main " +
                         $"-DevJuceSharpGitDir {Quote(Path.Combine(devJuce, ".git"))} " +
                         $"-PublicDotCharterUrl {Quote(publicDc)} -PublicJuceSharpUrl {Quote(publicJuce)} {extra}; exit $LASTEXITCODE");

    // A dev DotCharter repo (owner identity, version, .gitmodules, native pin, JuceSharp gitlink
    // in the index) and its dev JuceSharp, as PublicSnapshotExportTests builds them.
    private static (string DevDc, string DevJuce) NewDevRepos(string scratch)
    {
        var devJuce = Path.Combine(scratch, "dev-juce");
        Git(scratch, "init", "-q", "-b", "main", devJuce);
        WriteTree(devJuce, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <Version>9.8.7</Version>\n  </PropertyGroup>\n</Project>\n");
        WriteTree(devJuce, "src/JuceSharp.Native/include/dotcharter_audio.h", "#define DCA_ABI_VERSION 47\n");
        Commit(devJuce, "native work", ".");

        var devDc = Path.Combine(scratch, "dev-dc");
        Git(scratch, "init", "-q", "-b", "main", devDc);
        Git(devDc, "config", "user.name", "Owner Name");
        Git(devDc, "config", "user.email", "owner@example.com");
        WriteTree(devDc, "DotCharter.UI/DotCharter.UI.csproj", "<Project>\n  <PropertyGroup>\n    <Version>1.2.3</Version>\n  </PropertyGroup>\n</Project>\n");
        WriteTree(devDc, ".gitmodules", $"[submodule \"JuceSharp\"]\n\tpath = JuceSharp\n\turl = https://github.com/SomeeName/JuceSharp{Dev}.git\n");
        WriteTree(devDc, "README.md", $"See SomeeName/DotCharter{Dev}.\n");
        WriteTree(devDc, "tools/setup/native-runtime.json", $"{{\n  \"repository\": \"SomeeName/JuceSharp{Dev}\",\n  \"release\": \"native-prebuilt\",\n  \"runtimes\": []\n}}\n");
        Git(devDc, "add", "--", ".");
        Git(devDc, "update-index", "--add", "--cacheinfo", $"160000,{Git(devJuce, "rev-parse", "HEAD")},JuceSharp");
        Commit(devDc, "first dev commit");
        return (devDc, devJuce);
    }

    private static string Contribute(string repo, string message, string path)
    {
        Git(repo, "add", "--", path);
        Git(repo, "-c", "user.name=Contributor", "-c", "user.email=contributor@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
        return Git(repo, "rev-parse", "HEAD");
    }

    private static void CommitEmpty(string repo, string message) =>
        Git(repo, "-c", "user.name=Dev", "-c", "user.email=dev@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", message);

    private static (int ExitCode, string Output) RunExport(string scratch, string devDc, string devJuce, string root, string publicDc, string publicJuce) =>
        RunPwsh(scratch, $"& {Quote(Release("Export-PublicSnapshot.ps1"))} -DevRepo {Quote(devDc)} -DevRef main " +
                         $"-DevJuceSharpGitDir {Quote(Path.Combine(devJuce, ".git"))} -ScratchRoot {Quote(Path.Combine(scratch, root))} " +
                         $"-PublicDotCharterUrl {Quote(publicDc)} -PublicJuceSharpUrl {Quote(publicJuce)}; exit $LASTEXITCODE");
}
