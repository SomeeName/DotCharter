using Avalonia.Controls;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Panels;

/// <summary>
/// The chord panel's "Apply to" ladder as its own view, over the same
///     <c>ChordTemplateEditorViewModel</c>: <see cref="ChordTemplateEditorView" /> embeds it, and the
///     Sound tab pins it above its segments with the editor's own copy hidden.
///     <para>
/// It is a preview host of its own: pinned over the Technique and Playback segments it
/// is on screen while the chord panel is not, and the bulk-edit marks must show for
///         the technique edit it governs there.
///     </para>
/// </summary>
public partial class ChordApplyScopeBar : UserControl
{
    private readonly EffectiveVisibilityWatcher _visibility;
    private ChordApplyPreview? _previewHost;

    public ChordApplyScopeBar()
    {
        InitializeComponent();
        _visibility = new EffectiveVisibilityWatcher(this, _ => ReportShown());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReportShown();
    }

    /// <summary>Tells the preview of the view model this bar shows whether the bar is
    /// effectively visible — and tells the previous one that it is not.</summary>
    private void ReportShown()
    {
        var preview = (DataContext as ChordTemplateEditorViewModel)?.Preview;
        if (!ReferenceEquals(preview, _previewHost)) _previewHost?.SetHostShown(this, false);
        _previewHost = preview;
        preview?.SetHostShown(this, _visibility.IsEffectivelyVisible);
    }
}
