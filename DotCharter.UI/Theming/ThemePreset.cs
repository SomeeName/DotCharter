using Avalonia.Media;

namespace DotCharter.UI.Theming;

public sealed class ThemePreset
{
    public ThemePreset(string id, string displayName, IReadOnlyDictionary<string, Color> colors)
    {
        Id = id;
        DisplayName = displayName;
        Colors = colors;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public IReadOnlyDictionary<string, Color> Colors { get; }

    /// <summary>
    ///     Four brushes the Appearance tab paints a preset's preview chip with:
    ///     page, panel, accent and text. A preset list that is only NAMES makes
    ///     the user apply each one to find out what it looks like — the whole
    ///     point of a preset is that it can be recognized at a glance.
    ///     <para>
    ///         Built once per preset and cached, because the theme card is
    ///         rebuilt whenever the preset list re-templates. Falls back to a
    ///         neutral grey when a (community) pack omits a token — a pack is
    ///         arbitrary user JSON and must never be able to null-ref the tab.
    ///     </para>
    /// </summary>
    public IBrush SwatchBackground => _swatchBackground ??= Brush(ThemeTokens.AppBackground, 0x14, 0x14, 0x18);
    public IBrush SwatchPanel => _swatchPanel ??= Brush(ThemeTokens.PanelBackground, 0x22, 0x22, 0x28);
    public IBrush SwatchAccent => _swatchAccent ??= Brush(ThemeTokens.Accent, 0x8B, 0x5C, 0xF6);
    public IBrush SwatchText => _swatchText ??= Brush(ThemeTokens.TextPrimary, 0xF4, 0xF4, 0xF5);

    private IBrush? _swatchBackground;
    private IBrush? _swatchPanel;
    private IBrush? _swatchAccent;
    private IBrush? _swatchText;

    private IBrush Brush(string token, byte r, byte g, byte b)
    {
        var color = Colors.TryGetValue(token, out var c) ? c : Color.FromRgb(r, g, b);
        // Immutable: one brush instance is bound by every realized item
        // container in the preset list.
        return new SolidColorBrush(color).ToImmutable();
    }
}
