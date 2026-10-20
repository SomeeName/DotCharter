using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
///     The inspector's EQ module.
///     <para>
///         The code-behind owns exactly two things the markup cannot: the plot's
///         edit callbacks, and the analyser's poll timer.
///     </para>
/// </summary>
public partial class EqModuleView : UserControl
{
    /// <summary>
    ///     How often the analyser is re-read.
    ///     <para>
    ///         ~15 Hz. A spectrum display reads as continuous well below the
    ///         frame rate, and each poll is a 2048-point FFT on the message
    ///         thread — cheap, but not free, and there is no reason to pay for it
    ///         sixty times a second.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(66);

    /// <summary>Started on attach, STOPPED on detach. That second half is the
    /// rule this pane's siblings are held to (<c>ViewSubscriptionLeakGuardTests</c>):
    /// inspector views are re-created on every float/redock, so a timer that
    /// outlives its view polls the audio engine forever, once per redock.</summary>
    private readonly DispatcherTimer _spectrumTimer;

    public EqModuleView()
    {
        InitializeComponent();

        _spectrumTimer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, OnSpectrumTick);

        Plot.BandChanged += OnPlotBandChanged;
        Plot.BandSelected += OnPlotBandSelected;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _spectrumTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _spectrumTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSpectrumTick(object? sender, EventArgs e)
    {
        if (DataContext is EqModuleViewModel vm) vm.PollSpectrum();
    }

    /// <summary>A drag, a wheel or a double-click on the plot changed a band. The
    /// view model owns the push; the control never talks to audio.</summary>
    private void OnPlotBandChanged(object? sender, int index)
    {
        if (DataContext is EqModuleViewModel vm) vm.OnBandEditedFromPlot(index);
    }

    private void OnPlotBandSelected(object? sender, int index)
    {
        if (DataContext is EqModuleViewModel vm) vm.SelectedIndex = index;
    }
}
