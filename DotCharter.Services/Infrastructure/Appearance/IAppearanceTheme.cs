namespace DotCharter.Services.Infrastructure.Appearance;

/// <summary>One customizable theme colour: its resource key, and the localization keys of its name and its heading.</summary>
public sealed record ThemeTokenEntry(string Key, string NameKey, string GroupKey);

/// <summary>One colour-blind string palette: the id <c>AppConfig.StringColorPresetId</c> persists, and its display name.</summary>
public sealed record StringPaletteEntry(string Id, string DisplayName);

/// <summary>
///     The app's ONE theme as a settings screen drives it, with every colour
///     carried as a hex string (<c>#RRGGBB</c>, or <c>#AARRGGBB</c> when not
///     opaque) so nothing here needs Avalonia.
///     <para>
///         The implementation stays in <c>DotCharter.UI</c>, because applying a
///         colour writes <c>Application.Current.Resources</c>. There is one theme,
///         so no preset type crosses this seam: the only things that vary are the
///         string palette laid over it and the user's per-token overrides.
///     </para>
/// </summary>
public interface IAppearanceTheme
{
    /// <summary>The colours the user may change, in catalogue order (grouped by <see cref="ThemeTokenEntry.GroupKey" />).</summary>
    IReadOnlyList<ThemeTokenEntry> Tokens { get; }

    IReadOnlyList<StringPaletteEntry> StringPalettes { get; }

    /// <summary>The palette that carries no colours of its own: the theme's string colours show.</summary>
    string DefaultStringPaletteId { get; }

    /// <summary>The live colour of <paramref name="tokenKey" />, as a readout.</summary>
    string GetHex(string tokenKey);

    /// <summary>Previews one colour live. An unparsable value changes nothing.</summary>
    void SetHex(string tokenKey, string hex);

    /// <summary>Lays a string palette over the theme and KEEPS every other colour the user changed.</summary>
    void ApplyStringPalette(string paletteId);

    /// <summary>Puts every colour back to the theme's, with only <paramref name="paletteId" />'s string colours over it.</summary>
    void ResetColors(string paletteId);

    /// <summary>Applies a persisted theme: the free-form overrides with the palette's string colours winning.</summary>
    void ApplyPersisted(IReadOnlyDictionary<string, string> freeFormOverrides, string paletteId);

    /// <summary>
    ///     Token → persisted hex for every live colour that differs from the
    ///     theme. With <paramref name="excludeStringTokens" /> the seven string
    ///     colours are left out, because an active palette governs them.
    /// </summary>
    IReadOnlyDictionary<string, string> FreeFormOverrides(bool excludeStringTokens);
}
