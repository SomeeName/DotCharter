using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;

namespace DotCharter.UI.Views.Editor;

/// <summary>
/// A live copy of a tab's badge for its row in the strip's "More" menu (<see cref="InspectorTabMoreMenu" />):
///     every property that draws the badge is BOUND to the original's effective value, its style-set brushes
///     included, so the copy shows, counts and colours exactly what the tab does and hides when it hides. Copies the
///     shapes a badge is made of (a panel, a border, a text, a dot); anything else has no copy.
/// </summary>
internal static class TabBadgeMirror
{
    private static readonly AvaloniaProperty[] Common =
    [
        Visual.IsVisibleProperty, Layoutable.WidthProperty, Layoutable.HeightProperty, Layoutable.MinWidthProperty,
        Layoutable.MarginProperty, Layoutable.HorizontalAlignmentProperty, Layoutable.VerticalAlignmentProperty
    ];

    private static readonly AvaloniaProperty[] BorderLook =
    [
        Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty,
        Border.CornerRadiusProperty, Border.PaddingProperty
    ];

    private static readonly AvaloniaProperty[] TextLook =
    [
        TextBlock.TextProperty, TextBlock.FontFamilyProperty, TextBlock.FontSizeProperty, TextBlock.FontWeightProperty,
        TextBlock.ForegroundProperty
    ];

    private static readonly AvaloniaProperty[] DotLook = [Shape.FillProperty];

    public static Control? Of(Control source)
    {
        var copy = source switch
        {
            TextBlock => Bound(new TextBlock(), source, TextLook),
            Ellipse => Bound(new Ellipse(), source, DotLook),
            Border { Child: var child } => Bound(new Border { Child = child is null ? null : Of(child) }, source, BorderLook),
            StackPanel stack => Children(new StackPanel { Orientation = stack.Orientation, Spacing = stack.Spacing }, stack),
            Panel panel when panel.GetType() == typeof(Panel) => Children(new Panel(), panel),
            _ => null
        };
        return copy is null ? null : Bound(copy, source, Common);
    }

    private static Control Children(Panel copy, Panel source)
    {
        foreach (var child in source.Children)
        {
            if (Of(child) is { } mirrored) copy.Children.Add(mirrored);
        }

        return copy;
    }

    private static Control Bound(Control copy, Control source, AvaloniaProperty[] properties)
    {
        foreach (var property in properties) copy[!property] = source[!property];
        copy.IsHitTestVisible = false;
        return copy;
    }
}
