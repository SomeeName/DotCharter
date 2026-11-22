using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace DotCharter.UI.Controls;

/// <summary>
/// The tiny page buttons over a <see cref="ColumnFlowPanel" /> column that shows one card of several (inspector
///     design D): one toggle per paged card, captioned and named by the card's
///     <see cref="ColumnFlowPanel.TitleProperty" />, the card on screen lit. The panel owns it and tells it what to
///     show; a click is reported through <see cref="PagePicked" />. Its buttons are pooled: a section switch rebinds
///     them, it does not build new ones.
/// </summary>
public sealed class CardPager : StackPanel
{
    /// <summary>The ToggleButton theme every page button wears (declared by the inspector grammar).</summary>
    public const string ThemeKey = "ColumnFlowNavToggle";

    private readonly List<ToggleButton> _buttons = [];
    private readonly List<Control?> _pages = [];

    public CardPager()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 3;
    }

    /// <summary>A page button was clicked: the card it stands for.</summary>
    public event Action<Control>? PagePicked;

    /// <summary>The cards the buttons stand for, in order — the test hook.</summary>
    internal IReadOnlyList<ToggleButton> Buttons => [.. _buttons.Where(b => b.IsVisible)];

    /// <summary>One button per card in <paramref name="pages" />, <paramref name="current" />'s lit.</summary>
    public void Show(IReadOnlyList<Control> pages, Control? current)
    {
        while (_buttons.Count < pages.Count) _buttons.Add(NewButton());
        for (var i = 0; i < _buttons.Count; i++)
        {
            var button = _buttons[i];
            var page = i < pages.Count ? pages[i] : null;
            if (!ReferenceEquals(_pages[i], page))
            {
                _pages[i] = page;
                if (page is null)
                {
                    button.ClearValue(ContentControl.ContentProperty);
                    button.ClearValue(AutomationProperties.NameProperty);
                }
                else
                {
                    button.Content = new TextBlock { [!TextBlock.TextProperty] = page[!ColumnFlowPanel.TitleProperty] };
                    button[!AutomationProperties.NameProperty] = page[!ColumnFlowPanel.TitleProperty];
                }
            }

            button.IsVisible = page is not null;
            button.IsChecked = page is not null && ReferenceEquals(page, current);
        }
    }

    private ToggleButton NewButton()
    {
        var button = new ToggleButton { Classes = { "ColumnFlowNav" } };
        button[!ThemeProperty] = new DynamicResourceExtension(ThemeKey);
        var index = _buttons.Count;
        _pages.Add(null);
        button.Click += (_, _) =>
        {
            // A page button never unlights itself: the panel decides what is lit.
            button.IsChecked = true;
            if (_pages[index] is { } page) PagePicked?.Invoke(page);
        };
        Children.Add(button);
        return button;
    }
}
