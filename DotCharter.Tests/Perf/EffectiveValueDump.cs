using System.Globalization;
using Avalonia;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Writes the effective look of every realized control under a root: one line per
///     control and property, keyed by the control's path in the tree. Two dumps of the
///     same tree taken before and after a style move diff to exactly the controls whose
///     look changed (#428) — including the prewarmed surfaces no screenshot shows.
/// </summary>
internal static class EffectiveValueDump
{
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "Foreground", "Background", "BorderBrush", "BorderThickness", "CornerRadius", "Padding", "Margin",
        "FontSize", "FontWeight", "FontFamily", "FontStyle", "FontStretch", "TextWrapping", "TextTrimming",
        "TextAlignment", "LineHeight", "LetterSpacing", "MaxLines", "TextDecorations", "Spacing", "ColumnSpacing",
        "RowSpacing", "Orientation", "MinHeight", "MinWidth", "MaxHeight", "MaxWidth", "Height", "Width",
        "HorizontalAlignment", "VerticalAlignment", "HorizontalContentAlignment", "VerticalContentAlignment",
        "Opacity", "IsVisible", "BoxShadow", "ClipToBounds", "Fill", "Stroke", "StrokeThickness", "ZIndex",
        "Text", "Stretch", "IsHitTestVisible", "Cursor", "BackgroundSizing", "OpacityMask", "Effect",
    };

    internal static void Write(Visual root, TextWriter writer)
    {
        var seen = new HashSet<StyledElement>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(StyledElement Element, string Path)>();
        stack.Push((root, root.GetType().Name));
        while (stack.Count > 0)
        {
            var (e, path) = stack.Pop();
            if (!seen.Add(e)) continue;
            WriteElement(e, path, writer);
            var children = new List<(StyledElement, string)>();
            var i = 0;
            if (e is Visual v)
            {
                foreach (var c in v.GetVisualChildren())
                    if (c is StyledElement s) children.Add((s, $"{path}/{s.GetType().Name}[{i++}]"));
            }

            i = 0;
            foreach (var c in ((ILogical)e).LogicalChildren)
                if (c is StyledElement s) children.Add((s, $"{path}/L{s.GetType().Name}[{i++}]"));
            for (var k = children.Count - 1; k >= 0; k--) stack.Push(children[k]);
        }
    }

    private static void WriteElement(StyledElement element, string path, TextWriter writer)
    {
        foreach (var property in AvaloniaPropertyRegistry.Instance.GetRegistered(element)
                     .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(element.GetType()))
                     .Where(p => Properties.Contains(p.Name))
                     .DistinctBy(p => p.Name)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            writer.Write(path);
            writer.Write(' ');
            writer.Write(property.Name);
            writer.Write('=');
            writer.WriteLine(Format(element.GetValue(property)));
        }

        if (element is Visual visual)
        {
            var b = visual.Bounds;
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{path} Bounds={b.X:0.##},{b.Y:0.##},{b.Width:0.##},{b.Height:0.##}"));
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        ISolidColorBrush s => string.Create(CultureInfo.InvariantCulture, $"#{s.Color} o{s.Opacity:0.###}"),
        IGradientBrush g => g.GetType().Name + "[" + string.Join(";", g.GradientStops.Select(x =>
            string.Create(CultureInfo.InvariantCulture, $"{x.Color}@{x.Offset:0.###}"))) + "]",
        IBrush b => b.GetType().Name,
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
