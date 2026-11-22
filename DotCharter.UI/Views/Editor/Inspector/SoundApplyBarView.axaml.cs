using Avalonia;
using Avalonia.Controls;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
/// The Sound tab's pinned "Apply to" picker. The code-behind keeps the box on the
///     ladder's live rung (<see cref="ChordApplyLadder.Effective" />), forwards a pick to the
///     ladder's own verb, and — like the old chord panel's scope bar, whose job it took on the Sound tab
/// — reports itself as a host of the bulk-edit preview: pinned over the Technique and
///     Bends sections it is on screen while the chord panel is not, and the marks must show for the
/// technique, bend and keyboard edits it governs there (a technique KEY follows the rung
///     only while a host is on screen, <see cref="ChordApplyPreview.IsHostShown" />).
/// </summary>
public partial class SoundApplyBarView : UserControl
{
    private readonly EffectiveVisibilityWatcher _visibility;
    private readonly ApplyRungPickerSync _picker;
    private ChordApplyPreview? _previewHost;

    public SoundApplyBarView()
    {
        InitializeComponent();
        _picker = new ApplyRungPickerSync(this.FindControl<ComboBox>("SoundApplyPicker")!);
        _visibility = new EffectiveVisibilityWatcher(this, _ => ReportShown());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _picker.SetEditor(DataContext as ChordTemplateEditorViewModel, VisualRoot != null);
        ReportShown();
    }

    // The ladder is a singleton's: listen only while this view is on screen (a dock pane is
    // re-created on float/redock).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _picker.Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _picker.Unhook();
    }

    /// <summary>Tells the preview of the view model this row shows whether the row is
    /// effectively visible — and tells the previous one that it is not.</summary>
    private void ReportShown()
    {
        var preview = _picker.Editor?.Preview;
        if (!ReferenceEquals(preview, _previewHost)) _previewHost?.SetHostShown(this, false);
        _previewHost = preview;
        preview?.SetHostShown(this, _visibility.IsEffectivelyVisible);
    }
}
