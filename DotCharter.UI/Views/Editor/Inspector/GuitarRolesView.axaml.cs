using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
/// The Guitar roles switch with its approximation caption and its
///     reason / result line. Hosted by the Mixer inspector's Stems card, over
///     <c>StemMixerViewModel.GuitarRoles</c>.
/// </summary>
public partial class GuitarRolesView : UserControl
{
    /// <summary>Builds the view from its markup.</summary>
    public GuitarRolesView() => InitializeComponent();
}
