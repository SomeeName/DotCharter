using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Views.Editor.Inspector;

public partial class NotesInspectorView : UserControl
{
    public NotesInspectorView() => InitializeComponent();

    private void NotesList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is not ChartAnnotation annotation) return;
        if (DataContext is not InspectorViewModel vm) return;
        vm.Annotations.JumpToAnnotationCommand.Execute(annotation);
    }
}
