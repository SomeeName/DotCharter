using Avalonia;
using Avalonia.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Bends page's scope card: what Apply reaches (a chord's "Bend scope" rung), the target, the range.</summary>
public partial class SoundBendsScopeView : UserControl
{
    private readonly ApplyRungPickerSync _scope;

    public SoundBendsScopeView()
    {
        InitializeComponent();
        _scope = new ApplyRungPickerSync(this.FindControl<ComboBox>("SoundBendsScopePicker")!);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _scope.SetEditor((DataContext as InspectorViewModel)?.ChordTemplateEditor, VisualRoot != null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scope.Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _scope.Unhook();
    }
}
