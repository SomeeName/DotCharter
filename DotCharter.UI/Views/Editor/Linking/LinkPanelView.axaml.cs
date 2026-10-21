using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor.Linking;

/// <summary>
///     The link panel: the content of the <c>PrewarmableFlyout</c> a click on a LINKS chip opens
///     (<c>MainWindow</c> places it at the pointer). Its view model is
///     <c>LinkedMeasuresViewModel.Panel</c>.
/// </summary>
public partial class LinkPanelView : UserControl
{
    public LinkPanelView() => InitializeComponent();
}
