using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Separation lives on <c>StemSeparationViewModel</c>, but the stem mixer's
///     visibility is the shell's. The view model raises <c>StemsApplied</c> once the
///     stems are registered and the mixer rebuilt, and the shell opens the mixer on
///     it. Drop either half and the stems still apply — the mixer just stops
///     opening, and nothing else would fail.
/// </summary>
public sealed class StemMixerOpensAfterSeparationTests
{
    [Fact]
    public void TheSeparationViewModel_DeclaresStemsApplied()
    {
        var applied = typeof(StemSeparationViewModel).GetEvent("StemsApplied");

        Assert.NotNull(applied);
        Assert.Equal(typeof(Action), applied.EventHandlerType);
    }

    [Fact]
    public void TheSeparationViewModel_RaisesStemsApplied_AfterRebuildingTheMixer()
    {
        var source = ReadShellSource("StemSeparationViewModel.cs");

        Assert.Matches(new Regex(@"_stemMixer\.Rebuild\(\);\s*StemsApplied\?\.Invoke\(\);"), source);
    }

    [Fact]
    public void TheShell_OpensTheStemMixer_WhenStemsAreApplied()
    {
        var source = ReadShellSource("MainWindowViewModel.cs");

        Assert.Matches(new Regex(@"StemSeparation\.StemsApplied\s*\+=\s*OnStemsApplied;"), source);
        Assert.Matches(new Regex(@"void\s+OnStemsApplied\(\)\s*(\{\s*IsStemMixerVisible\s*=\s*true;\s*\}|=>\s*IsStemMixerVisible\s*=\s*true;)"), source);
    }

    private static string ReadShellSource(string fileName)
    {
        return UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", fileName)));
    }
}
