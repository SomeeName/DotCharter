using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     A <c>DynamicResource</c> does NOT convert types. Handing a property that
///     takes a <see cref="Avalonia.Thickness" /> a token declared as an
///     <c>x:Double</c> throws <see cref="InvalidCastException" /> the moment the
///     control attaches — a crash on open, from a build with zero warnings.
///     <para>
///         This is a trap in the token scale itself, not a typo: the spacing
///         scale has <c>Space*</c> (raw doubles, for <c>Spacing</c> on a
///         StackPanel and for <c>ColumnSpacing</c>/<c>RowSpacing</c>) sitting
///         right beside <c>Padding*</c> and <c>Gap*</c> (real Thicknesses), and
///         the names give no hint which is which. Three separate agents reworking
///         three unrelated surfaces shipped it on the same day — dialogs, the
///         shell, and the online hub — which is what makes it worth a lint rather
///         than a code review note.
///     </para>
///     <para>
///         Source-level on purpose: the failure needs the control to ATTACH, so
///         only a view that some test happens to realise would ever catch it, and
///         most views have no such test.
///     </para>
/// </summary>
public sealed class ResourceTokenTypeTests
{
    /// <summary>Properties whose type is <c>Thickness</c>.</summary>
    private static readonly string[] ThicknessProperties =
        ["Padding", "Margin", "BorderThickness"];

    /// <summary>Properties that take a raw <c>double</c>.</summary>
    private static readonly string[] DoubleProperties =
        ["Spacing", "ColumnSpacing", "RowSpacing", "Width", "Height", "MinWidth", "MinHeight",
         "MaxWidth", "MaxHeight", "FontSize", "Opacity", "LineHeight", "StrokeThickness"];

    [Fact]
    public void NoThicknessProperty_IsGivenADoubleToken()
    {
        var doubles = TokensOfKind("x:Double");
        Assert.NotEmpty(doubles); // the scan must not pass by finding no tokens

        var offenders = Scan(ThicknessProperties, doubles,
            "takes a Thickness but the token is an x:Double — DynamicResource does not " +
            "convert, so this throws InvalidCastException when the control attaches");

        Assert.True(offenders.Count == 0, Describe(offenders));
    }

    [Fact]
    public void NoDoubleProperty_IsGivenAThicknessToken()
    {
        var thicknesses = TokensOfKind("Thickness");
        Assert.NotEmpty(thicknesses);

        var offenders = Scan(DoubleProperties, thicknesses,
            "takes a double but the token is a Thickness — the mirror image of the " +
            "Padding-gets-a-Space bug, and it fails the same way");

        Assert.True(offenders.Count == 0, Describe(offenders));
    }

    /// <summary>
    ///     <c>LineHeight</c> has been in <see cref="DoubleProperties" /> since
    ///     this lint was written, so the two facts above already cover the
    ///     <c>LineHeight*</c> / <c>FireLineHeight*</c> ladders added on
    ///     2026-09-12 — but only NEGATIVELY, and a negative fact over a token
    ///     family that might not exist passes vacuously. This is the positive
    ///     half: the ladders are there, and every step on them really is an
    ///     <c>x:Double</c>. Declared as a <c>Thickness</c> (the mirror-image
    ///     typo this class exists for) a step would throw
    ///     <see cref="InvalidCastException" /> on every TextBlock the style
    ///     matches, from a clean build.
    /// </summary>
    [Fact]
    public void EveryLineHeightToken_IsADouble()
    {
        var doubles = TokensOfKind("x:Double");
        var thicknesses = TokensOfKind("Thickness");

        var steps = doubles
            .Where(k => k.StartsWith("LineHeight", StringComparison.Ordinal)
                        || k.StartsWith("FireLineHeight", StringComparison.Ordinal))
            .ToList();

        Assert.True(steps.Count >= 20,
            $"only {steps.Count} LineHeight steps are declared as x:Double — the type ladders have moved.");

        var wrong = thicknesses
            .Where(k => k.StartsWith("LineHeight", StringComparison.Ordinal)
                        || k.StartsWith("FireLineHeight", StringComparison.Ordinal))
            .ToList();
        Assert.True(wrong.Count == 0,
            "these LineHeight steps are declared as a Thickness, which DynamicResource cannot convert to the " +
            "double LineHeight takes:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>Every resource key declared with the given XAML element name,
    /// across the whole UI project — so a token added to any dictionary is
    /// covered, not just the main one.</summary>
    private static HashSet<string> TokensOfKind(string element)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in AxamlFiles())
        {
            foreach (Match m in Regex.Matches(
                     UiSourceTree.ReadAllText(file), $@"<{Regex.Escape(element)}\s+x:Key=""(?<key>[^""]+)"""))
            {
                keys.Add(m.Groups["key"].Value);
            }
        }

        return keys;
    }

