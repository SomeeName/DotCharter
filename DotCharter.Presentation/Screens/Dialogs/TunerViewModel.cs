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
///     Tools ▸ Tuner. One instance per open: it takes a FRESH lease on the
///     shared <see cref="LiveTuner" /> (the strings reload and forget their last
///     readings), formats what the tuner hears on every change, and lets go when
///     the host disposes it — the Tools popover's readout keeps its own lease.
/// </summary>
public sealed class TunerViewModel : DialogViewModel<bool>, ITunerScreen, IDisposable
{
    private readonly LiveTuner _tuner;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IDisposable? _lease;
    private readonly TunerString[] _strings;

    private string _pitchShiftInfo = "";
    private string _statusHint = "";
    private bool _hasInput;
    private bool _hasReading;
    private string _noteText = "–";
    private string _centsText = "";
    private bool _isInTune;
    private double _centsOff;
    private string _tunedCountLabel = "";
    private bool _allTuned;

    public TunerViewModel(LiveTuner tuner, ILocalizationService localization, IAppLogger logger)
    {
        _tuner = tuner;
        _localization = localization;
        _logger = logger;
        CloseCommand = new RelayCommand(() => Close(true));

        _lease = tuner.Acquire(fresh: true);
        _strings = _lease == null ? [] : [.. tuner.Strings.Select(s => new TunerString(s.NoteName))];
        Refresh();
        tuner.Changed += Refresh;
        logger.Info(AppLogCategory.Audio, _lease == null
            ? "Tuner opened with nothing to tune against."
            : $"Tuner opened. Targets=[{string.Join(" ", _strings.Select(s => s.Label))}], hasInput={tuner.HasInput}.");
    }

    public string PitchShiftInfo
    {
        get => _pitchShiftInfo;
        private set
        {
            if (SetProperty(ref _pitchShiftInfo, value)) OnPropertyChanged(nameof(IsPitchShiftActive));
        }
    }

    public bool IsPitchShiftActive => PitchShiftInfo.Length > 0;

    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    public bool HasInput
    {
        get => _hasInput;
        private set => SetProperty(ref _hasInput, value);
    }

    public bool HasReading
    {
        get => _hasReading;
        private set => SetProperty(ref _hasReading, value);
    }

    public string NoteText
    {
        get => _noteText;
        private set => SetProperty(ref _noteText, value);
    }

    public string CentsText
    {
        get => _centsText;
        private set => SetProperty(ref _centsText, value);
    }

    public bool IsInTune
    {
        get => _isInTune;
        private set => SetProperty(ref _isInTune, value);
    }

    public double CentsOff
    {
        get => _centsOff;
        private set => SetProperty(ref _centsOff, value);
    }

    public IReadOnlyList<ITunerString> Strings => _strings;

    public string TunedCountLabel
    {
        get => _tunedCountLabel;
        private set => SetProperty(ref _tunedCountLabel, value);
    }

    public bool AllTuned
    {
        get => _allTuned;
        private set => SetProperty(ref _allTuned, value);
    }

    public ICommand CloseCommand { get; }

    public void Dispose()
    {
        _tuner.Changed -= Refresh;
        if (_lease == null) return;
        _lease.Dispose();
        _logger.Info(AppLogCategory.Audio, "Tuner closed.");
    }

    /// <summary>"+3 ¢" in the UI culture, a true minus sign when flat, ±0 at the centre.</summary>
    internal static string FormatCents(double cents, IFormatProvider culture) =>
        string.Create(culture, $"{cents:+0;−0;±0} ¢");

    private void Refresh()
    {
        if (IsClosed) return;

        if (_lease == null)
        {
            StatusHint = _localization["Tuner_NoArrangement"];
            return;
        }

        var shift = _tuner.PitchShiftSemitones;
        PitchShiftInfo = shift == 0 ? "" : _localization.Format("Tuner_DropPedal", shift.ToString("+0;−0"));
        HasInput = _tuner.HasInput;
        StatusHint = HasInput ? "" : _localization["Tuner_NoInput"];

        HasReading = _tuner.HasReading;
        IsInTune = _tuner.IsInTune;
        NoteText = HasReading ? _tuner.HeardNote : "–";
        CentsText = HasReading ? FormatCents(_tuner.CentsOff, CultureInfo.CurrentCulture) : "";
        CentsOff = HasReading ? _tuner.CentsOff : 0;

        var live = _tuner.Strings;
        for (var i = 0; i < _strings.Length && i < live.Count; i++) _strings[i].Update(live[i]);

        AllTuned = _tuner.AllTuned;
        TunedCountLabel = _strings.Length == 0 || AllTuned
            ? ""
            : _localization.Format("Tuner_TunedCountFmt", _tuner.TunedCount, _strings.Length);
    }

    private sealed class TunerString(string label) : ObservableObject, ITunerString
    {
        private bool _isNearest;
        private bool _isTuned;
        private double _centsOff;
        private bool _hasReading;

        public string Label { get; } = label;

        public bool IsNearest
        {
            get => _isNearest;
            private set => SetProperty(ref _isNearest, value);
        }

        public bool IsTuned
        {
            get => _isTuned;
            private set => SetProperty(ref _isTuned, value);
        }

        public double CentsOff
        {
            get => _centsOff;
            private set => SetProperty(ref _centsOff, value);
        }

        public bool HasReading
        {
            get => _hasReading;
            private set => SetProperty(ref _hasReading, value);
        }

        public void Update(LiveTunerString source)
        {
            IsNearest = source.IsNearest;
            IsTuned = source.IsTuned;
            CentsOff = source.CentsOff;
            HasReading = source.HasReading;
        }
    }
}
