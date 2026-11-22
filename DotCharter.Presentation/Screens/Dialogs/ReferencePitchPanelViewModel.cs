using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Audio.Tuning;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The arrangement dialog's A440 check. Measure reads the open song (the instrument's
///     cached stem, else the mix); the result shows the audio's offset beside the chart's, and when
///     they disagree confidently by more than the threshold, one click writes the suggestion into
///     the dialog's cent-offset field. Nothing changes silently: the dialog's Save is the undoable write.
/// </summary>
public sealed class ReferencePitchPanelViewModel : ObservableObject, IReferencePitchPanel
{
    private readonly IReferencePitchService? _service;
    private readonly ILocalizationService _loc;
    private readonly IAppLogger _logger;
    private readonly Func<bool> _isBass;
    private readonly Func<double> _authored;
    private readonly Action<double> _apply;
    private readonly AsyncRelayCommand _measure;
    private readonly RelayCommand _applyCommand;
    private string _statusText;
    private bool _isBusy;
    private ReferencePitchCheck? _suggestion;

    public ReferencePitchPanelViewModel(IReferencePitchService? service, ILocalizationService localization, IAppLogger logger,
        Func<bool> isBass, Func<double> authored, Action<double> apply)
    {
        _service = service;
        _loc = localization;
        _logger = logger;
        _isBass = isBass;
        _authored = authored;
        _apply = apply;
        _statusText = localization[service == null ? "Dialog_ReferencePitch_NoAudio" : "Dialog_ReferencePitch_Idle"];
        _measure = new AsyncRelayCommand(MeasureAsync, () => _service != null && !IsBusy);
        _applyCommand = new RelayCommand(Apply, () => HasSuggestion);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) _measure.NotifyCanExecuteChanged();
        }
    }

    public bool HasSuggestion => _suggestion is not null;

    public string ApplyLabel => _suggestion is { } s ? FormatApplyLabel(_loc, s.SuggestedCentOffset) : "";

    public ICommand MeasureCommand => _measure;

    public ICommand ApplyCommand => _applyCommand;

    /// <summary>The words for a finished check; pure so the tests can pin every branch.</summary>
    public static string Describe(ReferencePitchCheck check, ILocalizationService loc)
    {
        switch (check.Failure)
        {
            case ReferencePitchFailure.NoAudio:
                return loc["Dialog_ReferencePitch_NoAudio"];
            case ReferencePitchFailure.DecodeFailed:
                return loc["Dialog_ReferencePitch_Failed"];
        }

        if (!check.IsConfident) return loc.Format("Dialog_ReferencePitch_Unsure", Percent(check.Confidence));

        var authored = check.AuthoredSemitones == 0
            ? Signed(check.AuthoredCentOffset)
            : loc.Format("Dialog_ReferencePitch_WithSemitones", Signed(check.AuthoredCentOffset),
                Signed(check.AuthoredSemitones), Signed(check.AuthoredResidual));
        var line = FormatResult(loc, check.MeasuredCents, check.ReferenceHz, authored, check.Confidence);
        return check.ShouldOffer ? line : line + " " + loc["Dialog_ReferencePitch_Matches"];
    }

    /// <summary>The result line for measured values; shared by the dialog and its gallery fake.</summary>
    public static string FormatResult(ILocalizationService loc, double measuredCents, double referenceHz, string authored, double confidence) =>
        loc.Format("Dialog_ReferencePitch_Result", Signed(measuredCents), referenceHz.ToString("0.0", CultureInfo.CurrentCulture),
            authored, Percent(confidence));

    /// <summary>The Use button's label for a suggested cent offset.</summary>
    public static string FormatApplyLabel(ILocalizationService loc, double suggestedCents) =>
        loc.Format("Dialog_ReferencePitch_Apply", Signed(suggestedCents));

    private async Task MeasureAsync()
    {
        if (_service == null) return;
        IsBusy = true;
        SetSuggestion(null);
        StatusText = _loc["Dialog_ReferencePitch_Measuring"];
        try
        {
            var check = await _service.MeasureAsync(_isBass(), _authored(), CancellationToken.None);
            StatusText = Describe(check, _loc);
            if (check.ShouldOffer) SetSuggestion(check);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, "Reference pitch check failed", ex);
            StatusText = _loc["Dialog_ReferencePitch_Failed"];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply()
    {
        if (_suggestion is not { } s) return;
        _apply(s.SuggestedCentOffset);
        _logger.Info(AppLogCategory.Events,
            $"Reference pitch: cent offset {s.AuthoredCentOffset:0.#} -> {s.SuggestedCentOffset:0.#} in the arrangement dialog.");
        StatusText = _loc.Format("Dialog_ReferencePitch_Applied", Signed(s.SuggestedCentOffset));
        SetSuggestion(null);
    }

    private void SetSuggestion(ReferencePitchCheck? suggestion)
    {
        _suggestion = suggestion;
        OnPropertyChanged(nameof(HasSuggestion));
        OnPropertyChanged(nameof(ApplyLabel));
        _applyCommand.NotifyCanExecuteChanged();
    }

    private static string Signed(double cents) => cents.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture);

    private static string Percent(double confidence) => confidence.ToString("P0", CultureInfo.CurrentCulture);
}
