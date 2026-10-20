using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The markup half of #412: a grammar is included at ONE level, and nothing is
///     included at the main window's own scope. A style attaches to every control its
///     selector's type matches inside the scope that declares it, worn or not, so a
///     grammar at window scope costs every screen and dialog, and one included by a
///     view AND by the view hosting it is attached twice to every control inside.
///     The headless half, on the real prewarmed tree, is
///     <c>Perf/StyleIncludeScopeUiPerfSmokeTests</c>.
/// </summary>
public sealed class StyleIncludeScopeTests
{
    private static readonly Regex Include = new("<StyleInclude\\s+Source=\"(?<src>[^\"]+)\"", RegexOptions.Compiled);

    /// <summary>The settings shell hosts every other Settings screen in its ScreenTabHost, and
    /// each of those includes its own grammar — so the shell may include none of them.</summary>
    [Fact]
    public void TheSettingsShell_IncludesNoGrammarItsTabsInclude()
    {
        var settings = ScreenCatalog.Default.Entries.Where(e => e.Area == ScreenCatalog.Settings).ToList();
        var shell = settings.Single(e => e.ViewType.Name == "SettingsShellView");
        var shellIncludes = IncludesIn(AxamlOf(shell.ViewType));

        var tabs = settings.Where(e => e != shell).ToList();
        Assert.True(tabs.Count >= 10, $"only {tabs.Count} settings tabs are in the catalog — the walk is broken");

        var offenders = new List<string>();
        foreach (var tab in tabs)
        {
            var includes = IncludesIn(AxamlOf(tab.ViewType));
            if (!includes.Contains("avares://DotCharter/Views/Settings/SettingsStyles.axaml"))
                offenders.Add($"{tab.ViewType.Name} does not include SettingsStyles.axaml itself (its rows render unstyled)");
            foreach (var both in includes.Intersect(shellIncludes, StringComparer.Ordinal))
                offenders.Add($"{tab.ViewType.Name} and SettingsShellView both include {both}");
        }

        Assert.True(offenders.Count == 0,
            "A grammar the shell and a tab both include is attached TWICE to every control in that tab (#412):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>MainWindow's own Styles cover Play Mode, the song browser, Settings and every
    /// prewarmed dialog: nothing may be included or declared there.</summary>
    [Fact]
    public void MainWindow_DeclaresNoStylesAtWindowScope()
    {
        var doc = MainWindowSource.Document();
        var windowStyles = doc.Root!.Elements().Where(e => e.Name.LocalName == "Window.Styles").SelectMany(e => e.Elements()).ToList();
        Assert.True(windowStyles.Count == 0,
            "MainWindow.axaml declares " + string.Join(", ", windowStyles.Select(e => e.Name.LocalName)) + " in Window.Styles. " +
            "That scope is every screen and dialog of the app: its rules attach to each control of their type there (#412). " +
            "Put it on the element that wears it.");

        // Anti-vacuity: the workspace grammar is still included, on an element inside the editor.
        var scoped = doc.Descendants().Where(e => e.Name.LocalName.EndsWith(".Styles", StringComparison.Ordinal)
                                                  && e.Elements().Any(i => i.Name.LocalName == "StyleInclude")).ToList();
        Assert.True(scoped.Count >= 1, "MainWindow.axaml includes the workspace grammar nowhere — the editor's rails render unstyled");
        foreach (var styles in scoped)
        {
            Assert.True(styles.Parent!.Ancestors().Any(a => (string?)a.Attribute("Name") == "MainSplitGrid"
                                                            || (string?)a.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "MainSplitGrid"),
                $"<{styles.Name.LocalName}> in MainWindow.axaml is not inside the editor's MainSplitGrid, so it reaches other screens");
        }
    }

    /// <summary>The grammars whose Border / TextBlock / Grid / StackPanel families became
    /// ControlThemes in #428, and the app-level files whose panel families did.</summary>
    private static readonly string[] ConvertedGrammars =
    [
        "DotCharter.UI/Views/PlayMode/PlayModeStyles.axaml",
        "DotCharter.UI/Views/PlayMode/PlayModeThemes.axaml",
        "DotCharter.UI/Views/Dialogs/DialogStyles.axaml",
        "DotCharter.UI/Views/Dialogs/DialogStyles/ShellAndHead.axaml",
        "DotCharter.UI/Views/Dialogs/DialogStyles/BodyAndStates.axaml",
        "DotCharter.UI/Views/Dialogs/DialogStyles/Foot.axaml",
        "DotCharter.UI/Views/Settings/SettingsStyles.axaml",
        "DotCharter.Tone.UI/Styles/Fire/FirePopoverBody.axaml",
        "DotCharter.Tone.UI/Styles/Fire/FireSectionsAndOverlays.axaml",
        "DotCharter.Tone.UI/Styles/Fire/FireShellRail.axaml",
    ];

    private static readonly Regex ClassRuleOnFamilyType = new(
        @"^(Border|TextBlock|Grid|StackPanel)(\.[A-Za-z0-9_\-]+)+(:[a-z\-]+(\([^)]*\))?)*$", RegexOptions.Compiled);

    /// <summary>
    ///     #428: a `Border.X` / `TextBlock.X` / `Grid.X` / `StackPanel.X` RULE is attached to
    ///     every control of that type in its scope, worn or not; in these grammars each
    ///     family is a ControlTheme its wearers wear, so its rules attach to them alone. The
    ///     headless half, which counts the frames on the real prewarmed shell, is
    ///     <c>Perf/StyleIncludeScopeUiPerfSmokeTests.TheConvertedGrammars_StayWithinTheirRuleFrameCeilings</c>.
    /// </summary>
    [Fact]
    public void TheConvertedGrammars_DeclareTheirFamiliesAsControlThemes()
    {
        var offenders = new List<string>();
        var themes = 0;
        foreach (var relative in ConvertedGrammars)
        {
            var doc = XDocument.Parse(UiSourceTree.ReadAllText(UiSourceTree.Resolve(
                Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)))));
            themes += doc.Descendants().Count(e => e.Name.LocalName == "ControlTheme");
            foreach (var style in doc.Descendants().Where(e => e.Name.LocalName == "Style"
                                                               && e.Ancestors().All(a => a.Name.LocalName != "ControlTheme")))
            {
                foreach (var part in (style.Attribute("Selector")?.Value ?? "").Split(','))
                {
                    if (ClassRuleOnFamilyType.IsMatch(part.Trim()))
                        offenders.Add($"{relative}: Selector=\"{part.Trim()}\"");
                }
            }
        }

        Assert.True(themes >= 60, $"only {themes} ControlThemes in the converted grammars — the walk is broken");
        Assert.True(offenders.Count == 0,
            "A class rule on a Border / TextBlock / Grid / StackPanel is attached to every control of that type in its " +
            "scope (#428). Put it in the family's ControlTheme as a `^.X` rule and give the wearers the theme:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>#428: the settings shell's rules live on its rail, where every control that
    /// wears them is; at the view's own level they reached every control of every tab.</summary>
    [Fact]
    public void TheSettingsShell_KeepsItsRailRulesOnTheRail()
    {
        var doc = XDocument.Parse(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "SettingsShellView.axaml"))));
        var viewLevel = doc.Root!.Elements().Where(e => e.Name.LocalName == "UserControl.Styles")
            .SelectMany(e => e.Elements()).Select(e => e.Attribute("Selector")?.Value ?? e.Name.LocalName).ToList();
        var classRules = viewLevel.Where(s => Regex.IsMatch(s, @"\.[A-Za-z]")).ToList();
        Assert.True(classRules.Count == 0,
            "SettingsShellView declares class rules at the view level, where they reach every tab: " + string.Join(", ", classRules));
        var rail = doc.Descendants().Where(e => e.Name.LocalName == "Border.Styles").SelectMany(e => e.Elements()).Count();
        Assert.True(rail >= 20, $"only {rail} rules on the rail — the shell's look went missing");
    }

    private static HashSet<string> IncludesIn(string axaml) =>
        Include.Matches(UiSourceTree.ReadAllText(axaml)).Select(m => m.Groups["src"].Value).ToHashSet(StringComparer.Ordinal);

    private static string AxamlOf(Type view)
    {
        var root = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var matches = UiSourceTree.EnumerateFiles(root, view.Name + ".axaml", SearchOption.AllDirectories).ToList();
        Assert.True(matches.Count == 1, $"expected one {view.Name}.axaml under DotCharter.UI/Views, found {matches.Count}");
        return matches[0];
    }
}
