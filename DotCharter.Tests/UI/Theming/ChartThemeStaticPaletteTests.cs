using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Media;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.Tests.UI.Theming;

/// <summary>
///     <b>A custom-drawn control may not COPY a themed colour into a static
///     brush.</b>
///     <para>
///         <c>ChartTheme.Apply</c> repaints its shared brush instances in place
///         on every palette change — a theme preset, a customized token, a
///         colour-blind string preset — so a control that draws with
///         <c>ChartTheme.GoldBrush</c> follows the palette for free. A control
///         that instead did <c>new SolidColorBrush(ChartTheme.GoldColor)</c> in
///         a <c>static readonly</c> field captured the colour ONCE, at type
///         init, and never changed again.
///     </para>
///     <para>
///         <c>MinimapOverviewControl</c> had three of them — the seven string
///         brushes, the phrase marker and the waveform stroke. The failure has
///         no exception and no log line: switching the theme (or picking
///         Okabe-Ito for readability, which is an ACCESSIBILITY setting) repainted
///         every other surface and left the overview strip drawing the launch
///         palette. It even looked correct on a cold start, because the type is
///         first touched after the startup theme has been applied.
///     </para>
/// </summary>
public sealed class ChartThemeStaticPaletteTests
{
    /// <summary>The colours <c>ChartTheme.Apply</c> assigns — i.e. the ones that
    /// MOVE. Derived from source so a new themed colour is covered the day it is
    /// added.</summary>
    private static readonly string[] ThemedColorFields = ThemedColorFieldsFromApply();

    // ── The mechanism the minimap now relies on ────────────────────────────

    [Fact]
    public void TheMinimapDrawsWithChartThemesOwnStringBrushInstances()
    {
        var shared = ChartTheme.StringBrushes;
        var minimap = StaticFieldValue<IBrush[]>("StringBrushes");

        Assert.Equal(shared.Count, minimap.Length);
        for (var i = 0; i < minimap.Length; i++)
        {
            Assert.True(ReferenceEquals(shared[i], minimap[i]),
                $"minimap string brush {i} is a COPY of the palette, so a theme change cannot reach it");
        }
    }

    [Fact]
    public void TheMinimapDrawsPhraseMarkersWithChartThemesOwnBrush()
    {
        Assert.True(ReferenceEquals(ChartTheme.GoldBrush, StaticFieldValue<IBrush>("PhraseBrush")),
            "the phrase marker is a copy of ChartTheme.GoldColor, so a theme change cannot reach it");
    }

    // ── …and the lint that stops the next one ──────────────────────────────

    /// <summary>
    ///     Every static field under <c>Controls/</c> seeded from a themed
    ///     <c>ChartTheme.*Color</c> must be repainted by that file's own
    ///     <c>ThemeChanged</c> handler. The alternative — sharing one of
    ///     ChartTheme's brushes — leaves nothing for this to match, which is
    ///     exactly the outcome it wants.
    /// </summary>
    [Fact]
    public void EveryStaticSeededFromAThemedColor_IsRepaintedOnThemeChanged()
    {
        Assert.NotEmpty(ThemedColorFields);

        var inspected = 0;
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(
                     UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls")), "*.cs",
                     SearchOption.AllDirectories))
        {
            var source = SourceText.StripComments(UiSourceTree.ReadAllText(file));
            if (!source.Contains("ChartTheme.", StringComparison.Ordinal)) continue;

            var handler = ThemeChangedHandlerBody(source);
            foreach (Match declaration in StaticFieldDeclaration.Matches(source))
            {
                var name = declaration.Groups["name"].Value;
                var initializer = declaration.Groups["init"].Value;
                if (!ThemedColorFields.Any(c =>
                        initializer.Contains("ChartTheme." + c, StringComparison.Ordinal)))
                {
                    continue;
                }

                inspected++;
                if (handler != null && handler.Contains(name, StringComparison.Ordinal)) continue;
                offenders.Add($"{Path.GetFileName(file)}: {name}");
            }
        }

        Assert.True(inspected > 0,
            "no static field under Controls/ is seeded from a themed ChartTheme colour any more — " +
            "if that is really true, delete this lint rather than leaving it passing on nothing.");
        Assert.True(offenders.Count == 0,
            "These statics copy a colour ChartTheme.Apply moves, and nothing repaints them on a theme " +
            "change — the control keeps drawing the launch palette forever, with no exception and no log " +
            "line. Share the matching ChartTheme brush instead, or repaint the field in the file's own " +
            "ThemeChanged handler:\n  " + string.Join("\n  ", offenders));
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>A FIELD declaration with an initializer — <c>static</c>,
    /// <c>readonly</c> or both — captured as (name, initializer). The access
    /// modifier is what keeps locals out; seeding a colour once and never
    /// writing it again is the same fault whether "once" is per process or per
    /// instance, because a chart layer lives as long as the renderer that owns
    /// it.</summary>
    private static readonly Regex StaticFieldDeclaration = new(
        @"\b(?:private|internal|protected|public)\s+(?:static\s+readonly|static|readonly)\s+" +
        @"[\w<>\[\],\.\?\s]+?\b(?<name>\w+)\s*=\s*(?<init>[^;]+);",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static string? ThemeChangedHandlerBody(string source)
    {
        var at = source.IndexOf("void OnThemeChanged(", StringComparison.Ordinal);
        if (at < 0) return null;
        var open = source.IndexOf('{', at);
        if (open < 0) return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }

        return null;
    }

    /// <summary>The <c>public static Color X = …;</c> fields that
    /// <c>ChartTheme.Apply</c> ASSIGNS. A colour Apply never writes (SkyBlue,
    /// Cyan, …) is a fixed brand value and copying it is fine.</summary>
    private static string[] ThemedColorFieldsFromApply()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Themes", "ChartTheme.cs")));
        var at = source.IndexOf("private static void Apply(IThemeService svc)", StringComparison.Ordinal);
        Assert.True(at >= 0, "ChartTheme no longer declares Apply(IThemeService) — repoint this lint.");

        var open = source.IndexOf('{', at);
        var depth = 0;
        var end = source.Length;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0) { end = i; break; }
        }

        return Regex.Matches(source[open..end], @"^\s*(?<name>\w+Color)\s*=(?!=)", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static T StaticFieldValue<T>(string name)
    {
        var field = typeof(MinimapOverviewControl).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field != null, $"MinimapOverviewControl no longer declares `{name}` — repoint this test.");
        return (T)field!.GetValue(null)!;
    }
}
