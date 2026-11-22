using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.UI.Controls;

/// <summary>The "nothing matches" row of the gear picker: shown only while no real choice matches the search.</summary>
internal sealed record GearNoMatchChoice() : IToneChoice
{
    public string DisplayName => LocalizationService.Instance["GearPicker_NoMatch"];
}

/// <summary>
///     The look of <see cref="DeferredChoiceBox" />'s list in the Fire design system: the popup card aligned
///     to the field and as wide as it, and the rows (group heading, current-value mark, matched text).
/// </summary>
internal static class GearPickerParts
{
    private const double MaxListHeight = 340;

    /// <summary>Gives the box's popup the Fire card chrome and pins it under <paramref name="field" />, its width.</summary>
    public static void StylePopup(AutoCompleteBox box, Control field)
    {
        box.Classes.Add("GearPicker");
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        box.MaxDropDownHeight = MaxListHeight;
        if (box.GetTemplateDescendants().OfType<Popup>().FirstOrDefault() is not { } popup) return;

        popup.PlacementTarget = field;
        popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        popup.HorizontalOffset = 0;
        popup.VerticalOffset = 4;
        if (popup.Child is not Border card) return;

        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("FireSurfaceMenu"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("FireBorderStrong"));
        card.Bind(Border.BoxShadowProperty, card.GetResourceObservable("FireShadowScreen"));
        card.BorderThickness = new Thickness(1);
        card.CornerRadius = new CornerRadius(8);
        card.Padding = new Thickness(4);
        card.MinWidth = 0;
        Match(card, field);
        field.SizeChanged += (_, _) => Match(card, field);
    }

    /// <summary>The card is exactly as wide as the field it drops from.</summary>
    public static void Match(Border card, Control field)
    {
        if (field.Bounds.Width > 0) card.Width = field.Bounds.Width;
    }

    /// <summary>One row: an optional group heading, the current-value mark, the name with the typed text highlighted.</summary>
    public static Control Row(IToneChoice choice, string? search, bool isCurrent, string? heading)
    {
        if (choice is GearNoMatchChoice)
        {
            var empty = new TextBlock { Text = choice.DisplayName, FontStyle = FontStyle.Italic, Margin = new Thickness(6, 4), IsHitTestVisible = false };
            empty.Bind(TextBlock.ForegroundProperty, empty.GetResourceObservable("FireTextMuted"));
            return empty;
        }

        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        AddHighlighted(name, choice.DisplayName, search);
        if (isCurrent) name.FontWeight = FontWeight.SemiBold;

        var mark = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, IsVisible = isCurrent, Name = "CurrentMark" };
        mark.Bind(Shape.FillProperty, mark.GetResourceObservable("FireAccent"));
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*") };
        line.Children.Add(mark);
        Grid.SetColumn(name, 1);
        line.Children.Add(name);

        if (string.IsNullOrEmpty(heading)) return line;

        var header = new TextBlock { Text = heading.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 6, 0, 4), Name = "GroupHeading" };
        header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("FireTextMuted"));
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(line);
        return stack;
    }

    /// <summary>The characters of <paramref name="text" /> that match <paramref name="search" /> in the accent colour.</summary>
    private static void AddHighlighted(TextBlock target, string text, string? search)
    {
        var needle = search?.Trim();
        var at = string.IsNullOrEmpty(needle) ? -1 : text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            target.Text = text;
            return;
        }

        target.Inlines ??= [];
        if (at > 0) target.Inlines.Add(new Run(text[..at]));
        var hit = new Run(text.Substring(at, needle!.Length)) { FontWeight = FontWeight.Bold };
        hit.Bind(TextElement.ForegroundProperty, target.GetResourceObservable("FireAccent"));
        target.Inlines.Add(hit);
        if (at + needle.Length < text.Length) target.Inlines.Add(new Run(text[(at + needle.Length)..]));
    }
}
