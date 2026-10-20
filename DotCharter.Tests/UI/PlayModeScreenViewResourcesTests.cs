using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     A Play Mode screen view (#294) merges the view-scoped dictionaries whose keys it
///     uses. Its XAML loads on its own, outside <c>PlayModeView</c>'s resource scope, so a
///     <c>{StaticResource GlassPanelBorder}</c> resolves only through a dictionary the
///     view merges ITSELF — and a theme that does not resolve fails SILENTLY: the
///     control keeps the default theme, still lays out with a size, and the headless
///     construction smoke test stays green (seen: <c>PlayingView</c> without the merge
///     passed it). So the guard is here, over the markup.
/// </summary>
public sealed class PlayModeScreenViewResourcesTests
{
    private static readonly Regex StaticKey = new(@"\{StaticResource (?<key>[A-Za-z0-9_.]+)\}", RegexOptions.Compiled);

    /// <summary>Each view-scoped dictionary a screen may need, by its include source.</summary>
    private static readonly (string Source, string RelativePath)[] Dictionaries =
    [
        ("avares://DotCharter/Views/PlayMode/PlayModeThemes.axaml", "DotCharter.UI/Views/PlayMode/PlayModeThemes.axaml"),
        ("avares://DotCharter/Views/Panels/SongFacetTemplates.axaml", "DotCharter.UI/Views/Panels/SongFacetTemplates.axaml")
    ];

    [Fact]
    public void EveryScreenView_MergesTheDictionariesWhoseKeysItUses()
    {
        var screens = PlayModeMarkup.ScreenFiles();
        Assert.True(screens.Count >= 10, $"only {screens.Count} screen views found — the scan is broken");

        var offenders = new List<string>();
        var checkedUses = 0;
        foreach (var (source, relative) in Dictionaries)
        {
            var keys = KeysOf(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(keys.Count > 0, $"no keys read from {relative}");

            foreach (var file in screens)
            {
                var markup = File.ReadAllText(file);
                var used = StaticKey.Matches(markup).Select(m => m.Groups["key"].Value).Where(keys.Contains).Distinct().ToList();
                if (used.Count == 0) continue;
                checkedUses += used.Count;
                if (!markup.Contains($"<ResourceInclude Source=\"{source}\" />", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)} uses {string.Join(", ", used)} but does not merge {source}");
                }
            }
        }

        Assert.True(checkedUses >= 10, $"only {checkedUses} uses of a view-scoped key found — the scan is broken");
        Assert.True(offenders.Count == 0,
            "A screen view's StaticResources resolve only through what IT merges; these would render with the " +
            "default theme and no error:\n  " + string.Join("\n  ", offenders));
    }

    private static HashSet<string> KeysOf(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Root!.Elements()
            .Select(e => (string?)e.Attribute(x + "Key"))
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
