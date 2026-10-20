using System.Text.RegularExpressions;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The eight gear-category colours are FIXED IDENTITIES — section 01 of the
///     owner's Implementation Spec lists them under "GEAR CATEGORY IDENTITY ·
///     USED IN THE RIG, GEAR LIST AND TONE CHIPS" and says, in capitals, do not
///     reassign. A reassigned hue is the quietest possible regression: every
///     screen still renders, and every drive pedal is quietly the wrong colour.
///     <para>
///         A source lint rather than a runtime check, because it has to hold
///         whether or not any test happens to realise a knob — and because the
///         control deliberately carries NO per-category literal to compare
///         against (the dictionary is the single source; a fallback table in
///         code would BE the second source this layer exists to avoid).
///     </para>
/// </summary>
public sealed class FireUnitIdentityTests
{
    /// <summary>The spec's table, verbatim.</summary>
    private static readonly (FireUnitCategory Category, string Hex)[] Identity =
    [
        (FireUnitCategory.Dynamics, "#8FA3BD"),
        (FireUnitCategory.Amps, "#F0A54A"),
        (FireUnitCategory.Cabinets, "#C98F5E"),
        (FireUnitCategory.Drive, "#E05A5A"),
        (FireUnitCategory.Eq, "#4ADE80"),
        (FireUnitCategory.Modulation, "#E879B9"),
        (FireUnitCategory.DelayReverb, "#3EC9C0"),
        (FireUnitCategory.UtilityRouting, "#A78BFA")
    ];

    [Fact]
    public void EveryGearCategoryKeepsTheSpecsColour()
    {
        var declared = BrushesIn(TokenFile());
        Assert.NotEmpty(declared); // the scan must not pass by finding nothing

        foreach (var (category, hex) in Identity)
        {
            var key = FireUnitState.TokenKeyFor(category);
            Assert.True(declared.TryGetValue(key, out var actual),
                $"{category} maps to '{key}', which is not declared in FireControls.axaml — " +
                "the knob would draw its arc in the missing-token grey");
            Assert.Equal(hex, actual, ignoreCase: true);
        }
    }

    [Fact]
    public void EveryCategoryHasItsOwnColour_NoTwoShareOne()
    {
        var keys = Enum.GetValues<FireUnitCategory>()
            .Select(FireUnitState.TokenKeyFor)
            .ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Identity.Length, Identity.Select(i => i.Hex).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheSpecTableCoversEveryCategoryTheEnumHas()
    {
        // A ninth category added without a colour would silently draw grey.
        Assert.Equal(
            Enum.GetValues<FireUnitCategory>().OrderBy(c => c).ToArray(),
            Identity.Select(i => i.Category).OrderBy(c => c).ToArray());
    }

    [Fact]
    public void NoOtherDictionaryRedeclaresAGearIdentity()
    {
        // Two dictionaries declaring `FireUnitDrive` resolve last-wins, which
        // means the colour would depend on merge order.
        var tokenFile = TokenFile();
        var keys = Enum.GetValues<FireUnitCategory>().Select(FireUnitState.TokenKeyFor).ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (var file in AxamlFiles())
        {
            if (string.Equals(file, tokenFile, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (key, _) in BrushesIn(file))
            {
                if (keys.Contains(key))
                    offenders.Add($"{Path.GetFileName(file)} redeclares {key}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void TheControlsCarryNoLiteralHexOfTheirOwn()
    {
        // "No literal hex — every colour resolves from the token dictionary."
        // The four Fire controls are custom-DRAWN, which is exactly where a hex
        // is easiest to inline and hardest to spot.
        var dir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Controls", "Fire");
        Assert.True(Directory.Exists(dir), dir);

        var offenders = new List<string>();
        foreach (var file in UiSourceTree.EnumerateFiles(dir, "*.cs"))
        {
            var text = UiSourceTree.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"(?<!\w)0x[0-9A-Fa-f]{6,8}(?!\w)|""#[0-9A-Fa-f]{3,8}"""))
            {
                // The doc comments quote the spec's table; the CODE must not.
                var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                var line = text[lineStart..text.IndexOf('\n', m.Index)].TrimStart();
                if (line.StartsWith("///", StringComparison.Ordinal) ||
                    line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "a Fire control carries a literal colour instead of a token:\n  " +
            string.Join("\n  ", offenders));
    }

    private static Dictionary<string, string> BrushesIn(string file)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(file),
                     @"<SolidColorBrush\s+x:Key=""(?<key>[^""]+)""\s+Color=""(?<color>[^""]+)"""))
        {
            found[m.Groups["key"].Value] = m.Groups["color"].Value;
        }

        return found;
    }

    private static string TokenFile()
        => Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireControls.axaml");

    private static IEnumerable<string> AxamlFiles()
        => UiSourceTree.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml",
            SearchOption.AllDirectories);
}
