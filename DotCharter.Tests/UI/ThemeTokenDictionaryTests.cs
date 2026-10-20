using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>The colour tokens live in ONE file and both Avalonia hosts merge
///     it.</b> Pure XML parsing — no Avalonia session, so this runs in the
///     default gate.
///     <para>
///         The tokens used to be typed inline in <c>DotCharter.UI/App.axaml</c>,
///         which meant a second host (<c>DotCharter.UI.Gallery</c>) could only
///         get them by COPYING them — and a copied palette drifts the first time
///         a token moves, silently, because both files keep building.
///     </para>
/// </summary>
public sealed class ThemeTokenDictionaryTests
{
    private const string ColorTokensUri = "avares://DotCharter.Tone.UI/Styles/ColorTokens.axaml";

    private static string AppMarkup() =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml")));

    private static string GalleryAppMarkup() =>
        UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI.Gallery", "App.axaml"));

    private static string TokensMarkup() =>
        UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "ColorTokens.axaml"));

    /// <summary>
    ///     The dictionary exists, is a <c>ResourceDictionary</c>, and carries a
    ///     real ladder of colours — not an empty shell that would make every
    ///     other assertion here pass while the app rendered unstyled.
    /// </summary>
    [Fact]
    public void TheColorTokenDictionary_DeclaresTheColours()
    {
        var doc = XDocument.Parse(TokensMarkup());
        Assert.Equal("ResourceDictionary", doc.Root!.Name.LocalName);

        var keys = ColourKeys(TokensMarkup());
        Assert.True(keys.Count > 50, $"only {keys.Count} <Color x:Key=…> in ColorTokens.axaml");
        Assert.Contains("AppBackgroundColor", keys);
        Assert.Contains("AccentColorValue", keys);
    }

    /// <summary>
    ///     <c>App.axaml</c> merges the dictionary and declares NO colour of its
    ///     own. An inline <c>&lt;Color x:Key=…&gt;</c> beside the merge is a
    ///     second source of truth that wins, which is precisely the drift this
    ///     split removes.
    /// </summary>
    [Fact]
    public void AppAxaml_MergesTheDictionary_AndDeclaresNoColourInline()
    {
        var app = AppMarkup();
        Assert.Contains("ResourceDictionary.MergedDictionaries", app, StringComparison.Ordinal);
        Assert.Contains($"<ResourceInclude Source=\"{ColorTokensUri}\" />", app, StringComparison.Ordinal);

        var inline = ColourKeys(app);
        Assert.True(inline.Count == 0,
            "App.axaml declares colour token(s) inline instead of in Styles/ColorTokens.axaml: " +
            string.Join(", ", inline));
    }

    /// <summary>
    ///     <b>The gallery's App and the app's App include the same SET of
    ///     URIs.</b> The gallery exists so a screen designed there needs no
    ///     porting; that only holds while it renders the same styles, control
    ///     themes and tokens. Compared as a set, not as text — the two files
    ///     differ in everything else (no DI, no ViewLocator, no dock templates).
    /// </summary>
    [Fact]
    public void TheGalleryApp_IncludesTheSameUris_AsTheRealApp()
    {
        var app = IncludeUris(AppMarkup());
        var gallery = IncludeUris(GalleryAppMarkup());

        Assert.True(app.Count >= 3, $"App.axaml names only {app.Count} include URI(s) — this lint's anchor moved");
        Assert.Equal(app, gallery);
    }

    /// <summary>
    ///     …and the two <c>Application.Styles</c> lists are in the same ORDER,
    ///     down to the two element-only entries. Order is the contract in this
    ///     theme (Fire component styles must win over the generic ones), so a
    ///     gallery that merged the same files in another order would dress
    ///     controls differently from the app while every URI still matched.
    /// </summary>
    [Fact]
    public void TheGalleryApp_OrdersItsStyles_LikeTheRealApp() => Assert.Equal(StyleOrder(AppMarkup()), StyleOrder(GalleryAppMarkup()));

    // ── helpers ─────────────────────────────────────────────────────────────

    private static List<string> ColourKeys(string markup) =>
        Regex.Matches(SourceText.StripMarkupComments(markup), @"<Color\s+x:Key=""(?<k>[^""]+)""")
            .Select(m => m.Groups["k"].Value)
            .ToList();

    /// <summary>Every <c>Source="…"</c> on a <c>StyleInclude</c> or
    /// <c>ResourceInclude</c>, as a set.</summary>
    private static HashSet<string> IncludeUris(string markup) =>
        Regex.Matches(SourceText.StripMarkupComments(markup), @"<(?:Style|Resource)Include\s+Source=""(?<u>[^""]+)""")
            .Select(m => m.Groups["u"].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     The <c>Application.Styles</c> children in document order: an element
    ///     name for the two theme objects, the URI for each include.
    /// </summary>
    private static List<string> StyleOrder(string markup)
    {
        var block = Regex.Match(SourceText.StripMarkupComments(markup),
            "<Application.Styles>(?<b>.*?)</Application.Styles>", RegexOptions.Singleline);
        Assert.True(block.Success, "no <Application.Styles> block");

        return Regex.Matches(block.Groups["b"].Value, @"<(?<e>[\w:.]+)(?<a>[^>]*)/?>")
            .Select(m =>
            {
                var source = Regex.Match(m.Groups["a"].Value, @"Source=""(?<u>[^""]+)""");
                return source.Success ? source.Groups["u"].Value : m.Groups["e"].Value;
            })
            .ToList();
    }
}
