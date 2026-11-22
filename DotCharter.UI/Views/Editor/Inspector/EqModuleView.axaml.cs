using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.Tone.UI.Controls;

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

    /// <summary>Started while effectively visible, STOPPED on hide and detach. The detach half is the
    /// rule this pane's siblings are held to (<c>ViewSubscriptionLeakGuardTests</c>):
    /// inspector views are re-created on every float/redock, so a timer that
    /// outlives its view polls the audio engine forever, once per redock.</summary>
    private readonly DispatcherTimer _spectrumTimer;

    private bool _visible;

    public EqModuleView()
    {
        InitializeComponent();

        _spectrumTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _spectrumTimer.Tick += OnSpectrumTick;
        _ = new EffectiveVisibilityWatcher(this, visible =>
        {
            _visible = visible;
            SyncSpectrumTimer();
        });
        // the pager arranges a put-away card off stage, still "visible": the Response card's plot is only read on screen
        EqCards.ShownCardsChanged += SyncSpectrumTimer;

        Plot.BandChanged += OnPlotBandChanged;
        Plot.BandSelected += OnPlotBandSelected;
    }

    /// <summary>The analyser is polled: the module is on screen AND its Response card is not paged away.</summary>
    internal bool IsPollingSpectrum => _spectrumTimer.IsEnabled;

    private void SyncSpectrumTimer()
    {
        if (_visible && EqCards.ShownCards.Contains(EqResponseCard)) _spectrumTimer.Start();
        else _spectrumTimer.Stop();
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

    /// <summary>A typed EQ field commits when it loses focus as on Enter (its Tag carries the same command).</summary>
    private void CommitField(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: ICommand command } && command.CanExecute(null)) command.Execute(null);
    }

    private void OnPlotBandSelected(object? sender, int index)
    {
        if (DataContext is EqModuleViewModel vm) vm.SelectedIndex = index;
    }
}
