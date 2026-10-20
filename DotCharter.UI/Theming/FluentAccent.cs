using Avalonia.Media;

namespace DotCharter.UI.Theming;

/// <summary>
///     Fluent's own controls (the radio dot, a stock slider, the focus accent)
///     read <c>SystemAccentColor</c> and its three light and three dark steps,
///     which default to the WINDOWS accent. Deriving them from the theme's
///     accent keeps every stock control on the one Graphite blue.
/// </summary>
public static class FluentAccent
{
    public const string Key = "SystemAccentColor";

    /// <summary>The seven Fluent accent keys for <paramref name="accent" />:
    /// the colour itself, then steps mixed 20 / 40 / 60 % toward white and black.</summary>
    public static IReadOnlyDictionary<string, Color> Derive(Color accent) => new Dictionary<string, Color>
    {
        [Key] = accent,
        [Key + "Light1"] = Mix(accent, Colors.White, 0.2),
        [Key + "Light2"] = Mix(accent, Colors.White, 0.4),
        [Key + "Light3"] = Mix(accent, Colors.White, 0.6),
        [Key + "Dark1"] = Mix(accent, Colors.Black, 0.2),
        [Key + "Dark2"] = Mix(accent, Colors.Black, 0.4),
        [Key + "Dark3"] = Mix(accent, Colors.Black, 0.6)
    };

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        a.A,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
