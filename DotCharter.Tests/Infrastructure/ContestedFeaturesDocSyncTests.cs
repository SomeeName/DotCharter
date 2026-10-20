using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Services.Infrastructure.Features;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Keeps <c>docs/agents/services/contested-features.md</c>, the registry
///     (<see cref="ContestedFeatures.All" />) and the places the switch is wired
///     (the menu files, the palette, the component catalogue, the build files) in
///     step (#824). A feature added in code but not listed, listed but not wired, or
///     wired by a menu row nobody registered fails here.
/// </summary>
public sealed partial class ContestedFeaturesDocSyncTests
{
    private const string DocRelativePath = "docs/agents/services/contested-features.md";

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex Backticked();

    [GeneratedRegex(@"Loc\s+(Menu_\w+)")]
    private static partial Regex LocKey();

    [GeneratedRegex(@"FromCommand\(\s*""([^""]+)""\s*,\s*""(Menu_\w+)""")]
    private static partial Regex PaletteEntry();

    private static string Root => TestPaths.RepoRoot();

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    /// <summary>The doc's "What the switch covers" table as feature id → the five cells.</summary>
    private static Dictionary<string, string[]> DocRows()
    {
        var text = Read("docs", "agents", "services", "contested-features.md");
        var start = text.IndexOf("## What the switch covers", StringComparison.Ordinal);
        Assert.True(start >= 0, "the doc lost its 'What the switch covers' section");
        var end = text.IndexOf("\n## ", start + 5, StringComparison.Ordinal);
        var section = end < 0 ? text[start..] : text[start..end];

        var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in section.Split('\n').Where(l => l.TrimStart().StartsWith('|')))
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length != 5 || cells[0].StartsWith("Feature id", StringComparison.Ordinal) || cells[0].StartsWith("---", StringComparison.Ordinal))
                continue;
            var id = Backticked().Match(cells[0]);
            Assert.True(id.Success, $"the first cell of a row must be a backticked feature id: {line}");
            rows.Add(id.Groups[1].Value, cells);
        }

        return rows;
    }

    private static HashSet<string> Tokens(string cell) =>
        Backticked().Matches(cell).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void The_doc_lists_exactly_the_features_in_code()
    {
        var rows = DocRows();
        Assert.Equal(
            ContestedFeatures.All.Select(f => f.Id).Order(StringComparer.Ordinal),
            rows.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Each_doc_row_lists_exactly_the_menu_keys_palette_ids_components_and_paths_in_code()
    {
        var rows = DocRows();
        foreach (var feature in ContestedFeatures.All)
        {
            var cells = rows[feature.Id];
            Assert.Equal(feature.MenuKeys.Order(StringComparer.Ordinal), Tokens(cells[1]).Order(StringComparer.Ordinal));
            Assert.Equal(feature.PaletteCommandIds.Order(StringComparer.Ordinal), Tokens(cells[2]).Order(StringComparer.Ordinal));
            Assert.Equal(feature.ComponentIds.Order(StringComparer.Ordinal), Tokens(cells[3]).Order(StringComparer.Ordinal));
            Assert.Equal(feature.SourcePaths.Order(StringComparer.Ordinal), Tokens(cells[4]).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Every_registered_source_path_exists()
    {
        foreach (var path in ContestedFeatures.All.SelectMany(f => f.SourcePaths))
        {
            var full = Path.Combine(Root, path);
            Assert.True(File.Exists(full) || Directory.Exists(full), $"{path} is named by ContestedFeatures.All but is not in the repo");
        }
    }

    private static bool IsContestedKey(string key, HashSet<string> registered) =>
        registered.Contains(key) || key.Contains("Songsterr", StringComparison.OrdinalIgnoreCase)
        || key.Contains("YouTube", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Every_menu_row_that_opens_a_contested_feature_is_marked_with_its_feature_and_removed_when_off()
    {
        var marked = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmarkedContested = new List<string>();
        var registered = ContestedFeatures.All.SelectMany(f => f.MenuKeys).ToHashSet(StringComparer.Ordinal);
        const string attribute = "ContestedMenu.Feature";

        foreach (var file in MainMenuSource.MenuFiles())
        {
            var fileMarks = 0;
            foreach (var item in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "MenuItem"))
            {
                var key = LocKey().Match((string?)item.Attribute("Header") ?? "");
                if (!key.Success) continue;
                var feature = item.Attributes().FirstOrDefault(a => a.Name.LocalName == attribute)?.Value;
                if (feature is not null)
                {
                    marked[key.Groups[1].Value] = feature;
                    fileMarks++;
                }
                else if (IsContestedKey(key.Groups[1].Value, registered))
                {
                    unmarkedContested.Add($"{Path.GetFileName(file)}: {key.Groups[1].Value}");
                }
            }

            if (fileMarks > 0)
            {
                var code = File.ReadAllText(file + ".cs");
                Assert.True(code.Contains("ContestedMenu.Prune(this)", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} marks a contested row but its constructor never calls ContestedMenu.Prune(this)");
            }
        }

        Assert.Empty(unmarkedContested);
        var expected = ContestedFeatures.All
            .SelectMany(f => f.MenuKeys.Select(k => (Key: k, f.Id)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Id}");
        Assert.Equal(expected, marked.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
    }

    [Fact]
    public void Every_palette_entry_for_a_contested_feature_is_registered_and_the_registered_ones_exist()
    {
        var catalog = Read("DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs");
        var registered = ContestedFeatures.All.SelectMany(f => f.PaletteCommandIds).ToHashSet(StringComparer.Ordinal);
        var contestedMenuKeys = ContestedFeatures.All.SelectMany(f => f.MenuKeys).ToHashSet(StringComparer.Ordinal);

        var entries = PaletteEntry().Matches(catalog).Select(m => (Id: m.Groups[1].Value, Key: m.Groups[2].Value)).ToList();
        Assert.NotEmpty(entries);

        var unregistered = entries
            .Where(e => contestedMenuKeys.Contains(e.Key) || e.Key.Contains("Songsterr", StringComparison.OrdinalIgnoreCase)
                        || e.Key.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
            .Where(e => !registered.Contains(e.Id))
            .Select(e => e.Id);
        Assert.Empty(unregistered);

        foreach (var id in registered)
            Assert.Contains(entries, e => e.Id == id);
        Assert.Contains("ContestedFeatures.FilterPalette(", catalog);
    }

    [Fact]
    public void Every_registered_component_is_defined_in_the_catalog_behind_the_switch()
    {
        var catalog = Read("DotCharter.Services", "Audio", "StemSeparation", "Components", "ComponentCatalog.cs");
        foreach (var id in ContestedFeatures.All.SelectMany(f => f.ComponentIds))
            Assert.Contains($"Id = \"{id}\"", catalog);
        Assert.Contains("if (!contestedFeatures) return tools;", catalog);
    }

    [Fact]
    public void The_property_the_constant_and_the_release_parameter_exist()
    {
        var props = Read("Directory.Build.props");
        Assert.Contains("DotCharterContestedFeatures", props);
        Assert.Contains("CONTESTED_FEATURES", props);

        var release = Read("tools", "release", "New-Release.ps1");
        Assert.Contains("[bool]$ContestedFeatures = $true", release);
        Assert.Contains("-p:DotCharterContestedFeatures=false", release);

        var doc = Read("docs", "agents", "services", "contested-features.md");
        Assert.Contains("DotCharterContestedFeatures", doc);
        Assert.Contains("-ContestedFeatures:$false", doc);
        Assert.True(File.Exists(Path.Combine(Root, DocRelativePath)));
    }

    [Fact]
    // The gate builds with the property at its default. A build that passes
    // -p:DotCharterContestedFeatures=false is the public build and is not run through the gate.
    public void The_default_build_has_the_switch_on() => Assert.True(ContestedFeatures.Enabled);

    [Fact]
    public void Only_the_registry_and_the_module_decide_what_the_switch_does()
    {
        // The compile constant is read in ONE file; everything else asks ContestedFeatures.Enabled.
        var offenders = new List<string>();
        foreach (var project in new[] { "DotCharter.Core", "DotCharter.Services", "DotCharter.Presentation", "DotCharter.UI", "DotCharter.Tone", "DotCharter.Tone.UI" })
        {
            var dir = Path.Combine(Root, project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                if (Path.GetFileName(file) == "ContestedFeatures.cs") continue;
                if (File.ReadAllText(file).Contains("CONTESTED_FEATURES", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(Root, file));
            }
        }

        Assert.Empty(offenders);
    }
}
