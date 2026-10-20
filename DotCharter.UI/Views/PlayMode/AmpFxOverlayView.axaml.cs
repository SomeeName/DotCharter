using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode;

/// <summary>
///     Play Mode's Amp/FX overlay: the shared <c>AmpFxPanel</c> (guitar VST3
///     chain, virtual instrument, drop tune) in a Fire modal card. Hosted once by
///     <see cref="PlayModeView" />, gated on <c>PlayModeViewModel.IsAmpFxOpen</c>.
/// </summary>
public partial class AmpFxOverlayView : UserControl
{
    public AmpFxOverlayView() => InitializeComponent();
}
