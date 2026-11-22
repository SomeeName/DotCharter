using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Issues tab's "Arrangement findings" card: every binding is to <c>InspectorViewModel</c>.</summary>
public partial class IssuesListView : UserControl
{
    public IssuesListView()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, OnRowClicked, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>A finding row or its Fix was clicked: the finding is now the one on the Fixes card.</summary>
    public event Action? FindingOpened;

    private void OnRowClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { CommandParameter: ArrangementIssue }) FindingOpened?.Invoke();
    }
}
