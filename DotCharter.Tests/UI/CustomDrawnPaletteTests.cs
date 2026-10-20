using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Custom-DRAWN controls cannot use <c>DynamicResource</c>; they look a
///     token up by NAME and fall back to a literal when it is missing. That
///     fallback is deliberate — a run HUD must not go invisible mid-song
///     because a key was renamed — but it is also the perfect hiding place: a
///     mistyped key produces no warning, no exception and no visible change,
///     because the control simply keeps painting its pre-Fire colour forever.
///
///     <para>
///         This is the guard for that. It reads the token names straight out of
///         the control SOURCES and asserts every one of them exists in the
///         shipped Fire dictionaries. It found a real one on the day it was
///         written: <c>FireAccentDim</c>, which never existed (the layer spells
///         it <c>FireAccentDeep</c>), and would have kept the dimmed-phrase bars
///         on <c>#6B21A8</c> indefinitely.
///     </para>
///     <para>
///         Source-level and platform-free, so it runs in the default gate.
///     </para>
/// </summary>
public sealed class CustomDrawnPaletteTests
{
    /// <summary>The controls that resolve theme brushes through a named
    /// lookup with a literal fallback, and the helper each one calls.</summary>
    private static readonly (string File, string Helper)[] Sources =
    [
        (Path.Combine("Controls", "LivePlaySectionGraph.cs"), "Solid"),
        (Path.Combine("Controls", "PracticeProgressGraph.cs"), "Resolve")
    ];

    [Fact]
    public void EveryTokenACustomDrawnControlAsksFor_ReallyExists()
    {
        var declared = DeclaredFireKeys();
        Assert.True(declared.Count > 100,
            $"only {declared.Count} Fire keys parsed — the dictionaries moved and this guard is now blind");

        var asked = 0;
        foreach (var (file, helper) in Sources)
        {
            var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", file));
            Assert.True(File.Exists(path), $"{file} moved — this guard no longer covers it");

            // `Solid("FireWarning", …)` / `Resolve(this, "FireSuccess", …)`.
            var pattern = helper == "Resolve"
                ? @"Resolve\(\s*\w+\s*,\s*""(Fire\w+)"""
                : @"\bSolid\(\s*""(Fire\w+)""";

            foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(path), pattern))
            {
                asked++;
                var key = m.Groups[1].Value;
                Assert.True(declared.Contains(key),
                    $"{file} resolves '{key}', which is not declared in FireTokens.axaml or " +
                    "FireControls.axaml — the lookup silently falls back to its baked literal, " +
                    "so the control keeps its pre-Fire colour with no other symptom");
            }
        }

        // The anti-vacuity floor: without it a regex that stopped matching
        // would pass this test while checking nothing at all.
        Assert.True(asked >= 8,
            $"only {asked} token lookups found across {Sources.Length} controls — the call shape changed " +
            "and the regexes no longer see it");
    }

    /// <summary>The accuracy heat ramp is a DATA encoding (red = missed,
    /// green = clean), not app chrome. It must stay a fixed literal ramp: if it
    /// followed the palette, two runs under two themes would report different
    /// accuracy for the same playing.</summary>
    [Fact]
    public void TheAccuracyHeatRamp_IsNotResolvedFromTheTheme()
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "LivePlaySectionGraph.cs"));
        var source = UiSourceTree.ReadAllText(path);

        var start = source.IndexOf("private static ImmutableSolidColorBrush HeatBrush", StringComparison.Ordinal);
        Assert.True(start > 0, "HeatBrush was renamed — this guard no longer reads the ramp");
        var end = source.IndexOf("public override void Render", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not bound the HeatBrush body");

        var body = source[start..end];
        Assert.DoesNotContain("TryFindResource", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Solid(\"", body, StringComparison.Ordinal);
        // …and it is still a ramp, not a single colour.
        Assert.Contains("0x7F", body, StringComparison.Ordinal);
        Assert.Contains("0x16", body, StringComparison.Ordinal);
    }

    private static HashSet<string> DeclaredFireKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "FireTokens.axaml", "FireControls.axaml" })
        {
            var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Styles", name));
            Assert.True(File.Exists(path), $"{name} moved — this guard is blind");
            // FireTokens.axaml is a CONTAINER over `Styles/FireTokens/`; read it
            // alone and this set is empty, which turns the guard into a list of
            // every Fire key a custom-drawn control names.
            foreach (Match m in Regex.Matches(StyleFileText.ReadWithIncludes(path), @"x:Key=""(Fire\w+)"""))
                keys.Add(m.Groups[1].Value);
        }

        return keys;
    }
}
