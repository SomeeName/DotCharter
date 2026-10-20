using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace DotCharter.Tests.Perf;

/// <summary>
///     TextBlock and ToggleButton families are ControlThemes since #240, like the
///     button and Border families (<see cref="ButtonThemes" />,
///     <see cref="BorderThemes" />): the class only gates the theme's rules, so a test
///     that builds a <c>TextBlock { Classes = { "FireMono" } }</c> in code has to hand
///     it its theme too, as the markup does.
/// </summary>
internal static class TextAndToggleThemes
{
    /// <summary>The ToggleButton themes that are not spelled <c>&lt;Class&gt;Toggle</c>.</summary>
    private static readonly Dictionary<string, string> ToggleKeys = new(StringComparer.Ordinal)
    {
        ["FireRailItem"] = "FireRailToggle"
    };

    /// <summary>Wears the theme of the TextBlock's first family class
    /// (<c>FireMono</c> → <c>FireMonoText</c>, <c>HelpText</c> → <c>HelpText</c>).
    /// Throws when there is none, so a test cannot pass on an unstyled TextBlock.</summary>
    public static TextBlock Themed(this TextBlock text)
    {
        text.Theme = Find(text.Classes, cls => cls.EndsWith("Text", StringComparison.Ordinal) ? cls : cls + "Text", "TextBlock");
        return text;
    }

    /// <summary>Wears the theme of the toggle's first family class
    /// (<c>Chip</c> → <c>ChipToggle</c>, <c>FireSegmentItem</c> → <c>FireSegmentItemToggle</c>).</summary>
    public static ToggleButton Themed(this ToggleButton toggle)
    {
        toggle.Theme = Find(toggle.Classes, cls => ToggleKeys.TryGetValue(cls, out var key) ? key : cls + "Toggle", "ToggleButton");
        return toggle;
    }

    private static ControlTheme Find(Classes classes, Func<string, string> keyOf, string type)
    {
        foreach (var cls in classes)
        {
            if (Application.Current!.TryFindResource(keyOf(cls), out var found) && found is ControlTheme theme)
                return theme;
        }

        throw new InvalidOperationException($"no {type} ControlTheme for classes '{string.Join(' ', classes)}' (#240)");
    }
}
