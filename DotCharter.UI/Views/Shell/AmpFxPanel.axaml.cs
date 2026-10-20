using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// The guitar's VST3 chain, the virtual instrument for note preview and the
/// drop-tune pedal, bound to a <c>TransportViewModel</c> and drawn in the rig's
/// Fire language. Hosted by Play Mode's Amp/FX overlay.
/// </summary>
/// <remarks>
///     The chain is the SHARED plugin rack (<c>VstRackView</c>, over
///     <c>TransportViewModel.VstRack</c>) — the view the Tools popover and
///     RigLab's Plugins tab host too, with its own drag reorder. This panel
///     therefore holds no pointer controller of its own: two reorder gestures
///     over one chain is how two surfaces end up disagreeing about the signal
///     path.
/// </remarks>
public partial class AmpFxPanel : UserControl
{
    public AmpFxPanel() => InitializeComponent();
}
