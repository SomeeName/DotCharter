using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor.Linking;

/// <summary>
///     The Inspector's "Linked · edits apply to N places" line, on the app's one
///     <c>LinkedMeasuresViewModel</c> (<c>InspectorViewModel.LinkedMeasures</c>). Hidden while the
///     selection is in no linked placement: the host binds its <c>IsVisible</c> to
///     <c>IsBannerVisible</c>.
/// </summary>
public partial class LinkBannerView : UserControl
{
    public LinkBannerView() => InitializeComponent();
}
