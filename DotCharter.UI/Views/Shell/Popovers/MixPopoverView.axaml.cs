using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell.Popovers;

/// <summary>
///     The Mix popover, and the clock behind its INPUT LEVEL meter.
/// </summary>
/// <remarks>
///     <para>
///         The clock lives HERE and not on <c>TransportViewModel</c>, which is a
///         DI singleton alive from boot: a meter timer on it would poll the
///         audio engine for the whole session to feed a panel that is shut. The
///         amp rig made the same call for the same reason — the view owns the
///         clock, the view model owns the number.
///     </para>
///     <para>
///         The gate is ATTACH, and that is exactly right rather than merely
///         convenient: <c>PrewarmableFlyout</c> realizes this content at boot by
///         building the popup and then CANCELLING its own open, so no popup
///         window is ever created and the content is never parented into a
///         visual tree. Attached therefore means the panel really is on screen,
///         and the clock cannot run for a shut menu.
///     </para>
/// </remarks>
public partial class MixPopoverView : UserControl
{
    /// <summary>~30 Hz. Fast enough that a peak meter looks live, slow enough
    /// that the repaint gate (lit-bar count) absorbs most ticks.</summary>
    private static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(33);

    private readonly DispatcherTimer _meterClock;

    public MixPopoverView()
    {
        InitializeComponent();

        _meterClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = MeterInterval };
        _meterClock.Tick += (_, _) => (DataContext as MainWindowViewModel)?.Transport.PollInputMeter();

        AttachedToVisualTree += (_, _) => _meterClock.Start();
        DetachedFromVisualTree += (_, _) => _meterClock.Stop();
    }

    /// <summary>Test seam: whether the meter clock is running right now. The
    /// claim worth proving is not "a timer exists" but "it is stopped while
    /// nobody is looking".</summary>
    internal bool IsMeterRunning => _meterClock.IsEnabled;
}