    /// <summary>
    ///     The two ways a resource reaches a property, and BOTH have to be
    ///     scanned.
    ///     <para>
    ///         The direct form <c>Padding="{DynamicResource SpaceXs}"</c> is the
    ///         one this lint was written for. The <c>Setter</c> form puts the
    ///         resource in a <c>Value=</c> attribute, so <c>Padding="{</c> never
    ///         appears and the first pattern cannot see it — while it does
    ///         exactly the same thing and fails exactly the same way, on every
    ///         control the selector matches rather than on one. There are ~160
    ///         such assignments under <c>DotCharter.UI</c>, i.e. the lint written
    ///         to stop the bug that "shipped from three separate changes on one
    ///         day" was covering half the syntax it exists for.
    ///     </para>
    /// </summary>
    private static IEnumerable<string> PatternsFor(string[] properties)
    {
        var props = string.Join('|', properties);
        yield return $@"\b(?<prop>{props})=""\{{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z0-9_]+)\}}""";
        yield return $@"<Setter\s+[^>]*?Property=""(?<prop>{props})""[^>]*?" +
                      @"Value=""\{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z0-9_]+)\}""";
    }

    private static List<string> Scan(string[] properties, HashSet<string> wrongKind, string why)
    {
        var offenders = new List<string>();
        var patterns = PatternsFor(properties).ToList();

        foreach (var file in AxamlFiles())
        {
            var text = UiSourceTree.ReadAllText(file);
            foreach (var pattern in patterns)
            {
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    var key = m.Groups["key"].Value;
                    if (!wrongKind.Contains(key)) continue;
                    var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(file)}:{line}  {m.Groups["prop"].Value}=\"{{...{key}}}\" — {why}");
                }
            }
        }

        return offenders;
    }

    /// <summary>
    ///     The lint has to SEE the <c>Setter</c> form, which is not something the
    ///     two facts above can show while the repo is clean — a lint that matches
    ///     nothing passes identically to a lint that matches everything correctly.
    ///     <para>
    ///         So this counts what the patterns reach. The direct form and the
    ///         <c>Setter</c> form must both be present in real numbers; if the
    ///         second pattern were dropped (or broken by an attribute order this
    ///         markup does not use), the <c>Setter</c> count would fall to zero
    ///         and this fails instead of the repo going unguarded.
    ///     </para>
    /// </summary>
    [Fact]
    public void BothSpellings_AreActuallyReached()
    {
        string[] properties = [.. ThicknessProperties, .. DoubleProperties];
        var patterns = PatternsFor(properties).ToList();
        var counts = new int[patterns.Count];

        foreach (var file in AxamlFiles())
        {
            var text = UiSourceTree.ReadAllText(file);
            for (var i = 0; i < patterns.Count; i++)
                counts[i] += Regex.Matches(text, patterns[i]).Count;
        }

        Assert.True(counts[0] > 100,
            $"the direct `Padding=\"{{DynamicResource …}}\"` pattern matched {counts[0]} sites — " +
            "it has stopped reading the markup");
        Assert.True(counts[1] > 100,
            $"the `<Setter Property=\"Padding\" Value=\"{{DynamicResource …}}\">` pattern matched " +
            $"{counts[1]} sites. Every one of those is a resource handed to a property through a " +
            "style, which fails exactly like the direct form and is invisible to the other pattern.");
    }

    private static IEnumerable<string> AxamlFiles()
        => UiSourceTree.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml",
            SearchOption.AllDirectories);

    private static string Describe(List<string> offenders)
        => $"{offenders.Count} resource-token type mismatch(es) — each one is a clean build that " +
           $"crashes the view on open:\n  {string.Join("\n  ", offenders)}";
}
