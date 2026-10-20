using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace DotCharter.Tests.Perf;

/// <summary>
///     A button family is a ControlTheme since #240: the class only gates the
///     theme's rules, so a test that builds a <c>Button { Classes = { "FireGhost" } }</c>
///     in code has to hand it its theme too, exactly as the markup does.
/// </summary>
internal static class ButtonThemes
{
    /// <summary>Wears the theme of the button's first family class
    /// (<c>FireGhost</c> → <c>FireGhostButton</c>). Throws when there is none,
    /// so a test cannot pass on an unstyled button.</summary>
    public static Button Themed(this Button button)
    {
        foreach (var cls in button.Classes)
        {
            var key = cls.EndsWith("Button", StringComparison.Ordinal) ? cls : cls + "Button";
            if (Application.Current!.TryFindResource(key, out var found) && found is ControlTheme theme)
            {
                button.Theme = theme;
                return button;
            }
        }

        throw new InvalidOperationException(
            $"no Button ControlTheme for classes '{string.Join(' ', button.Classes)}' (#240)");
    }
}
