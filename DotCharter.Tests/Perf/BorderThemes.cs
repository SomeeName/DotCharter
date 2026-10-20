using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace DotCharter.Tests.Perf;

/// <summary>
///     A Border family is a ControlTheme since #240, exactly like a button family
///     (<see cref="ButtonThemes" />): the class only gates the theme's rules, so a test
///     that builds a <c>Border { Classes = { "FireTag" } }</c> in code has to hand it
///     its theme too, as the markup does.
/// </summary>
internal static class BorderThemes
{
    /// <summary>Wears the theme of the border's first family class
    /// (<c>FireTag</c> → <c>FireTagBorder</c>). Throws when there is none, so a
    /// test cannot pass on an unstyled border.</summary>
    public static Border Themed(this Border border)
    {
        foreach (var cls in border.Classes)
        {
            if (Application.Current!.TryFindResource(cls + "Border", out var found) && found is ControlTheme theme)
            {
                border.Theme = theme;
                return border;
            }
        }

        throw new InvalidOperationException(
            $"no Border ControlTheme for classes '{string.Join(' ', border.Classes)}' (#240)");
    }
}
