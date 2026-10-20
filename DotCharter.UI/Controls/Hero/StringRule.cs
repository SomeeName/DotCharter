using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DotCharter.UI.Controls.Hero;

/// <summary>
///     The mockup's <c>.string-rule</c> under the hero's title: six 3 px bars, one per string in the
///     mockup's string colours, 3 px apart across 180 px, at 85 % opacity.
/// </summary>
public sealed class StringRule : Control
{
    private static readonly IBrush[] Bars =
    [
        .. new[] { "#ff3b5c", "#ffd23f", "#3bc9ff", "#ff8a3b", "#4dff88", "#b36bff" }
            .Select(hex => (IBrush)new ImmutableSolidColorBrush(Color.Parse(hex), 0.85))
    ];

    public StringRule()
    {
        Width = 180;
        Height = 3;
    }

    public override void Render(DrawingContext context)
    {
        const double gap = 3;
        var bar = (Bounds.Width - gap * (Bars.Length - 1)) / Bars.Length;
        for (var i = 0; i < Bars.Length; i++)
            context.DrawRectangle(Bars[i], null, new RoundedRect(new Rect(i * (bar + gap), 0, bar, Bounds.Height), 2));
    }
}
