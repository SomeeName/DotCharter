using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/agent/MergeTrain.psm1</c> — the decisions
///     <c>tools/agent/Merge-Train.ps1</c> makes from data alone (#668): whether a
///     train's change is docs only, and so takes the fast path (the build plus
///     the docs lints) instead of the full gate; and which test hosts are the
///     train's own to kill. Executed against the module itself under Windows
///     PowerShell 5.1.
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

    private static string PsArray(string joined) =>
        "@(" + string.Join(", ", joined.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => $"'{p.Replace("'", "''")}'")) + ")";

    private string Run(string body) => HeadlessShardsModule.RunModule(_scratch, body, "tools", "agent", "MergeTrain.psm1");
}
