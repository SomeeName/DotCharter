using System.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/agent/AffectedTests.psm1</c>, the selection behind
///     <c>Invoke-Gate.ps1 -Affected</c>: which test classes cover a diff. Each
///     rule runs against a small synthetic test project written to a scratch
///     folder, so a case names exactly the rule it pins; one case reads this
///     checkout's real test sources to prove the index sees what the gate runs.
///     Executed under Windows PowerShell 5.1, in ONE host process shared by the class:
///     starting a host and importing the module costs more than most cases, so the cases
///     are sent to a running session, each in its own scope and scratch folder.
/// </summary>
public sealed class AffectedTestsModuleTests : IClassFixture<AffectedTestsModuleTests.ModuleSession>, IDisposable
{
    private static readonly string[] StructuralLints =
    [
        "CommentHygieneTests", "DocReferenceLintTests", "SourceFileSizeBudgetTests",
        "MethodComplexityBudgetTests", "AssemblyLayeringTests", "SplitTypePartialRatchetTests"
    ];

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-affected-" + Guid.NewGuid().ToString("N"));

    private string Repo => Path.Combine(_scratch, "repo");

    private readonly ModuleSession _session;

    public AffectedTestsModuleTests(ModuleSession session)
    {
        _session = session;
        Directory.CreateDirectory(Repo);
    }

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    // ---- the index ----

