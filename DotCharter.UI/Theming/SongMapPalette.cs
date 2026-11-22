using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DotCharter.UI.Theming;

/// <summary>Fixed colours for song map and Live Play section bands and song map phrase markers.</summary>
internal static class SongMapPalette
{
    public static ISolidColorBrush LabelBrush { get; } = Brushes.Black;

    private static readonly Dictionary<string, ISolidColorBrush> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["noguitar"] = Brush("#78818F"),
        ["intro"] = Brush("#B38B56"),
        ["preverse"] = Brush("#63A072"),
        ["verse"] = Brush("#38974F"),
        ["postvs"] = Brush("#468D79"),
        ["modverse"] = Brush("#36A070"),
        ["prechorus"] = Brush("#689AB2"),
        ["chorus"] = Brush("#2D92C4"),
        ["postchorus"] = Brush("#0B82DC"),
        ["modchorus"] = Brush("#5A97CC"),
        ["hook"] = Brush("#E05CD2"),
        ["prebrdg"] = Brush("#A886C4"),
        ["bridge"] = Brush("#A268D8"),
        ["postbrdg"] = Brush("#AB65F1"),
        ["solo"] = Brush("#FF4C64"),
        ["outro"] = Brush("#AA7B34"),
        ["ambient"] = Brush("#679D97"),
        ["breakdown"] = Brush("#CE6B48"),
        ["interlude"] = Brush("#8B8BC6"),
        ["transition"] = Brush("#778E39"),
        ["riff"] = Brush("#9D931E"),
        ["fadein"] = Brush("#8E9566"),
        ["fadeout"] = Brush("#868549"),
        ["buildup"] = Brush("#CD7948"),
        ["variation"] = Brush("#D6629E"),
        ["head"] = Brush("#AA6FA4"),
        ["modbridge"] = Brush("#9073EA"),
        ["melody"] = Brush("#B98498"),
        ["vamp"] = Brush("#598F3A"),
        ["silence"] = Brush("#878785"),
        ["tapping"] = Brush("#E86B64"),
        ["count"] = Brush("#869498"),
        ["end"] = Brush("#8E7E7A"),
    };

    // Custom names share these swatches. Every known colour is reserved.
    private static readonly ISolidColorBrush[] Fallback =
    [
        Brush("#599F54"), Brush("#4C8D7B"), Brush("#3B9894"), Brush("#7287DB"),
        Brush("#7571EF"), Brush("#8882B0"), Brush("#C36ECC"), Brush("#AF6E9D"),
        Brush("#D370A9"), Brush("#F64D7C"), Brush("#AE796F"), Brush("#C37C5F"),
        Brush("#8D8749"), Brush("#8D8E69"), Brush("#7C9C49"), Brush("#538E3F")
    ];

    public static ISolidColorBrush FillFor(string name)
    {
        var trimmed = name.Trim();
        if (Known.TryGetValue(trimmed, out var brush)) return brush;

        var hash = 0;
        foreach (var character in trimmed) hash = unchecked(hash * 31 + char.ToUpperInvariant(character));
        return Fallback[(hash & int.MaxValue) % Fallback.Length];
    }

    private static ISolidColorBrush Brush(string value) => new ImmutableSolidColorBrush(Color.Parse(value));
}
