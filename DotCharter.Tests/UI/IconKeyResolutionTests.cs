using System.Text.RegularExpressions;
using DotCharter.Presentation.Contracts.Home;
using DotCharter.Presentation.Fakes;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>An icon resource key that does not exist renders as NOTHING</b> — no
///     exception, no log line, just a control with a blank space where the
///     glyph should be. It is the failure mode
///     <c>ShellConstructionUiPerfSmokeTests.AssertIconsResolved</c> exists for,
///     and it reaches further than the shell: a screen contract that carries an
///     icon KEY (rather than a glyph) moves the mistake into data, where the
///     compiler cannot see it at all.
///     <para>
///         The Home screen's destination cards do exactly that, and the first
///         one shipped with <c>IconChart</c> — which is not a key; the real one
///         is <c>IconChartLine</c>. The card rendered with an empty icon column
///         and nothing said so.
///     </para>
/// </summary>
public sealed class IconKeyResolutionTests
{
    [Fact]
    public void EveryIconKeyAFakeSupplies_IsARealResourceKey()
    {
        var declared = DeclaredIconKeys();
        Assert.True(declared.Count > 20, $"only {declared.Count} icon keys found — the sweep is not reaching them");

        var supplied = FakeScreens.VariantsOf(typeof(IHomeScreen))
            .Select(v => (IHomeScreen)v.Create())
            .SelectMany(s => s.Destinations)
            .Select(d => d.IconKey)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(supplied);

        var missing = supplied.Where(k => !declared.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            "these icon keys resolve to nothing, so the control draws a blank space and says nothing:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));
    }

    /// <summary>Every <c>x:Key="Icon…"</c> declared anywhere in the app's markup.</summary>
    private static HashSet<string> DeclaredIconKeys()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in UiSourceTree.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(file), @"x:Key=""(Icon[A-Za-z0-9]*)"""))
                keys.Add(m.Groups[1].Value);
        }

        return keys;
    }
}
