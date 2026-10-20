using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace DotCharter.UI.Gallery.Pages.Tokens;

/// <summary>
///     The design-system page: every colour token as a labelled swatch, the
///     type scale as sample lines, the spacing ladder as bars, and a row of
///     each product button class and input control in enabled + disabled state.
///     <para>
///         The colour, type and spacing lists are ENUMERATED from the product's
///         own dictionaries (<see cref="ThemeTokenSources" />) — a token added to
///         <c>Styles/ColorTokens.axaml</c> appears here with no edit to this
///         file. The button and input rows are markup + the class list below,
///         because a style class is not enumerable at runtime.
///     </para>
/// </summary>
public partial class DesignTokensPage : UserControl
{
    /// <summary>
    ///     The classes <c>Styles/Buttons.axaml</c> and the Fire layer define, as
    ///     element type + class, with the ControlTheme that carries each family's
    /// rules (the class alone styles nothing). <b>Keep in step with
    ///     those two files</b>: a class that stops matching a selector renders as
    ///     a bare default button, silently, which is exactly what this page
    ///     exists to show.
    /// </summary>
    private static readonly (string Element, string Classes, string Theme, string Label)[] ButtonClasses =
    [
        ("Button", "", "", "Button (default)"),
        ("Button", "Primary", "PrimaryButton", "Primary"),
        ("Button", "Secondary", "SecondaryButton", "Secondary"),
        ("Button", "Subtle", "SubtleButton", "Subtle"),
        ("Button", "Danger", "DangerButton", "Danger"),
        ("Button", "Pill", "PillButton", "Pill"),
        ("Button", "Compact", "PrimaryButton", "Compact"),
        ("Button", "FirePrimary", "FirePrimaryButton", "FirePrimary"),
        ("Button", "FireCta", "FireCtaButton", "FireCta"),
        ("Button", "FireGhost", "FireGhostButton", "FireGhost"),
        ("Button", "FireQuiet", "FireQuietButton", "FireQuiet"),
        ("ToggleButton", "Chip", "ChipToggle", "Chip"),
        ("ToggleButton", "Chip Mini", "ChipToggle", "Chip Mini"),
        ("ToggleButton", "Seg", "SegToggle", "Seg"),
        ("ToggleButton", "RibbonChip", "RibbonChipToggle", "RibbonChip")
    ];

    public DesignTokensPage()
    {
        InitializeComponent();
        BuildColorSwatches();
        BuildTypeScale();
        BuildSpacingBars();
        BuildButtonRows();
    }

    private void BuildColorSwatches()
    {
        var keys = ThemeTokenSources.KeysIn(ThemeTokenSources.ColorTokensUri);
        ColorHeading.Text = $"Colour tokens ({keys.Count})";

        foreach (var key in keys)
        {
            var color = ThemeTokenSources.Resolve(this, key) as Color?;
            ColorSwatches.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 8, 8),
                Children =
                {
                    new Border
                    {
                        Height = 40,
                        CornerRadius = new CornerRadius(4),
                        Background = color is { } c ? new SolidColorBrush(c) : Brushes.Transparent,
                        BorderThickness = new Thickness(1),
                        BorderBrush = Resolve<IBrush>("BorderDefault")
                    },
                    Label(key, "FontSizeXs", "TextSecondary"),
                    Label(color?.ToString() ?? "(unresolved)", "FontSizeMicro", "TextDim")
                }
            });
        }
    }

    private void BuildTypeScale()
    {
        var keys = ThemeTokenSources.KeysIn(ThemeTokenSources.TypographyUri, "FontSize");
        TypeHeading.Text = $"Type scale ({keys.Count})";

        foreach (var key in keys)
        {
            var size = ThemeTokenSources.Resolve(this, key) as double? ?? 0d;
            TypeScale.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{key} · {size:0.##}",
                        Width = 220,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 11,
                        Foreground = Resolve<IBrush>("TextDim")
                    },
                    new TextBlock
                    {
                        Text = "The quick brown fox jumps over the lazy dog",
                        FontSize = size > 0 ? size : 12,
                        Foreground = Resolve<IBrush>("TextPrimary")
                    }
                }
            });
        }
    }

    private void BuildSpacingBars()
    {
        var keys = ThemeTokenSources.KeysIn(ThemeTokenSources.MetricsUri, "Space");
        SpacingHeading.Text = $"Spacing ({keys.Count})";

        foreach (var key in keys)
        {
            var value = ThemeTokenSources.Resolve(this, key) as double? ?? 0d;
            SpacingBars.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{key} · {value:0.##}",
                        Width = 220,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 11,
                        Foreground = Resolve<IBrush>("TextDim")
                    },
                    new Border
                    {
                        // A zero-width bar would be invisible AND measure zero,
                        // which the smoke test could not tell from a broken page.
                        Width = Math.Max(value, 1),
                        Height = 14,
                        Background = Resolve<IBrush>("AccentColor")
                    }
                }
            });
        }
    }

    private void BuildButtonRows()
    {
        foreach (var (element, classes, theme, label) in ButtonClasses)
        {
            ButtonsEnabled.Children.Add(MakeButton(element, classes, theme, label, enabled: true));
            ButtonsDisabled.Children.Add(MakeButton(element, classes, theme, label, enabled: false));
        }
    }

    private static Control MakeButton(string element, string classes, string theme, string label, bool enabled)
    {
        ContentControl control = element == "ToggleButton"
            ? new ToggleButton { IsChecked = enabled }
            : new Button();

        control.Content = label;
        control.IsEnabled = enabled;
        control.Margin = new Thickness(0, 0, 8, 8);
        AutomationProperties.SetName(control, enabled ? label : label + " (disabled)");
        foreach (var c in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) control.Classes.Add(c);
        if (theme.Length > 0) control[!StyledElement.ThemeProperty] = new DynamicResourceExtension(theme);
        return control;
    }

    private TextBlock Label(string text, string sizeKey, string brushKey) => new()
    {
        Text = text,
        FontSize = ThemeTokenSources.Resolve(this, sizeKey) as double? ?? 11,
        Foreground = Resolve<IBrush>(brushKey),
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private T? Resolve<T>(string key) where T : class =>
        ThemeTokenSources.Resolve(this, key) as T;
}
