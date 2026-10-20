using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace DotCharter.UI.Gallery.Pages.Tokens;

/// <summary>
///     Reads the PRODUCT's token dictionaries back out of
///     <c>DotCharter.UI</c> at runtime, so the tokens page enumerates what the
///     app actually declares instead of a list that goes stale the first time
///     somebody adds a token.
/// </summary>
internal static class ThemeTokenSources
{
    /// <summary>The colour tokens App.axaml merges. Same URI the gallery's App merges.</summary>
    public const string ColorTokensUri = "avares://DotCharter.Tone.UI/Styles/ColorTokens.axaml";

    /// <summary>The type scale (<c>FontSize*</c>).</summary>
    public const string TypographyUri = "avares://DotCharter.Tone.UI/Styles/Typography.axaml";

    /// <summary>The metric ladder (<c>Space*</c>, <c>Gap*</c>, <c>Padding*</c>, <c>Radius*</c>).</summary>
    public const string MetricsUri = "avares://DotCharter.Tone.UI/Styles/Resources/Metrics.axaml";

    /// <summary>
    ///     Every <c>x:Key</c> in the dictionary at <paramref name="uri" />, in
    ///     declaration-independent (ordinal) order.
    ///     <para>
    ///         A token file is either a bare <c>ResourceDictionary</c>
    ///         (ColorTokens) or a <c>Styles</c> carrying nothing but its own
    ///         <c>Styles.Resources</c> (Typography, Metrics) — both shapes are
    ///         handled, because both exist in the product.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> KeysIn(string uri, string prefix = "")
    {
        var loaded = AvaloniaXamlLoader.Load(new Uri(uri));
        var dict = loaded switch
        {
            IResourceDictionary d => d,
            Styles s => s.Resources,
            _ => null
        };

        if (dict == null) return [];

        return dict.Keys
            .OfType<string>()
            .Where(k => prefix.Length == 0 || k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Resolves <paramref name="key" /> the way a <c>DynamicResource</c>
    ///     would — through the attached control — so a value ThemeService has
    ///     overridden at runtime shows the OVERRIDE, not the file's default.
    /// </summary>
    public static object? Resolve(Control host, string key)
    {
        // The host chain first — that is what a DynamicResource on this control
        // would see. A page built in its CONSTRUCTOR is not attached yet, so
        // that chain is empty and the Application dictionary is the only place
        // the token lives; without the fallback every swatch would be blank in
        // exactly the state the capture run renders.
        if (host.TryFindResource(key, out var value) && value != null) return value;
        return Application.Current?.TryFindResource(key, out var appValue) == true ? appValue : null;
    }
}
