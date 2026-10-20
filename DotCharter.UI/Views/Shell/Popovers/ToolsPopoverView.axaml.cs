using Avalonia.Controls;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell.Popovers;

/// <summary>
///     The Tools popover: tuner readout, drop tuning, MIDI instrument and the
///     guitar VST3 chain (the shared plugin rack).
/// </summary>
/// <remarks>
///     A live thing hangs off this view and it is gated on ATTACH — which is
///     the right gate, not merely a convenient one. A popover on this rail is
///     REALIZED at boot by <c>PrewarmableFlyout</c>, but that builds the popup and
///     then CANCELS its own open: no popup window is ever created, so the content
///     is never parented into a visual tree and never attached. Attached therefore
///     means the panel really is on screen; <c>MixPopoverView</c> states the same mechanism.
///     <para>
///         The <c>IN TUNE</c> readout holds a lease on the shared
///         <c>LiveTuner</c> while the panel is up — the same ~15 Hz detection the
///         Tuner dialog reads, released the moment the panel closes, so nothing
///         polls the input for a shut menu.
///     </para>
///     <para>
///         The VST3 chain is the shared plugin rack (<c>VstRackView</c>), which
///         owns its drag reorder and gates it on the same attach.
///     </para>
/// </remarks>
public partial class ToolsPopoverView : UserControl
{
    private IDisposable? _tunerLease;

    public ToolsPopoverView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) => StartMonitoringTuner();
        DetachedFromVisualTree += (_, _) => StopMonitoringTuner();
    }

    /// <summary>Test seam: whether this view is currently holding a tuner
    /// monitor. The claim worth proving is that a closed popover holds
    /// none.</summary>
    internal bool IsMonitoringTuner => _tunerLease != null;

    private void StartMonitoringTuner()
    {
        if (_tunerLease != null) return;

        // Null means there is nothing to tune against (no arrangement, or a
        // vocal one). The row then keeps its em dash instead of a number
        // nothing produced.
        _tunerLease = (DataContext as MainWindowViewModel)?.ToneRig.TunerReadout?.Begin();
    }

    private void StopMonitoringTuner()
    {
        _tunerLease?.Dispose();
        _tunerLease = null;
    }
}
