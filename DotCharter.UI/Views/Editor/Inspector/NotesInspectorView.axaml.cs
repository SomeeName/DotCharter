using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Annotations tab: the filtered list and the details card (design D's cards).</summary>
public partial class NotesInspectorView : UserControl
{
    public NotesInspectorView()
    {
        InitializeComponent();
        Cards = this.FindControl<ColumnFlowPanel>("NotesCards")!;
        this.FindControl<TextBox>("NotesSelectedText")!.LostFocus += (_, _) =>
            (DataContext as InspectorViewModel)?.Annotations.CommitSelectedText();
    }

    /// <summary>The card body — the test hook.</summary>
    internal ColumnFlowPanel Cards { get; }

    private void ShowDetails() => Cards.ShowPage(this.FindControl<Control>("NotesDetailsCard")!);

    private void EditSelected_Click(object? sender, RoutedEventArgs e) => ShowDetails();

    private void ShowDetails_Click(object? sender, RoutedEventArgs e)
    {
        ShowDetails();
        this.FindControl<TextBox>("NotesComposerText")?.Focus();
    }

    private void NotesList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is not ChartAnnotation annotation) return;
        if (DataContext is not InspectorViewModel vm) return;
        vm.Annotations.JumpToAnnotationCommand.Execute(annotation);
    }
}
