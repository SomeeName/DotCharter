using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The public export (<c>tools/release/Export-PublicSnapshot.ps1</c> over
///     <c>PublicSnapshot.psm1</c>): the rename map rewrites every dev repo name, the leak check
///     reports each kind of leak with file:line and passes a clean tree, its personal-data
///     patterns are exactly <see cref="PersonalDataLintTests" />'s, and a snapshot is ONE commit
///     on top of the previous public one, with no trailer and no dev id, whose JuceSharp gitlink
///     is the PUBLIC JuceSharp snapshot. The module's own code runs under pwsh on fixture repos;
///     every private name here is assembled from pieces, like the module's.
/// </summary>
public sealed partial class PublicSnapshotExportTests
{
    private const string Dev = "-" + "Dev";
    private static string Root => TestPaths.RepoRoot();
    private static string Module => Path.Combine(Root, "tools", "release", "PublicSnapshot.psm1");
    private static string ExportScript => Path.Combine(Root, "tools", "release", "Export-PublicSnapshot.ps1");

    [Fact]
    public void RenameMap_RewritesEveryForm_AndLeavesOtherWords()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-rename-").FullName;
        try
        {
            var input = string.Join('\n',
                $"url = https://github.com/SomeeName/JuceSharp{Dev}.git",
                $"Fixes SomeeName/DotCharter{Dev}#12 and SomeeName/JuceSharp{Dev}#3",
                $"bare DotCharter{Dev}, (JuceSharp{Dev}) and \"DotCharter{Dev}\"",
                $"DotCharter{Dev}elopment jucesharp{Dev.ToLowerInvariant()}rate SomeeName/DotCharter");
            const string expected = "url = https://github.com/SomeeName/JuceSharp.git\n" +
                                    "Fixes SomeeName/DotCharter#12 and SomeeName/JuceSharp#3\n" +
                                    "bare DotCharter, (JuceSharp) and \"DotCharter\"\n";
            var tail = $"DotCharter{Dev}elopment jucesharp{Dev.ToLowerInvariant()}rate SomeeName/DotCharter";
            File.WriteAllText(Path.Combine(scratch, "in.txt"), input);

            var run = RunPwsh(scratch, $"""
                Import-Module {Quote(Module)} -Force
                $text = [IO.File]::ReadAllText({Quote(Path.Combine(scratch, "in.txt"))})
                [IO.File]::WriteAllText({Quote(Path.Combine(scratch, "out.txt"))}, (Convert-PublicText -Text $text))
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Equal(expected + tail, File.ReadAllText(Path.Combine(scratch, "out.txt")));
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    [Fact]
    public void LeakCheck_ReportsEveryPlantedLeak_WithFileAndLine_AndPassesACleanTree()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-leaks-").FullName;
        try
        {
            // case -> (rule, the planted line). Each lands on line 3 of docs/note.md in its own tree.
            var cases = new (string Case, string Rule, string Line)[]
            {
                ("dev-repo", "dev-repo", $"clone SomeeName/DotCharter{Dev}.git"),
                ("dev-repo-lower", "dev-repo", $"see jucesharp{Dev.ToLowerInvariant()} for that"),
                ("archive", "archive-repo", "moved from JuceSharp-" + "Archive last year"),
                ("addon-name", "addon-name", "loads " + "dotcharter" + ".rock" + "smith.dll"),
                ("addons-path", "addons-path", "the reader lives in " + "add" + "ons/Reader"),
                ("user-path", "user-profile-path", "C:" + @"\Users\" + "jdoe" + @"\Music"),
                // A placeholder is matched whole, and an escaped separator is still a separator.
                ("user-path-prefix", "user-profile-path", "C:" + @"\Users\" + "youngster" + @"\Music"),
                ("user-path-escaped", "user-profile-path", "\"C:" + @"\\Users\\" + "jdoe" + @"\\Music" + "\""),
                ("library-drive", "personal-data", "F" + @":\" + "Rock" + @"smith\dlc"),
                ("real-name", "personal-data", "Thanks, " + "Lu" + "kas!"),
                // A link to a dev-repo pull request, issue or commit is dead in the public repos.
                ("history-pr", "history-link", "see https://github.com/" + "SomeeName/DotCharter/" + "pull/575"),
                ("history-issue", "history-link", "[#25](https://github.com/" + "someename/JuceSharp/" + "issues/25)"),
                ("history-commit", "history-link", "github.com/" + "SomeeName/DotCharter/" + "commit/211d25ae1")
            };
            foreach (var c in cases)
                WriteTree(Path.Combine(scratch, c.Case), "docs/note.md", $"# Note\n\n{c.Line}\n");

            // A path-only leak: an innocuous file inside the add-on folder.
            WriteTree(Path.Combine(scratch, "addons-file"), "add" + "ons/x/readme.txt", "nothing to see\n");
            // Nothing is skipped by content (#83): a file with a literal NUL is read as Latin-1,
            // and a gzip fixture is decompressed in memory (the finding names the .gz and the
            // line of the decompressed text).
            WriteTree(Path.Combine(scratch, "nul-file"), "src/Odd.cs", $"// a\0b\n\nvar repo = \"DotCharter{Dev}\";\n");
            WriteGzip(Path.Combine(scratch, "gz-file"), "fixtures/song.json.gz", "{\n  \"x\": 1,\n  \"path\": \"F:" + @"\\" + "Rock" + "smith" + @"\\dlc" + "\"\n}\n");

            // Clean: near misses of every rule, and a clean binary file and gzip fixture.
            var clean = Path.Combine(scratch, "clean");
            WriteTree(clean, "README.md",
                "https://github.com/SomeeName/DotCharter/releases/latest github.com/SomeeName/JuceSharp/pulls\n" +
                $"SomeeName/DotCharter and SomeeName/JuceSharp\nDotCharter{Dev}elopment jucesharp{Dev.ToLowerInvariant()}rate\n" +
                @"E:\Rocksmith\dlc C:\Program Files\DotCharter %USERPROFILE%\Music" + "\nthe add-on, myaddons/ and Isabela in Belarus\n" +
                // Every made-up user folder the tree uses, in the spellings it uses them.
                UserPath(@"you\.local") + UserPath(@"Alex\AppData") + UserPath(@"A B\tone.json") + UserPath("NAME</c>") +
                UserPath("&lt;name&gt;</c>") + UserPath(@"<user>\x") + UserPath("\u2026`") + UserPath("M\u00fcller\\x") +
                "\"C:" + @"\\Users\\alice\\x" + "\"\n" + "'C:" + @"\\\\Users\\\\M\\u00fcller\\\\x" + "'\n");
            File.WriteAllBytes(Path.Combine(clean, "blob.bin"), [0, .. Encoding.ASCII.GetBytes($"DotCharter{Dev}elopment")]);
            WriteGzip(clean, "fixtures/clean.json.gz", "{ \"song\": \"SomeeName/DotCharter\" }\n");

            var run = RunPwsh(scratch, $$"""
                Import-Module {{Quote(Module)}} -Force
                foreach ($dir in Get-ChildItem -LiteralPath {{Quote(scratch)}} -Directory) {
                    $found = @(Find-PublicTreeLeaks -Root $dir.FullName)
                    if ($found.Count -eq 0) { "$($dir.Name)|clean" }
                    foreach ($f in $found) { "$($dir.Name)|$($f.Rule)|$($f.Path):$($f.Line)" }
                }
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var c in cases)
                Assert.True(lines.Contains($"{c.Case}|{c.Rule}|docs/note.md:3"), $"'{c.Case}' was not reported as {c.Rule} at docs/note.md:3.\n{run.Output}");
            Assert.Contains($"addons-file|addons-path|{"add" + "ons"}/x/readme.txt:0", lines);
            Assert.Contains("nul-file|dev-repo|src/Odd.cs:3", lines);
            Assert.Contains("gz-file|personal-data|fixtures/song.json.gz:3", lines);
            Assert.Contains("clean|clean", lines);
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    [Fact]
    public void LeakCheck_PersonalPatterns_AreExactlyThePersonalDataLints()
    {
        var run = RunPwsh(Path.GetTempPath(), $$"""
            Import-Module {{Quote(Module)}} -Force
            foreach ($p in Get-PersonalDataPatterns) { "$($p.IgnoreCase)`t$($p.Pattern)" }
            """);
        Assert.True(run.ExitCode == 0, run.Output);
        var actual = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        var expected = PersonalDataLintTests.Patterns
            .Select(p => $"{(p.Options.HasFlag(RegexOptions.IgnoreCase) ? "True" : "False")}\t{p}")
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static void Export_IsOneCommitOnThePreviousSnapshot_WithoutTrailers_AndPointsAtThePublicJuceSharp()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-export-").FullName;
        try
        {
            var devJuce = Path.Combine(scratch, "dev-juce");
            Git(scratch, "init", "-q", "-b", "main", devJuce);
            WriteTree(devJuce, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <Version>9.8.7</Version>\n  </PropertyGroup>\n</Project>\n");
            WriteTree(devJuce, "src/JuceSharp.Native/include/dotcharter_audio.h", "#define DCA_ABI_VERSION 47\n");
            WriteTree(devJuce, "README.md", $"Issues: SomeeName/DotCharter{Dev}\n");
            Commit(devJuce, "native work", ".");
            var devJuceCommit = Git(devJuce, "rev-parse", "HEAD");

            var devDc = Path.Combine(scratch, "dev-dc");
            Git(scratch, "init", "-q", "-b", "main", devDc);
            Git(devDc, "config", "user.name", "Owner Name");
            Git(devDc, "config", "user.email", "owner@example.com");
            WriteTree(devDc, "DotCharter.UI/DotCharter.UI.csproj", "<Project>\n  <PropertyGroup>\n    <Version>1.2.3</Version>\n  </PropertyGroup>\n</Project>\n");
            WriteTree(devDc, ".gitmodules", $"[submodule \"JuceSharp\"]\n\tpath = JuceSharp\n\turl = https://github.com/SomeeName/JuceSharp{Dev}.git\n");
            // Dev pins its gitlink, but the rename map changes the JuceSharp README, so the
            // public tree differs and that pin must NOT be inherited.
            WriteTree(devDc, "tools/setup/native-runtime.json", Manifest((devJuceCommit, DllHash, HostHash)));
            WriteTree(devDc, "README.md", $"See SomeeName/DotCharter{Dev}.\n");
            Git(devDc, "add", "--", ".");
            // The gitlink lives only in the index: the dev JuceSharp is never checked out here.
            Git(devDc, "update-index", "--add", "--cacheinfo", $"160000,{devJuceCommit},JuceSharp");
            Commit(devDc, "first dev commit\n\nCo-Authored-By: Someone <x@example.com>");
            var devFirst = Git(devDc, "rev-parse", "HEAD");

            // Release 1: the public repos exist but are empty (no main), so a root commit.
            var emptyDc = Path.Combine(scratch, "empty-dc.git");
            var emptyJuce = Path.Combine(scratch, "empty-juce.git");
            Git(scratch, "init", "-q", "--bare", emptyDc);
            Git(scratch, "init", "-q", "--bare", emptyJuce);
            var first = RunExport(scratch, devDc, devJuce, "s1", emptyDc, emptyJuce);
            Assert.True(first.ExitCode == 0, first.Output);
            var publicDc1 = Path.Combine(scratch, "s1", "DotCharter");
            var publicJuce1 = Path.Combine(scratch, "s1", "JuceSharp.git");
            var dc1 = Git(publicDc1, "rev-parse", "main");
            var juce1 = Git(publicJuce1, "rev-parse", "main");

            Assert.Equal("", Git(publicDc1, "log", "-1", "--format=%P", "main"));
            Assert.Equal("", Git(publicJuce1, "log", "-1", "--format=%P", "main"));
            Assert.Equal("Initial public commit of DotCharter", Git(publicDc1, "log", "-1", "--format=%B", "main"));
            Assert.Equal("Initial public commit of JuceSharp", Git(publicJuce1, "log", "-1", "--format=%B", "main"));
            Assert.Equal("Owner Name <owner@example.com>|Owner Name <owner@example.com>",
                Git(publicDc1, "log", "-1", "--format=%an <%ae>|%cn <%ce>", "main"));
            var raw = Git(publicDc1, "cat-file", "-p", "main");
            // The whole message is the one line: nothing follows it, so no trailer of any kind.
            Assert.EndsWith("\n\nInitial public commit of DotCharter", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Co-Authored-By", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(devFirst, raw, StringComparison.Ordinal);
            Assert.Equal("v1.2.3", Git(publicDc1, "describe", "--tags", "--exact-match", "main"));
            // Annotated, by the owner, with the commit's message.
            Assert.Equal("tag", Git(publicDc1, "cat-file", "-t", "v1.2.3"));
            var tag = Git(publicDc1, "cat-file", "-p", "v1.2.3");
            Assert.Contains($"object {dc1}", tag, StringComparison.Ordinal);
            Assert.Contains("tagger Owner Name <owner@example.com>", tag, StringComparison.Ordinal);
            Assert.EndsWith("\n\nInitial public commit of DotCharter", tag, StringComparison.Ordinal);
            Assert.Equal("tag", Git(publicJuce1, "cat-file", "-t", "v9.8.7"));
            Assert.EndsWith("\n\nInitial public commit of JuceSharp", Git(publicJuce1, "cat-file", "-p", "v9.8.7"), StringComparison.Ordinal);

            // The gitlink is the public JuceSharp snapshot, and the dev names are gone.
            Assert.NotEqual(devJuceCommit, juce1);
            Assert.Matches($"^160000 commit {juce1}\tJuceSharp$", Git(publicDc1, "ls-tree", "main", "JuceSharp"));
            Assert.Contains("url = https://github.com/SomeeName/JuceSharp.git", Git(publicDc1, "show", "main:.gitmodules"), StringComparison.Ordinal);
            Assert.Equal("Issues: SomeeName/DotCharter", Git(publicJuce1, "show", "main:README.md"));
            var manifest = Git(publicDc1, "show", "main:tools/setup/native-runtime.json");
            Assert.Contains("\"repository\": \"SomeeName/JuceSharp\"", manifest, StringComparison.Ordinal);
            Assert.Contains($"\"commit\": \"{juce1}\"", manifest, StringComparison.Ordinal);
            var pin1 = manifest[manifest.IndexOf($"\"commit\": \"{juce1}\"", StringComparison.Ordinal)..];
            Assert.Contains("PENDING", pin1, StringComparison.Ordinal);
            Assert.DoesNotContain(DllHash, pin1, StringComparison.Ordinal);
            Assert.Contains("leak check: clean", first.Output, StringComparison.Ordinal);

            // Release 2: a new dev commit; the previous snapshots come from the "public" repos.
            // Same version first: public v1.2.3 already names the first snapshot, so the push
            // preflight refuses, dry run included (#83). A public tag is never moved.
            WriteTree(devDc, "README.md", $"See SomeeName/DotCharter{Dev}, now with more.\n");
            Commit(devDc, "second dev commit", "README.md");
            var sameVersion = RunExport(scratch, devDc, devJuce, "s2", publicDc1, publicJuce1);
            Assert.True(sameVersion.ExitCode != 0, sameVersion.Output);
            Assert.Contains($"DotCharter: tag v1.2.3 already exists for commit {dc1}", sameVersion.Output, StringComparison.Ordinal);
            Assert.Contains("bump the version", sameVersion.Output, StringComparison.Ordinal);
            // The unchanged JuceSharp reuses its public commit AND tag: no problem there.
            Assert.DoesNotContain("JuceSharp: tag", sameVersion.Output, StringComparison.Ordinal);

            WriteTree(devDc, "DotCharter.UI/DotCharter.UI.csproj", Csproj("1.2.4"));
            Commit(devDc, "version 1.2.4", "DotCharter.UI/DotCharter.UI.csproj");
            var second = RunExport(scratch, devDc, devJuce, "s2", publicDc1, publicJuce1);
            Assert.True(second.ExitCode == 0, second.Output);
            Assert.Contains("push preflight: both public repos accept this snapshot", second.Output, StringComparison.Ordinal);
            var publicDc2 = Path.Combine(scratch, "s2", "DotCharter");
            Assert.Equal(dc1, Git(publicDc2, "log", "-1", "--format=%P", "main"));
            Assert.Equal("2", Git(publicDc2, "rev-list", "--count", "main"));
            // A snapshot with a public parent is "Update to v<version>", and so is its tag.
            Assert.Equal("Update to v1.2.4", Git(publicDc2, "log", "-1", "--format=%B", "main"));
            Assert.EndsWith("\n\nUpdate to v1.2.4", Git(publicDc2, "cat-file", "-p", "v1.2.4"), StringComparison.Ordinal);
            // JuceSharp did not change: the previous public commit is reused, not stacked, and
            // its tag keeps that commit's message, so it is the tag already public.
            var publicJuce2 = Path.Combine(scratch, "s2", "JuceSharp.git");
            Assert.Equal(juce1, Git(publicJuce2, "rev-parse", "main"));
            Assert.Equal(Git(publicJuce1, "rev-parse", "v9.8.7"), Git(publicJuce2, "rev-parse", "v9.8.7"));
            Assert.Matches($"^160000 commit {juce1}\tJuceSharp$", Git(publicDc2, "ls-tree", "main", "JuceSharp"));

            // Release 3: JuceSharp moves on with nothing for the rename map to change, so the
            // public JuceSharp tree IS the dev gitlink's and the dev pin carries over.
            WriteTree(devJuce, "README.md", "Issues: the tracker\n");
            WriteTree(devJuce, "Directory.Build.props", Csproj("9.8.8"));
            Commit(devJuce, "native work 2", "README.md", "Directory.Build.props");
            var devJuce2 = Git(devJuce, "rev-parse", "HEAD");
            WriteTree(devDc, "tools/setup/native-runtime.json", Manifest((devJuceCommit, DllHash, HostHash), (devJuce2, DllHash2, HostHash2)));
            WriteTree(devDc, "DotCharter.UI/DotCharter.UI.csproj", Csproj("1.2.5"));
            Git(devDc, "add", "--", "tools/setup/native-runtime.json", "DotCharter.UI/DotCharter.UI.csproj");
            Git(devDc, "update-index", "--cacheinfo", $"160000,{devJuce2},JuceSharp");
            Commit(devDc, "bump JuceSharp");
            var third = RunExport(scratch, devDc, devJuce, "s3", publicDc2, Path.Combine(scratch, "s2", "JuceSharp.git"));
            Assert.True(third.ExitCode == 0, third.Output);
            var publicDc3 = Path.Combine(scratch, "s3", "DotCharter");
            var publicJuce3 = Path.Combine(scratch, "s3", "JuceSharp.git");
            var juce3 = Git(publicJuce3, "rev-parse", "main");
            Assert.Equal(juce1, Git(publicJuce3, "log", "-1", "--format=%P", "main"));
            Assert.Equal("Update to v9.8.8", Git(publicJuce3, "log", "-1", "--format=%B", "main"));
            Assert.EndsWith("\n\nUpdate to v9.8.8", Git(publicJuce3, "cat-file", "-p", "v9.8.8"), StringComparison.Ordinal);
            Assert.Equal(Git(devJuce, "rev-parse", "HEAD^{tree}"), Git(publicJuce3, "rev-parse", "main^{tree}"));
            Assert.Matches($"^160000 commit {juce3}\tJuceSharp$", Git(publicDc3, "ls-tree", "main", "JuceSharp"));
            var manifest3 = Git(publicDc3, "show", "main:tools/setup/native-runtime.json");
            var pin3 = manifest3[manifest3.IndexOf($"\"commit\": \"{juce3}\"", StringComparison.Ordinal)..];
            Assert.Contains($"\"juce_sharp.dll\": \"{DllHash2}\"", pin3, StringComparison.Ordinal);
            Assert.Contains($"\"juce_vst_host.exe\": \"{HostHash2}\"", pin3, StringComparison.Ordinal);
            Assert.DoesNotContain("PENDING", pin3, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void Export_RefusesAPushWithoutBothTargets()
    {
        var run = RunPwsh(Path.GetTempPath(), $"& {Quote(ExportScript)} -Push -DevRef HEAD; exit $LASTEXITCODE");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("-Push needs BOTH", run.Output, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) RunExport(string scratch, string devDc, string devJuce, string root,
        string? publicDc = null, string? publicJuce = null)
    {
        var args = $"-DevRepo {Quote(devDc)} -DevRef main -DevJuceSharpGitDir {Quote(Path.Combine(devJuce, ".git"))} -ScratchRoot {Quote(Path.Combine(scratch, root))}";
        if (publicDc is not null) args += $" -PublicDotCharterUrl {Quote(publicDc)} -PublicJuceSharpUrl {Quote(publicJuce!)}";
        return RunPwsh(scratch, $"& {Quote(ExportScript)} {args}; exit $LASTEXITCODE");
    }

    private static readonly string DllHash = new('a', 64);
    private static readonly string HostHash = new('b', 64);
    private static readonly string DllHash2 = new('c', 64);
    private static readonly string HostHash2 = new('d', 64);

    private static string Manifest(params (string Commit, string Dll, string Host)[] pins)
    {
        var entries = pins.Select(p =>
            $"    {{ \"commit\": \"{p.Commit}\", \"abi\": 47, \"files\": {{ \"juce_sharp.dll\": \"{p.Dll}\", \"juce_vst_host.exe\": \"{p.Host}\" }} }}");
        return $"{{\n  \"repository\": \"SomeeName/JuceSharp{Dev}\",\n  \"release\": \"native-prebuilt\",\n  \"runtimes\": [\n{string.Join(",\n", entries)}\n  ]\n}}\n";
    }

    private static string UserPath(string rest) => "C:" + @"\Users\" + rest + "\n";

    // A csproj / Directory.Build.props carrying only <Version>, as the export reads it.
    internal static string Csproj(string version) => $"<Project>\n  <PropertyGroup>\n    <Version>{version}</Version>\n  </PropertyGroup>\n</Project>\n";

    private static void WriteGzip(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.SmallestSize);
        gzip.Write(Encoding.UTF8.GetBytes(content));
    }

    internal static void WriteTree(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal static void Commit(string repo, string message, params string[] paths)
    {
        if (paths.Length > 0) Git(repo, ["add", "--", .. paths]);
        Git(repo, "-c", "user.name=Dev", "-c", "user.email=dev@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
    }

    internal static string Git(string cwd, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var git = Process.Start(info) ?? throw new InvalidOperationException("git did not start.");
        var stderr = git.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} failed ({git.ExitCode}): {stderr.Result}");
        return output.Trim();
    }

    // git writes read-only object files; clear the flag before deleting.
    internal static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    internal static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static (int ExitCode, string Output) RunPwsh(string scratch, string script)
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("dc-public-pwsh-").FullName, "case.ps1");
        File.WriteAllText(file, "$ErrorActionPreference = 'Stop'\n" + script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var info = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = scratch,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", file })
            info.ArgumentList.Add(argument);
        // Plain error text: ANSI escapes in a failure message make the TRX invalid XML.
        info.Environment["NO_COLOR"] = "1";
        // Never the owner's %LOCALAPPDATA% release cache: the export records and reads gate
        // runs there. A case that needs the cache across runs sets its own.
        info.Environment["DOTCHARTER_RELEASE_CACHE"] = Path.Combine(Path.GetDirectoryName(file)!, "release-cache");

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("pwsh (PowerShell 7+) is not on PATH, so the public export cannot be executed here.");
            return default;
        }

        Assert.NotNull(process);
        try
        {
            using (process)
            {
                var stdout = process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = process.StandardError.ReadToEnd();
                // A deadlock detector for a hung child, never a budget.
                Assert.True(process.WaitForExit(180_000), "pwsh did not finish within 180 s.");
                return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }
}
