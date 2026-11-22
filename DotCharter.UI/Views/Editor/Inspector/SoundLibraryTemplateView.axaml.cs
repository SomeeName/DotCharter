using Avalonia.Controls;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
///     The Library page's Template card: the old browse mode's chord panel fields on the picked template. While it is
///     on screen it hosts the "Apply to" preview, as the chord panel it replaced did (the marks for a card nobody can
///     see would be noise).
/// </summary>
public partial class SoundLibraryTemplateView : UserControl
{
    private readonly EffectiveVisibilityWatcher _visibility;
    private ChordApplyPreview? _previewHost;

    public SoundLibraryTemplateView()
    {
        InitializeComponent();
        _visibility = new EffectiveVisibilityWatcher(this, _ => ReportShown());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReportShown();
    }

    private void ReportShown()
    {
        var preview = (DataContext as InspectorViewModel)?.ChordTemplateEditor.Preview;
        if (!ReferenceEquals(preview, _previewHost)) _previewHost?.SetHostShown(this, false);
        _previewHost = preview;
        preview?.SetHostShown(this, _visibility.IsEffectivelyVisible);
    }
}
