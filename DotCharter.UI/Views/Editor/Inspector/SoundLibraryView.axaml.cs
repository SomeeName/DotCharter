using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.UI.Controls;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Library page's template list card: search, the list, New template and Edit template.</summary>
public partial class SoundLibraryView : UserControl
{
    public SoundLibraryView() => InitializeComponent();

    /// <summary>"Edit template": the Edit shape dialog opens on the picked template (its bound command),
    /// and the page's Template card comes on screen for its name and fingers (it may sit on another page of the cards).</summary>
    private void OnEditTemplate(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<ColumnFlowPanel>() is not { } panel) return;
        var card = panel.Children.OfType<ColumnFlowCard>().FirstOrDefault(c => c.Content is SoundLibraryTemplateView);
        if (card is not null) panel.ShowPage(card);
    }
}
