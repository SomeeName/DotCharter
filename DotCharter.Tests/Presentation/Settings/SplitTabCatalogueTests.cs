using System.Text.RegularExpressions;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Each tab split out of General declares its search rows once, in a
///     <see cref="SettingsTabRows" />, and its view hides rows and groups by the
///     same keys. The two must agree BOTH ways: a key the view hides by that the
///     catalogue never emits can never be hidden, and a catalogue row the view
///     does not bind is a search hit that filters nothing.
/// </summary>
public sealed class SplitTabCatalogueTests
{
    public static TheoryData<string> Tabs => ["General", "Editor", "Play", "Files"];

    [Theory]
    [MemberData(nameof(Tabs))]
    public void TheCatalogueAndTheView_NameTheSameKeys(string tab)
    {
        var (prefix, groups) = Catalogue(tab);
        var axaml = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", tab + "SettingsView.axaml")));
        var inView = Regex.Matches(axaml, @"ConverterParameter=""?(" + Regex.Escape(prefix) + @"[A-Za-z0-9.]+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var declared = groups.SelectMany(g => g.Rows).Select(r => r.Key)
            .Concat(Enumerable.Range(0, groups.Count).Select(i => prefix + "group." + i))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(declared.Count >= 5, $"{tab} declares only {declared.Count} keys; the walk went vacuous");
        var unbound = declared.Except(inView).Order(StringComparer.Ordinal).ToList();
        var undeclared = inView.Except(declared).Order(StringComparer.Ordinal).ToList();
        Assert.True(unbound.Count == 0, $"{tab}SettingsView never hides by: {string.Join(", ", unbound)}");
        Assert.True(undeclared.Count == 0, $"{tab}SettingsView hides by keys the catalogue never emits: {string.Join(", ", undeclared)}");
    }

    private static (string Prefix, IReadOnlyList<SettingsSearchGroup> Groups) Catalogue(string tab)
    {
        var type = typeof(SettingsTabRows).Assembly.GetType($"DotCharter.Presentation.Screens.Settings.{tab}SettingsViewModel")!;
        var rows = (SettingsTabRows)type
            .GetField("Rows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        return (rows.Prefix, rows.Localize(FakeLocalization.English()));
    }
}
