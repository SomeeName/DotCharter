using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.Onboarding;

namespace DotCharter.UI.Views.Dialogs;

/// <summary>
///     The first-run wizard's view. Its only code is the input-meter clock: a
///     ~15 Hz <see cref="DispatcherTimer"/> that asks the view model to
///     <see cref="OnboardingWizardViewModel.PollInputMeter"/> while — and ONLY
///     while — the wizard is open on its Input page and this view is attached.
///     The view model stays timer-free (constructible in the default gate); the
///     clock lives here because a timer that runs while nothing shows it is the
///     exact fault the "no clocks on hidden surfaces" rule is about.
/// </summary>
public partial class OnboardingWizardView : UserControl
{
    private const int MeterIntervalMs = 66;

    private OnboardingWizardViewModel? _vm;
    private DispatcherTimer? _meter;
    private bool _attached;

    public OnboardingWizardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => { _attached = true; SyncMeter(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; SyncMeter(); };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm?.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as OnboardingWizardViewModel;
        _vm?.PropertyChanged += OnVmPropertyChanged;
        SyncMeter();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OnboardingWizardViewModel.Step)
                           or nameof(OnboardingWizardViewModel.IsOpen))
        {
            SyncMeter();
        }
    }

    /// <summary>Starts the meter clock when the Input page is showing, stops it
    /// otherwise. Idempotent — every trigger above calls it.</summary>
    private void SyncMeter()
    {
        var wanted = _attached && _vm is { IsOpen: true, IsInput: true };
        if (wanted)
        {
            _meter ??= new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(MeterIntervalMs)
            };
            _meter.Tick -= OnMeterTick;
            _meter.Tick += OnMeterTick;
            if (!_meter.IsEnabled) _meter.Start();
        }
        else if (_meter is { IsEnabled: true })
        {
            _meter.Stop();
        }
    }

    /// <summary>True while the meter clock is running — what the smoke test
    /// reads to prove the timer parks off the Input page.</summary>
    public bool IsMeterRunning => _meter?.IsEnabled == true;

    private void OnMeterTick(object? sender, EventArgs e) => _vm?.PollInputMeter();
}
