using System.Reflection;
using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The usage report's command denominator is the palette catalogue, which is
///     built from the shell, so the shell hands it to the Help menu. Without that
///     wiring the report still opens, with every command missing from its
///     "never used" list — nothing would fail.
/// </summary>
public sealed class HelpMenuWiringTests
{
    [Fact]
    public void TheHelpMenu_TakesItsCommandDenominatorAsASeam()
    {
        var seam = typeof(HelpMenuViewModel).GetProperty("CommandUsageIds",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(seam);
        Assert.Equal(typeof(Func<IEnumerable<string>>), seam.PropertyType);
    }

    [Fact]
    public void TheShell_WiresTheHelpMenusCommandDenominatorToThePaletteCatalogue()
    {
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));

        Assert.Matches(new Regex(@"Help\.CommandUsageIds\s*=\s*\(\)\s*=>\s*CommandCatalog\.Build\(this,\s*_localization\)"), shell);
    }
}