    [Fact]
    public void Index_ListsClassesWithATest_NestedOnesAsOuterPlusInner_AndSkipsHelpersStaticAbstractAndPrivate()
    {
        Write("DotCharter.Tests/Core/Block.cs", """
            namespace DotCharter.Tests.Core
            {
                public sealed class BlockTests
                {
                    [Fact] public void Runs() { }
                    private sealed class NestedTests { [Fact] public void Hidden() { } }
                }

                public static class BlockHelper { }

                public abstract class BlockBaseTests { [Fact] public void Inherited() { } }

                internal sealed class NoCasesHere { public void Plain() { } }

                public sealed class HostTests
                {
                    public sealed class InnerTests { [Fact] public void Runs() { } }
                }
            }
            """);
        Write("DotCharter.Tests/Core/Scoped.cs", """
            namespace DotCharter.Tests.Core;

            public sealed class ScopedFixture { }

            public sealed class ScopedTests
            {
                [Theory, InlineData(1)] public void Runs(int _) { }
            }
            """);

        var classes = Run("$idx = New-AffectedTestIndex '@@REPO@@'\n($idx.Classes.Keys | ForEach-Object { $_ }) -join '|'").Split('|').Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["DotCharter.Tests.Core.BlockTests", "DotCharter.Tests.Core.HostTests+InnerTests", "DotCharter.Tests.Core.ScopedTests"],
            classes);
    }

    // ---- the rules ----

    [Fact]
    public void ChangedTestFile_SelectsItsClasses_AndTheStructuralLints()
    {
        AddLints();
        AddTest("Core", "NoteTests");
        AddTest("Core", "OtherTests");

        var sel = Select("DotCharter.Tests/Core/NoteTests.cs");

        Assert.Equal(Sorted(["Core.NoteTests", .. StructuralLints.Select(n => "Infrastructure." + n)]), sel.Gate);
        Assert.Empty(sel.Headless);
    }

    [Fact]
    public void ChangedSourceFile_SelectsClassesThatNameItsType_AndClassesNamedAfterIt_NotProseSubstringsOrOthers()
    {
        AddTest("Core/Models", "NoteTests");
        AddTest("Other", "BeatMapTests", "var n = new Note(1);");
        AddTest("Other", "ProseOnlyTests", "// Note is not used here\n var x = 1;");
        AddTest("Other", "SubstringTests", "var book = new NoteBook();");
        AddTest("Other", "UnrelatedTests");

        // No mirror folder for the launcher project: only the name rules apply.
        var sel = Select("DotCharter.Launcher/Note.cs");

        Assert.Equal(["Core.Models.NoteTests", "Other.BeatMapTests"], sel.Gate);
        Assert.Contains("named after type Note", Reasons(sel, "Core.Models.NoteTests"));
        Assert.Contains("names type Note", Reasons(sel, "Other.BeatMapTests"));
    }

    [Fact]
    public void ChangedSourceFile_WithPartialSuffix_UsesTheMainType()
    {
        AddTest("Other", "ViewModelUseTests", "new PlayModeViewModel();");

        var sel = Select("DotCharter.UI/ViewModels/PlayModeViewModel.Results.cs");

        Assert.Equal(["Other.ViewModelUseTests"], sel.Gate);
    }

    [Fact]
    public void ChangedNonCodeFile_SelectsClassesThatNameTheFile()
    {
        AddTest("Rendering", "ShaderTests", "File.ReadAllBytes(\"timeline.frag.spv\");");
        AddTest("Rendering", "OtherShaderTests", "File.ReadAllBytes(\"timeline.vert.spv\");");

        var sel = Select("DotCharter.Rendering/Shaders/timeline.frag.spv");

        // The mirror folder (Tests/Rendering) is small here, so both run; the name rule is the reason for the first.
        Assert.Contains("Rendering.ShaderTests", sel.Gate);
        Assert.Contains("names timeline.frag.spv", Reasons(sel, "Rendering.ShaderTests"));
        Assert.DoesNotContain("names timeline.frag.spv", Reasons(sel, "Rendering.OtherShaderTests"));
    }

    [Fact]
    public void SharedGuideNames_AreNotMatchedByName_ButTheDocsLintsRun()
    {
        AddTest("Other", "MentionsGuideTests", "var p = \"AGENTS.md\";");
        AddTest("Infrastructure", "ReleaseAssetLintTests");

        var sel = Select("DotCharter.UI/AGENTS.md");

        Assert.DoesNotContain("Other.MentionsGuideTests", sel.Gate);
        Assert.Contains("Infrastructure.ReleaseAssetLintTests", sel.Gate);
    }

    [Fact]
    public void DocsChange_SelectsTheMergeTrainDocsLints()
    {
        AddLints();
        AddTest("Infrastructure", "ReleaseAssetLintTests");
        AddTest("Core", "NoteTests");

        var sel = Select("docs/agents/workflow.md");

        Assert.Equal(Sorted(["Infrastructure.ReleaseAssetLintTests", .. StructuralLints.Select(n => "Infrastructure." + n)]), sel.Gate);
    }

    [Fact]
    public void ResxChange_AddsTheLocalizationLints_AndEveryClassOfTheLocalizationFolder()
    {
        AddLints();
        AddTest("Infrastructure", "LocalizationSweepTests");
        AddTest("Infrastructure", "StaticLocalizationRatchetTests");
        AddTest("Localization", "PluralStringTests");
        AddTest("Core", "NoteTests");

        var sel = Select("DotCharter.Localization/Resources.de-DE.resx");

        Assert.Contains("Infrastructure.LocalizationSweepTests", sel.Gate);
        Assert.Contains("Infrastructure.StaticLocalizationRatchetTests", sel.Gate);
        Assert.Contains("Localization.PluralStringTests", sel.Gate);
        Assert.DoesNotContain("Core.NoteTests", sel.Gate);
    }

    [Fact]
    public void ChangedView_SelectsTheHeadlessClassesThatNameIt_AndLeavesTheGoldensToTheirLane()
    {
        AddLints();
        AddTest("Perf", "AboutDialogUiPerfSmokeTests");
        AddTest("Perf", "ScreenGoldenUiPerfSmokeTests", "var v = new AboutDialog();");
        AddTest("Perf", "SettingsUiPerfSmokeTests", "var v = new SettingsView();");

        var sel = Select("DotCharter.UI/Views/Dialogs/AboutDialog.axaml");

        Assert.Equal(["Perf.AboutDialogUiPerfSmokeTests"], sel.Headless);
        Assert.DoesNotContain("Perf.AboutDialogUiPerfSmokeTests", sel.Gate);
        Assert.Equal(["Perf.ScreenGoldenUiPerfSmokeTests"], sel.Own);
    }

    [Fact]
    public void ReleaseLaneClasses_AreListedNotRun_UnlessTheirOwnTestFileChanged()
    {
        AddTest("Services", "CorpusTests", "var s = new Note();");
        var lane = "DotCharter.Tests.Services.CorpusTests";

        var viaName = Select("DotCharter.Launcher/Note.cs", lane: lane);
        var viaEdit = Select("DotCharter.Tests/Services/CorpusTests.cs", lane: lane);

        Assert.DoesNotContain("Services.CorpusTests", viaName.Gate);
        Assert.Equal(["Services.CorpusTests"], viaName.Lane);
        Assert.Contains("Services.CorpusTests", viaEdit.Gate);
        Assert.Empty(viaEdit.Lane);
    }

    [Fact]
    public void MirrorFolder_IsTheDeepestExistingOne_AndOnlyWhenItIsSmall()
    {
        AddTest("Services/Editing", "FooTests");
        AddTest("Services/Editing/Sub", "BarTests");
        AddTest("Services/Other", "OtherTests");
        AddTest("Audio/Dsp", "DspTests");
        for (var i = 0; i < 41; i++)
            AddTest("Rendering", "Flat" + i + "Tests");

        var editing = Select("DotCharter.Services/Editing/Deep/Thing.cs");
        var tone = Select("DotCharter.Tone/Audio/Dsp/Thing.cs");
        var wide = Select("DotCharter.Rendering/Flat/Thing.cs");

        Assert.Equal(["Services.Editing.FooTests", "Services.Editing.Sub.BarTests"], editing.Gate);
        Assert.Equal(["Audio.Dsp.DspTests"], tone.Gate);
        Assert.Empty(wide.Gate);
    }

    [Fact]
    public void StructuralLints_AreAlwaysSelected_AndARenamedOneIsReported()
    {
        AddLints();
        File.Delete(Path.Combine(Repo, "DotCharter.Tests", "Infrastructure", "CommentHygieneTests.cs"));
        AddTest("Core", "NoteTests");

        var sel = Select("DotCharter.Tests/Core/NoteTests.cs");

        Assert.DoesNotContain("Infrastructure.CommentHygieneTests", sel.Gate);
        Assert.Contains("Infrastructure.DocReferenceLintTests", sel.Gate);
        Assert.Contains("CommentHygieneTests", sel.Notes);
    }

    [Fact]
    public void NoChange_SelectsNothing_AndSaysSo()
    {
        AddLints();

        var output = Run("$idx = New-AffectedTestIndex '@@REPO@@'\n$sel = Get-AffectedSelection @() $idx\n(Format-AffectedSelection $sel 'origin/main') -join '|'");

        Assert.Equal("affected: nothing changed against origin/main - nothing to run", output);
    }

    [Fact]
    public void SelectionWiderThanTheCap_FallsBackToTheFullGate()
    {
        AddLints();
        AddTest("Core", "NoteTests");

        var sel = Select("DotCharter.Tests/Core/NoteTests.cs", max: 3);

        Assert.Contains("more than 3", sel.Fallback);
    }

    [Fact]
    public void FullGateFallback_IsToolsProjectsPropsTargetsGlobalConfigAndTheGitlink()
    {
        var output = Run("""
            $cases = @(
              'tools/agent/Invoke-Gate.ps1', 'tools/ci/gate-shard-weights.json', 'DotCharter.Core/DotCharter.Core.csproj',
              'Directory.Build.props', 'DotCharter.UI/Directory.Solution.targets', 'DotCharter.sln', 'global.json',
              '.editorconfig', 'JuceSharp',
              'DotCharter.Core/Models/Note.cs', 'docs/agents/workflow.md', 'DotCharter.Localization/Resources.resx',
              'DotCharter.UI/Views/AboutDialog.axaml', 'DotCharter.Tests/Core/NoteTests.cs', 'README.md'
            )
            ($cases | ForEach-Object { $_ + '=' + [bool](Get-AffectedFallbackReason @($_)) }) -join '|'
            """);

        var table = output.Split('|').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1] == "True");
        Assert.All(
            ["tools/agent/Invoke-Gate.ps1", "tools/ci/gate-shard-weights.json", "DotCharter.Core/DotCharter.Core.csproj",
             "Directory.Build.props", "DotCharter.UI/Directory.Solution.targets", "DotCharter.sln", "global.json",
             ".editorconfig", "JuceSharp"],
            path => Assert.True(table[path], path + " needs the full gate"));
        Assert.All(
            ["DotCharter.Core/Models/Note.cs", "docs/agents/workflow.md", "DotCharter.Localization/Resources.resx",
             "DotCharter.UI/Views/AboutDialog.axaml", "DotCharter.Tests/Core/NoteTests.cs", "README.md"],
            path => Assert.False(table[path], path + " is selectable"));
    }

    [Fact]
    public void AScriptChange_SkipsTheIndexAndNamesTheFallback()
    {
        var output = Run("$idx = New-AffectedTestIndex '@@REPO@@'\n$sel = Get-AffectedSelection @('tools/agent/Merge-Train.ps1', 'docs/a.md') $idx\n(Format-AffectedSelection $sel 'origin/main') -join '|'");

        Assert.Contains("FULL GATE - tools/agent/Merge-Train.ps1 is under tools/", output);
        Assert.DoesNotContain("classes selected", output);
    }

    // ---- the diff ----

    [Fact]
    public void ChangedPaths_AreCommittedUncommittedAndUntracked_AndBothSidesOfARename()
    {
        var output = Run("""
            $ErrorActionPreference = 'Continue'
            git init -q -b main
            git config user.email t@example.invalid
            git config user.name t
            git config core.autocrlf false
            Set-Content a.cs 'a'; Set-Content b.md 'b'; Set-Content old.cs 'o'
            git add -A; git commit -q -m one
            git branch base
            Set-Content b.md 'b2'; Set-Content e.cs 'e'; git mv old.cs new.cs
            git add -A; git commit -q -m two
            Set-Content a.cs 'a2'; Set-Content c.cs 'c'
            $work = (Get-AffectedChangedPaths (Get-Location).Path 'base').Paths -join ','
            $committed = (Get-AffectedChangedPaths (Get-Location).Path 'base' 'HEAD').Paths -join ','
            "$work|$committed"
            """, repoRoot: true);

        Assert.Equal("a.cs,b.md,c.cs,e.cs,new.cs,old.cs|b.md,e.cs,new.cs,old.cs", output);
    }

    // ---- the run ----

    [Theory]
    [InlineData("1 4", 1)]
    [InlineData("31 4", 2)]
    [InlineData("500 4", 4)]
    [InlineData("0 4", 1)]
    [InlineData("500 1", 1)]
    public void ShardCount_OneProcessPerThirtySecondsOfWork_AtMostTheShardCount(string args, int expected) =>
        Assert.Equal(expected.ToString(), Run("Get-AffectedShardCount " + string.Join(" ", args.Split(' '))));

    [Fact]
    public void Estimate_SumsTheMeasuredSeconds_AndCountsAnUnlistedClassAtAFraction()
    {
        var output = Run("(Get-AffectedEstimateSeconds @('A', 'B', 'C') @{ A = 10.5; B = 2 }).ToString([Globalization.CultureInfo]::InvariantCulture)");

        Assert.Equal("12.6", output);
    }

    [Fact]
    public void Report_NamesEveryClassWithItsReason_AndTheCountsPerReason()
    {
        AddLints();
        AddTest("Other", "BeatMapTests", "var n = new Note(1);");

        var output = Run("""
            $idx = New-AffectedTestIndex '@@REPO@@'
            $sel = Get-AffectedSelection @('DotCharter.Launcher/Note.cs') $idx
            (Format-AffectedSelection $sel 'origin/main' $idx.Classes.Count) -join "`n"
            """);

        Assert.Contains("affected: 7 class(es) selected of 7 test classes - 7 gate, 0 headless", output);
        Assert.Contains("by reason: names type 1, structural lint 6", output);
        Assert.Contains("Other.BeatMapTests  <- names type Note", output);
        Assert.Contains("Infrastructure.CommentHygieneTests  <- always runs", output);
    }

    // ---- this checkout ----

    /// <summary>
    ///     The index is built from source, the gate from the built assembly: every class the
    ///     release lane declares (a lint proves each exists) must be in it, and every lint the
    ///     rules name must resolve, or a rename would silently drop a class from the selection.
    /// </summary>
    [Fact]
    public void RealTestProject_IndexHoldsTheReleaseLane_AndEveryLintTheRulesName()
    {
        var root = TestPaths.RepoRoot();
        var lane = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "ci", "release-lane.json")))
            .RootElement.GetProperty("classes").EnumerateObject().Select(p => p.Name).ToArray();

        var output = Run($$"""
            $idx = New-AffectedTestIndex '{{root.Replace("'", "''")}}'
            $sel = Get-AffectedSelection @('DotCharter.Localization/Resources.resx', 'docs/agents/workflow.md') $idx
            $missing = @('{{string.Join("','", lane)}}' -split ',' | Where-Object { -not $idx.Classes.ContainsKey($_) })
            'count=' + $idx.Classes.Count + '|missing=' + ($missing -join ',') + '|notes=' + (@($sel.Notes) -join ';')
            """);

        var fields = output.Split('|').Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1]);
        Assert.True(int.Parse(fields["count"]) > 1500, $"The index holds {fields["count"]} test classes.");
        Assert.True(fields["missing"] == "", "Release-lane classes missing from the index: " + fields["missing"]);
        Assert.True(fields["notes"] == "", "Lints named by AffectedTests.psm1 that are not test classes: " + fields["notes"]);
    }

    [Fact]
    public void InvokeGate_ImportsTheModule_AndRefusesToCombineAffectedWithAnotherSuite()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "Invoke-Gate.ps1"));
        Assert.Contains("'AffectedTests.psm1'", script);

        var output = RunGate("-Affected -Gate");

        Assert.StartsWith("2|", output);
        Assert.Contains("-Affected picks its own classes", output);
    }

    [Fact]
    public void InvokeGate_AffectedSelectOnly_AgainstAnEmptyRange_RunsNothingAndExitsGreen()
    {
        var output = RunGate("-Affected -SelectOnly -Base HEAD -Head HEAD");

        Assert.StartsWith("0|", output);
        Assert.Contains("nothing changed against HEAD..HEAD - nothing to run", output);
    }

    // ---- helpers ----

    private sealed record Selection(string[] Gate, string[] Headless, string[] Lane, string[] Own, string Fallback, string Notes, Dictionary<string, string> Reasons);

    private Selection Select(string paths, string lane = "", int max = 600)
    {
        var output = Run($$"""
            $idx = New-AffectedTestIndex '@@REPO@@'
            $sel = Get-AffectedSelection {{PsArray(paths)}} $idx @({{(lane.Length == 0 ? "" : "'" + lane + "'")}}) {{max}}
            $short = { param($names) (@($names) | ForEach-Object { $_ -replace '^DotCharter\.Tests\.', '' } | Sort-Object) -join ',' }
            $reasons = foreach ($i in (@($sel.Gate) + @($sel.Headless))) { ($i.FullName -replace '^DotCharter\.Tests\.', '') + '=' + (($i.Reasons | ForEach-Object { $_.Text }) -join '; ') }
            'gate=' + (& $short ($sel.Gate | ForEach-Object { $_.FullName })) + "`n" +
            'headless=' + (& $short ($sel.Headless | ForEach-Object { $_.FullName })) + "`n" +
            'lane=' + (& $short $sel.LeftToLane) + "`n" +
            'own=' + (& $short $sel.OwnLane) + "`n" +
            'fallback=' + $sel.Fallback + "`n" +
            'notes=' + (@($sel.Notes) -join ';') + "`n" +
            ($reasons -join "`n")
            """);
        var lines = output.Replace("\r", "").Split('\n');
        string Field(string name) => lines.First(l => l.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];
        string[] List(string name) => Field(name).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var reasons = lines.Skip(6).Where(l => l.Contains('=')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
        return new Selection(List("gate"), List("headless"), List("lane"), List("own"), Field("fallback"), Field("notes"), reasons);
    }

    private static string Reasons(Selection sel, string cls) => sel.Reasons.TryGetValue(cls, out var r) ? r : "";

    private static string[] Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.OrdinalIgnoreCase)];

    private void AddLints()
    {
        foreach (var name in StructuralLints)
            AddTest("Infrastructure", name);
    }

    /// <summary>One test class, in a file named after it under <c>DotCharter.Tests/&lt;folder&gt;</c>.</summary>
    private void AddTest(string folder, string name, string body = "")
    {
        var ns = "DotCharter.Tests." + folder.Replace('/', '.');
        Write($"DotCharter.Tests/{folder}/{name}.cs", $$"""
            namespace {{ns}};

            public sealed class {{name}}
            {
                [Fact]
                public void Runs()
                {
                    {{body}}
                }
            }
            """);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
    }

    private static string PsArray(string joined) =>
        "@(" + string.Join(", ", joined.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => $"'{p.Replace("'", "''")}'")) + ")";

    private string Run(string body, bool repoRoot = false)
    {
        var text = body.Replace("@@REPO@@", Repo.Replace("'", "''"));
        if (repoRoot)
            text = $"Set-Location '{Repo.Replace("'", "''")}'\n" + text;
        return _session.Run(_scratch, text);
    }

    /// <summary>
    ///     One Windows PowerShell host with <c>AffectedTests.psm1</c> imported once. A case is a script file
    ///     the host dot-runs in its own scope; it answers with the case's output and a done marker, so a
    ///     failure carries the case's own error text.
    /// </summary>
    public sealed class ModuleSession : IDisposable
    {
        private const string Done = "@@case-done@@";

        private readonly string _folder = Path.Combine(
            Path.GetTempPath(), "dotcharter-affected-session-" + Guid.NewGuid().ToString("N"));

        private readonly System.Text.StringBuilder _stderr = new();
        private Process? _process;

        public string Run(string scratchDirectory, string body)
        {
            var process = _process ??= Start();
            var file = Path.Combine(scratchDirectory, "case-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(file, "$ErrorActionPreference = 'Stop'\n" + body + "\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            lock (_stderr)
                _stderr.Clear();
            process.StandardInput.WriteLine(file);
            process.StandardInput.Flush();

            var output = new System.Text.StringBuilder();
            while (true)
            {
                var read = process.StandardOutput.ReadLineAsync();
                // A deadlock detector for a hung host, never a budget.
                Assert.True(read.Wait(TimeSpan.FromSeconds(120)), "powershell did not answer within 120 s.");
                var line = read.Result;
                Assert.True(line is not null, "powershell exited." + Environment.NewLine + StdErr());
                if (line!.StartsWith(Done, StringComparison.Ordinal))
                {
                    Assert.True(line == Done + "0", "The case failed." + Environment.NewLine + StdErr() + Environment.NewLine + output);
                    return output.ToString().Trim();
                }

                output.AppendLine(line);
            }
        }

        public void Dispose()
        {
            if (_process is { HasExited: false } process)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(10_000))
                    process.Kill();
            }

            _process?.Dispose();
            BestEffortDelete.Directory(_folder);
        }

        private string StdErr()
        {
            lock (_stderr)
                return _stderr.ToString();
        }

        private Process Start()
        {
            Directory.CreateDirectory(_folder);
            var module = Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "AffectedTests.psm1");
            var loop = Path.Combine(_folder, "session.ps1");
            File.WriteAllText(loop, $$"""
                $ErrorActionPreference = 'Stop'
                Import-Module '{{module.Replace("'", "''")}}' -Force
                while ($true) {
                  $case = [Console]::In.ReadLine()
                  if ($null -eq $case) { break }
                  $failed = 0
                  $here = (Get-Location).Path
                  try { & $case | ForEach-Object { [Console]::Out.WriteLine("$_") } }
                  catch { $failed = 1; [Console]::Error.WriteLine(($_ | Out-String)) }
                  finally { Set-Location -LiteralPath $here }
                  [Console]::Out.WriteLine('{{Done}}' + $failed)
                  [Console]::Out.Flush()
                }
                """, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var info = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", loop })
                info.ArgumentList.Add(argument);

            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip("Windows PowerShell is not available, so the selection module cannot be executed here.");
                return null!;
            }

            Assert.NotNull(process);
            process!.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (_stderr)
                    _stderr.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
            return process;
        }
    }

    /// <summary>Runs the real <c>Invoke-Gate.ps1</c> with the arguments; returns "exit|stdout".</summary>
    private static string RunGate(string arguments)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = TestPaths.RepoRoot()
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "Invoke-Gate.ps1") }.Concat(arguments.Split(' ')))
            info.ArgumentList.Add(a);
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("Windows PowerShell is not available, so Invoke-Gate.ps1 cannot be executed here.");
            return "";
        }

        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        process.StandardError.ReadToEnd();
        // A deadlock detector for a hung child, never a budget.
        Assert.True(process.WaitForExit(240_000), "Invoke-Gate.ps1 did not finish within 240 s.");
        return process.ExitCode + "|" + stdout.GetAwaiter().GetResult().Replace("\r", "").Trim();
    }
}
