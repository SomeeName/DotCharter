using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>.claude/skills/dotcharter-worktree/scripts/AgentWorktree.psm1</c> (#613):
///     a new agent worktree takes JuceSharp's TRACKED tree at the gitlink even when
///     the main checkout's HEAD already is the gitlink (its uncommitted edits stay
///     out), extracts it into a folder of its own invocation, and keeps the untracked
///     native pair (the DLL and the VST host) together across the mirror. The module
///     runs under Windows PowerShell 5.1; the script is linted for using it, and is
///     never executed here.
/// </summary>
public sealed class AgentWorktreeModuleTests : IDisposable
{
    private const string Gitlink = "0123456789abcdef0123456789abcdef01234567";

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-agentworktree-" + Guid.NewGuid().ToString("N"));

    public AgentWorktreeModuleTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => DotCharter.Tone.Infrastructure.Storage.BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData(Gitlink, Gitlink, "True|True")]
    [InlineData(Gitlink, "fedcba9876543210fedcba9876543210fedcba98", "True|False")]
    [InlineData("", Gitlink, "False|True")]
    public void SourcePlan_ExtractsTheTrackedTreeWheneverThereIsAGitlink(string gitlink, string copied, string expected) =>
        Assert.Equal(expected, Run(
            $"$p = Get-JuceSourcePlan -Gitlink '{gitlink}' -CopiedHead '{copied}'\n'{{0}}|{{1}}' -f $p.Extract, $p.MainDllFitsGitlink"));

    [Fact]
    public void ExtractDir_IsOwnedByOneInvocation() =>
        Assert.Equal(@"False|C:\t\juce-0123456789ab-one", Run(
            $"$a = Get-JuceExtractDir -TempRoot 'C:\\t' -Gitlink '{Gitlink}' -Token 'one'\n"
            + $"$b = Get-JuceExtractDir -TempRoot 'C:\\t' -Gitlink '{Gitlink}' -Token 'two'\n"
            + "'{0}|{1}' -f ($a -eq $b), $a"));

    [Fact]
    public void NativeRuntime_IsTheDllAndTheVstHost() =>
        Assert.Equal("juce_sharp.dll,juce_vst_host.exe", Run("(Get-JuceNativeRuntimeFiles) -join ','"));

    [Theory]
    [InlineData("'juce_sharp.dll','juce_vst_host.exe'", "'juce_sharp.dll'", "juce_vst_host.exe")]
    [InlineData("'juce_sharp.dll','juce_vst_host.exe'", "'juce_sharp.dll','juce_vst_host.exe'", "")]
    [InlineData("@()", "@()", "")]
    public void NativeRuntimeLoss_NamesWhatTheMirrorRemoved(string before, string after, string expected) =>
        Assert.Equal(expected, Run($"(Get-JuceNativeRuntimeLoss -Before @({before}) -After @({after})) -join ','"));

    /// <summary>The script wraps the call in <c>@( ... )</c> and checks <c>.Count</c>, so the helper must stream one item per lost file.</summary>
    [Theory]
    [InlineData("'juce_sharp.dll','juce_vst_host.exe'", "'juce_sharp.dll','juce_vst_host.exe'", "0")]
    [InlineData("@()", "@()", "0")]
    [InlineData("'juce_sharp.dll','juce_vst_host.exe'", "'juce_sharp.dll'", "1")]
    [InlineData("'juce_sharp.dll','juce_vst_host.exe'", "@()", "2")]
    public void NativeRuntimeLoss_CountsLostFilesTheWayTheScriptUsesIt(string before, string after, string expected) =>
        Assert.Equal(expected, Run(
            $"$lost = @(Get-JuceNativeRuntimeLoss -Before @({before}) -After @({after}))\n$lost.Count"));

    [Fact]
    public void NativeRuntimeLoss_MessageNamesTheFileNotTheType()
    {
        var message = Run(
            "$lost = @(Get-JuceNativeRuntimeLoss -Before @('juce_sharp.dll','juce_vst_host.exe') -After @('juce_sharp.dll'))\n"
            + "\"The native runtime lost $($lost -join ', ') while JuceSharp was replaced\"");
        Assert.Contains("juce_vst_host.exe", message);
        Assert.DoesNotContain("System.String", message);
    }

    [Fact]
    public void Script_CountsTheLossAsAnArrayOfFiles()
    {
        var code = Code(Read());
        Assert.Matches(@"\$lost = @\(Get-JuceNativeRuntimeLoss -Before \$before -After @\(Get-PresentNativeFiles\)\)", code);
        Assert.Matches(@"if \(\$lost\.Count -gt 0\)", code);
    }

    [Theory]
    [InlineData("'juce_sharp.dll','juce_vst_host.exe'", "True")]
    [InlineData("@()", "True")]
    [InlineData("'juce_sharp.dll'", "False")]
    [InlineData("'juce_vst_host.exe'", "False")]
    public void NativeRuntimePair_IsBothOrNeither(string present, string expected) =>
        Assert.Equal(expected, Run($"Test-JuceNativeRuntimePair -Present @({present})"));

    [Fact]
    public void Script_DecidesThroughTheModule()
    {
        var code = Code(Read());
        Assert.Contains("'AgentWorktree.psm1'", code);
        Assert.Matches(@"(?m)^if \(\$plan\.Extract\) \{", code);
        Assert.Contains("Get-JuceExtractDir", code);
        Assert.Contains("Get-JuceNativeRuntimeLoss", code);
        Assert.Contains("Test-JuceNativeRuntimePair", code);
        Assert.DoesNotContain("$gitlink -ne $copied", code);
        Assert.DoesNotContain("\"juce-\" +", code);
        Assert.DoesNotContain("juce_sharp.dll.keep", code);
    }

    /// <summary>The scratch folders are removed in a <c>finally</c>, so a failed extraction leaves nothing behind for the next one.</summary>
    [Fact]
    public void Script_CleansItsOwnScratchInFinally()
    {
        var code = Code(Read());
        var extract = code.IndexOf("if ($plan.Extract) {", StringComparison.Ordinal);
        var cleanup = code.IndexOf("finally {", extract, StringComparison.Ordinal);
        Assert.True(extract >= 0 && cleanup > extract, "the extraction has no finally");
        Assert.Contains("@($tree, $keepDir)", code[cleanup..]);
    }

    [Theory]
    [InlineData(false, false, "install pinned")]
    [InlineData(true, false, "keep")]
    [InlineData(false, true, "keep")]
    public void NativePairPlan_UsesPinnedFallbackWhenNoLocalBuildFits(bool matching, bool sibling, string expected) =>
        Assert.Equal(expected, Run($$"""
            $entry = [pscustomobject]@{ commit = '{{Gitlink}}'; files = @{ 'juce_sharp.dll' = ('a' * 64); 'juce_vst_host.exe' = ('b' * 64) } }
            $hashes = @{ 'juce_sharp.dll' = ('c' * 64); 'juce_vst_host.exe' = ('d' * 64) }
            if (${{matching}}) { $hashes = $entry.files }
            (Get-JuceNativePairPlan -Gitlink '{{Gitlink}}' -CopiedHead ('f' * 40) -SiblingFound ${{sibling}} -ManifestEntry $entry -Hashes $hashes).Action
            """));

    [Fact]
    public void NativePairPlan_MissingPinWarnsWithGitlink() =>
        Assert.Equal($"warn|No native runtime pin for JuceSharp {Gitlink}.", Run($$"""
            $p = Get-JuceNativePairPlan -Gitlink '{{Gitlink}}' -CopiedHead '{{Gitlink}}' -SiblingFound $true -Hashes @{}
            "$($p.Action)|$($p.Reason)"
            """));

    [Fact]
    public void NativePairPlan_FailedInstallWarnsWithItsReason() =>
        Assert.Equal("warn|asset unavailable", Run($$"""
            $entry = [pscustomobject]@{ commit = '{{Gitlink}}'; files = @{ 'juce_sharp.dll' = ('a' * 64); 'juce_vst_host.exe' = ('b' * 64) } }
            $p = Get-JuceNativePairPlan -Gitlink '{{Gitlink}}' -ManifestEntry $entry -Hashes @{} -InstallAttempted -FailureReason 'asset unavailable'
            "$($p.Action)|$($p.Reason)"
            """));

    [Fact]
    public void Script_InstallsAndRechecksPinnedPair()
    {
        var code = Code(Read());
        Assert.Contains("Get-JuceNativePairPlan", code);
        Assert.Contains("Get-JuceNativePairState -RepoRoot $wt -Gitlink $gitlink", code);
        Assert.Contains("'tools/setup/Get-NativeRuntime.ps1'", code);
        Assert.Matches(@"-File \$installer -Force", code);
        Assert.Contains("-InstallAttempted", code);
        Assert.Contains("ConvertTo-Json -Compress", code);
        Assert.Contains("native-pair: ", code);
        Assert.Contains("$nativePlan.Reason", code);
    }

    [Fact]
    public void NativePairState_ReadsThisTreesManifestAndRehashesBothFiles()
    {
        var setup = Directory.CreateDirectory(Path.Combine(_scratch, "tools", "setup")).FullName;
        File.Copy(Path.Combine(TestPaths.RepoRoot(), "tools", "setup", "NativeRuntime.psm1"), Path.Combine(setup, "NativeRuntime.psm1"));
        var native = Directory.CreateDirectory(Path.Combine(_scratch, "JuceSharp", "src", "JuceSharp", "runtimes", "win-x64", "native")).FullName;
        File.WriteAllText(Path.Combine(native, "juce_sharp.dll"), "dll");
        File.WriteAllText(Path.Combine(native, "juce_vst_host.exe"), "host");
        var files = Directory.GetFiles(native).ToDictionary(p => Path.GetFileName(p)!, p =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
        File.WriteAllText(Path.Combine(setup, "native-runtime.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            repository = "SomeeName/JuceSharp",
            release = "native-prebuilt",
            runtimes = new[] { new { commit = Gitlink, files } }
        }));
        var output = Run($$"""
            $s = Get-JuceNativePairState -RepoRoot '{{_scratch}}' -Gitlink '{{Gitlink}}'
            "$($s.ManifestEntry.commit)|$($s.Hashes['juce_sharp.dll'])|$($s.Hashes['juce_vst_host.exe'])|$($s.FailureReason)"
            [IO.File]::WriteAllText('{{Path.Combine(native, "juce_vst_host.exe")}}', 'changed')
            $s = Get-JuceNativePairState -RepoRoot '{{_scratch}}' -Gitlink '{{Gitlink}}'
            $s.Hashes['juce_vst_host.exe'] -eq $s.ManifestEntry.files.'juce_vst_host.exe'
            Remove-Item -LiteralPath '{{Path.Combine(native, "juce_vst_host.exe")}}'
            $s = Get-JuceNativePairState -RepoRoot '{{_scratch}}' -Gitlink '{{Gitlink}}'
            $s.Hashes.ContainsKey('juce_vst_host.exe')
            """);
        Assert.Equal($"{Gitlink}|{files["juce_sharp.dll"]}|{files["juce_vst_host.exe"]}|\nFalse\nFalse",
            output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_RejectsAnIncompleteSiblingBeforeCopyingEitherFile()
    {
        var code = Code(Read());
        var check = code.IndexOf("if (-not (Test-Path $vstHost)) { continue }", StringComparison.Ordinal);
        var copy = code.IndexOf("Copy-Item $cDll $dll -Force", StringComparison.Ordinal);
        Assert.True(check >= 0 && copy > check, "both sibling files must exist before either is copied");
    }

    private static string Read() => File.ReadAllText(Path.Combine(
        TestPaths.RepoRoot(), ".claude", "skills", "dotcharter-worktree", "scripts", "New-AgentWorktree.ps1"));

    private static string Code(string text) => string.Join('\n', Regex.Replace(text, "<#.*?#>", "", RegexOptions.Singleline)
        .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.TrimStart().StartsWith('#')));

    private string Run(string body) => HeadlessShardsModule.RunModule(
        _scratch, body, ".claude", "skills", "dotcharter-worktree", "scripts", "AgentWorktree.psm1");
}
