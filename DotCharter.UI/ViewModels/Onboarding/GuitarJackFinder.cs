using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Onboarding;

/// <summary>What <see cref="GuitarJackFinder" /> drives: the device's inputs and the meter on the one being listened to.</summary>
public interface IGuitarJackScanTarget
{
    /// <summary>The capture device's inputs, as the jack picker lists them.</summary>
    IReadOnlyList<InputJackOption> Jacks { get; }

    /// <summary>The input the guitar is set to now: put back when nothing is heard.</summary>
    int CurrentJack { get; }

    /// <summary>Opens <paramref name="jack" /> alone (mono) and points the meter at it.</summary>
    void ListenOn(int jack);

    /// <summary>The latest captured window's peak in dBFS; −∞ for silence.</summary>
    double ReadPeakDb();
}

/// <summary>
///     "Find my guitar" on the first-run Input page: while the user strums, each
///     of the capture device's inputs is opened in turn, its loudest peak kept,
///     and the loudest input above <see cref="HeardFloorDb" /> becomes the guitar's
///     jack. Nothing heard puts the jack back as it was and says so.
///     <para>
///         One input at a time, through the same configuration Settings writes,
///         because the engine opens only the inputs the configuration names. The
///         clock is injected, so the scan runs without waiting in tests.
///     </para>
/// </summary>
public sealed partial class GuitarJackFinder : ObservableObject
{
    /// <summary>Quieter than a strummed guitar at any sane gain, louder than an idle input's hum.</summary>
    public const double HeardFloorDb = -45.0;

    public const int PollsPerJack = 8;

    /// <summary>Lets the newly opened input fill the capture window before it is read.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private readonly IGuitarJackScanTarget _target;
    private readonly ILocalizationService _localization;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GuitarJackFinder(IGuitarJackScanTarget target, ILocalizationService localization,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _target = target;
        _localization = localization;
        _delay = delay ?? Task.Delay;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private bool _heardNothing;

    public bool HasStatus => Status.Length > 0;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>The loudest input at or above <see cref="HeardFloorDb" />; null when none is.</summary>
    public static int? Loudest(IReadOnlyList<double> peaksDb)
    {
        int? best = null;
        for (var i = 0; i < peaksDb.Count; i++)
        {
            if (peaksDb[i] >= HeardFloorDb && (best is not { } b || peaksDb[i] > peaksDb[b]))
                best = i;
        }

        return best;
    }

    private bool CanFind() => !IsRunning && _target.Jacks.Count > 1;

    /// <summary>Re-asks <see cref="CanFind" /> when the device, and with it the input count, changes.</summary>
    public void Refresh() => FindCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync(CancellationToken ct)
    {
        var jacks = _target.Jacks.ToArray();
        var original = _target.CurrentJack;
        var peaks = new double[jacks.Length];
        IsRunning = true;
        HeardNothing = false;
        try
        {
            for (var i = 0; i < jacks.Length; i++)
            {
                Status = _localization.Format("Onboarding_Input_FindListeningFmt", jacks[i].Label, i + 1, jacks.Length);
                _target.ListenOn(jacks[i].Jack);
                await _delay(Settle, ct);
                var peak = double.NegativeInfinity;
                for (var p = 0; p < PollsPerJack; p++)
                {
                    peak = Math.Max(peak, _target.ReadPeakDb());
                    await _delay(Poll, ct);
                }

                peaks[i] = peak;
            }

            if (Loudest(peaks) is { } found)
            {
                _target.ListenOn(jacks[found].Jack);
                Status = _localization.Format("Onboarding_Input_FoundFmt", jacks[found].Label);
            }
            else
            {
                _target.ListenOn(original);
                HeardNothing = true;
                Status = _localization["Onboarding_Input_NotFound"];
            }
        }
        catch (OperationCanceledException)
        {
            _target.ListenOn(original);
            Status = string.Empty;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
