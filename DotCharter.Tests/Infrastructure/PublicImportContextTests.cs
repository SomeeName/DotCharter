using static DotCharter.Tests.Infrastructure.PublicSnapshotExportTests;

namespace DotCharter.Tests.Infrastructure;

/// <summary>Byte-preserving import context and staged feature/merge patches over a private suffix.</summary>
public sealed class PublicImportContextTests
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void FeatureThenMergeOwnEdit_PreserveTheDevTail_AndRecordEachCommitOnce(string host)
    {
        var scratch = Directory.CreateTempSubdirectory("dc-import-context-").FullName;
        try
        {
            var repo = Path.Combine(scratch, "repo");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "core.autocrlf", "false");
            Git(repo, "config", "commit.gpgsign", "false");
            var baseline = "# Guide\nClone the development repository first.\nStep one.\nStep two.\n";
            var featureText = baseline.Replace("Step two.", "Step 2.", StringComparison.Ordinal);
            var mergeText = featureText.Replace("Step one.", "Step 1, in the merge.", StringComparison.Ordinal);
            const string tail = "Dev-only tail.\n";
            WriteTree(repo, "docs/guide.md", baseline);
            Commit(repo, "base", ".");
            var before = Git(repo, "rev-parse", "HEAD");
            WriteTree(repo, "docs/guide.md", featureText);
            Commit(repo, "feature", "docs/guide.md");
            var feature = Git(repo, "rev-parse", "HEAD");
            WriteTree(repo, "docs/guide.md", mergeText);
            Git(repo, "add", "docs/guide.md");
            var mergeTree = Git(repo, "write-tree");
            var merge = Git(repo, "-c", "user.name=Tester", "-c", "user.email=tester@example.com",
                "commit-tree", mergeTree, "-p", before, "-p", feature, "-m", "Merge feature with own edit");
            var auto = Git(repo, "merge-tree", "--write-tree", "--no-messages", before, feature).Split('\n')[0];
            Assert.Equal(Git(repo, "rev-parse", feature + "^{tree}"), auto);
            Git(repo, "read-tree", "--reset", "-u", before);
            WriteTree(repo, "docs/guide.md", baseline + tail);
            Commit(repo, "private tail", "docs/guide.md");

            Apply(repo, scratch, before, feature, baseline, featureText, host);
            Assert.Equal(featureText + tail, File.ReadAllText(Path.Combine(repo, "docs/guide.md")));
            Apply(repo, scratch, auto, merge, featureText, mergeText, host);
            Assert.Equal(mergeText + tail, File.ReadAllText(Path.Combine(repo, "docs/guide.md")));
            Git(repo, "-c", "user.name=Tester", "-c", "user.email=tester@example.com", "commit", "-q", "-m",
                $"Import feature and merge\n\nPublic-Commit: DotCharter@{feature}\nPublic-Commit: DotCharter@{merge}");
            Assert.Equal($"DotCharter@{feature}\nDotCharter@{merge}",
                Git(repo, "log", "-1", "--format=%(trailers:key=Public-Commit,valueonly)").Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal("1\t1\tdocs/guide.md", Git(repo, "diff", "--numstat", auto, merge));
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Theory]
    // Public edited the last line so that it merely ENDS with the dev tail's text.
    [InlineData("foo\n", "foo\n}\n", "end }\n", "end }\n}\n")]
    [InlineData("foo\r\n", "foo\r\n}\r\n", "end }\r\n", "end }\r\n}\r\n")]
    // Public really appended the same lines: one copy.
    [InlineData("foo\n", "foo\n}\n", "foo\n}\n", "foo\n}\n")]
    [InlineData("foo\r\n", "foo\r\n}\r\n", "foo\r\n}\r\n", "foo\r\n}\r\n")]
    // The repeated-suffix loop: a base that ends with the tail, public inserting "end }".
    [InlineData("a\n}\n", "a\n}\n}\n", "a\nend }\n}\n", "a\nend }\n}\n}\n")]
    public void DevTail_MatchesPublicTextOnlyOnWholeLines(string baseText, string devText, string publicText, string expected)
    {
        var scratch = Directory.CreateTempSubdirectory("dc-import-tail-").FullName;
        try
        {
            var outFile = Path.Combine(scratch, "out.txt");
            var module = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "PublicSync.psm1");
            var run = RunPwsh(scratch, $$"""
                Import-Module {{Quote(module)}} -Force
                [IO.File]::WriteAllText({{Quote(outFile)}}, (Add-PublicImportTail -Text {{Quote(publicText)}} -BaseText {{Quote(baseText)}} -DevText {{Quote(devText)}}))
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Equal(expected, File.ReadAllText(outFile));
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void PublicEditEndingInTheTailText_KeepsTheDevLine_ThroughARealImportPatch()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-import-tail-real-").FullName;
        try
        {
            var repo = Path.Combine(scratch, "repo");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "core.autocrlf", "false");
            Git(repo, "config", "commit.gpgsign", "false");
            const string baseline = "foo\n";
            const string publicText = "end }\n";
            WriteTree(repo, "docs/guide.md", baseline);
            Commit(repo, "base", ".");
            var before = Git(repo, "rev-parse", "HEAD");
            WriteTree(repo, "docs/guide.md", publicText);
            Commit(repo, "public edit", "docs/guide.md");
            var after = Git(repo, "rev-parse", "HEAD");
            Git(repo, "read-tree", "--reset", "-u", before);
            WriteTree(repo, "docs/guide.md", baseline + "}\n");
            Commit(repo, "private tail", "docs/guide.md");

            Apply(repo, scratch, before, after, baseline, publicText, "pwsh");
            Assert.Equal(publicText + "}\n", File.ReadAllText(Path.Combine(repo, "docs/guide.md")));
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Theory]
    // git before 2.38 has no --write-tree: the command fails (usage, exit 129).
    [InlineData("fail")]
    // The command "succeeds" but prints no tree id.
    [InlineData("garbage")]
    public void MergeTreeThatCannotReplay_Throws_NamingTheGitVersionAndCommand(string mode)
    {
        var scratch = Directory.CreateTempSubdirectory("dc-merge-own-").FullName;
        try
        {
            var module = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "PublicSync.psm1");
            var run = RunPwsh(scratch, $$"""
                $sync = Import-Module {{Quote(module)}} -Force -PassThru
                & $sync {
                    function script:Invoke-SnapshotGit {
                        param([string[]]$Arguments, [int[]]$AllowExitCodes)
                        if ($Arguments -contains 'merge-tree') {
                            if ({{Quote(mode)}} -eq 'fail') { throw "git merge-tree failed (129): usage: git merge-tree" }
                            return 'usage: git merge-tree <base-tree> <branch1> <branch2>'
                        }
                        if ($Arguments -contains '--format=%P') { return ('a' * 40) + ' ' + ('b' * 40) }
                        return 'c' * 40
                    }
                }
                try { $own = Get-PublicMergeOwnBase -GitDir 'fixture.git' -Sha ('d' * 40); "RESULT: no error, own base '$own'" }
                catch { "RESULT: $($_.Exception.Message)" }
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("RESULT: merge " + new string('d', 40), run.Output, StringComparison.Ordinal);
            Assert.Contains("git 2.38", run.Output, StringComparison.Ordinal);
            Assert.Contains("merge-tree --write-tree --no-messages", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    private static void Apply(string repo, string scratch, string before, string after, string baseText, string afterText, string host)
    {
        var staged = Git(repo, "write-tree");
        var devText = File.ReadAllText(Path.Combine(repo, "docs/guide.md"));
        var beforeFile = Path.Combine(scratch, "before.txt");
        var afterFile = Path.Combine(scratch, "after.txt");
        var module = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "PublicSync.psm1");
        var run = RunPowerShell(scratch, $$"""
            Import-Module {{Quote(module)}} -Force
            [IO.File]::WriteAllText({{Quote(beforeFile)}}, (Add-PublicImportTail -Text {{Quote(baseText)}} -BaseText {{Quote(baseText)}} -DevText {{Quote(devText)}}))
            [IO.File]::WriteAllText({{Quote(afterFile)}}, (Add-PublicImportTail -Text {{Quote(afterText)}} -BaseText {{Quote(baseText)}} -DevText {{Quote(devText)}}))
            """, host);
        Assert.True(run.ExitCode == 0, run.Output);
        Git(repo, "read-tree", "--reset", "-u", before);
        WriteTree(repo, "docs/guide.md", File.ReadAllText(beforeFile));
        Git(repo, "add", "docs/guide.md");
        var projectedBefore = Git(repo, "write-tree");
        Git(repo, "read-tree", "--reset", "-u", after);
        WriteTree(repo, "docs/guide.md", File.ReadAllText(afterFile));
        Git(repo, "add", "docs/guide.md");
        var projectedAfter = Git(repo, "write-tree");
        var patch = Path.Combine(scratch, "import.patch");
        File.WriteAllText(patch, Git(repo, "diff", "--binary", "--full-index", projectedBefore, projectedAfter) + "\n");
        Git(repo, "read-tree", "--reset", "-u", staged);
        Git(repo, "apply", "-3", "--index", patch);
    }
}
