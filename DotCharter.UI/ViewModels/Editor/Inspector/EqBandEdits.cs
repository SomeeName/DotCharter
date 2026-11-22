using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Equalizer;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     Inspector design D's typed edits on the EQ Band card, beside the sliders: Frequency (20–20000 Hz), Gain (±18 dB, the
///     engine's own range) and Q (0.3–18) as text fields that commit on Enter or when they lose focus and name the range
///     inline when a value is out of it; and "Reset gain + Q", which asks first and puts the selected band's gain and Q
///     back to the preset it was loaded from (flat 0 dB / the default Q with no preset). Type, frequency, enable and
///     solo stay. Every write goes through the module's one commit path (<c>EqModuleViewModel.Commit</c>).
/// </summary>
public sealed partial class EqBandEdits : ObservableObject
{
    private readonly EqSection _section;
    private readonly Func<int> _selected;
    private readonly Action _commit;
    private readonly ILocalizationService _localization;

    private const string FrequencyFormat = "0";
    private const string GainFormat = "0.0";
    private const string QFormat = "0.00";

    [ObservableProperty] private string _frequencyInput = "";
    [ObservableProperty] private string _gainInput = "";
    [ObservableProperty] private string _qInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    internal EqBandEdits(EqSection section, Func<int> selected, Action commit, ILocalizationService localization)
    {
        _section = section;
        _selected = selected;
        _commit = commit;
        _localization = localization;
        BasePresetKey = section.PresetKey;
        Load();
    }

    /// <summary>The preset the bands were last loaded from; an edit makes the section custom, this remembers the source.</summary>
    public string? BasePresetKey { get; internal set; }

    /// <summary>The out-of-range sentence is on screen.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>"Reset band 2's gain and Q? Type, frequency, enable and solo stay." under the Band card's buttons.</summary>
    public InspectorPendingAction ResetConfirm { get; } = new();

    private EqBand Band => _section.Bands[_selected()];

    /// <summary>Re-reads the selected band into the fields (a slider, the plot or another band moved).</summary>
    public void Load()
    {
        var band = Band;
        FrequencyInput = band.FrequencyHz.ToString(FrequencyFormat, CultureInfo.CurrentCulture);
        GainInput = band.GainDb.ToString(GainFormat, CultureInfo.CurrentCulture);
        QInput = band.Q.ToString(QFormat, CultureInfo.CurrentCulture);
        Error = "";
    }

    [RelayCommand]
    private void CommitFrequency() =>
        Commit(FrequencyInput, Band.FrequencyHz, FrequencyFormat, EqLimits.MinFrequencyHz, EqLimits.MaxFrequencyHz, "Mixer_Eq_FrequencyRange",
            hz => Band with { FrequencyHz = hz });

    [RelayCommand]
    private void CommitGain() =>
        Commit(GainInput, Band.GainDb, GainFormat, -EqLimits.MaxGainDb, EqLimits.MaxGainDb, "Mixer_Eq_GainRange", db => Band with { GainDb = db });

    [RelayCommand]
    private void CommitQ() => Commit(QInput, Band.Q, QFormat, EqLimits.MinQ, EqLimits.MaxQ, "Mixer_Eq_QRange", q => Band with { Q = q });

    /// <summary>Asks, then the selected band's gain and Q back to its preset's (or flat); one commit.</summary>
    [RelayCommand]
    private void Reset()
    {
        var index = _selected();
        ResetConfirm.Ask(_localization.Format("Mixer_Eq_ResetAsk", index + 1), () =>
        {
            var source = EqPresets.ByKey(BasePresetKey) is { } preset && index < preset.Count ? preset[index] : default(EqBand?);
            _section.SetBand(index, _section.Bands[index] with
            {
                GainDb = source?.GainDb ?? 0.0,
                Q = source?.Q ?? EqLimits.DefaultQ
            });
            _commit();
        });
    }

    // A field left unchanged (Tab walks through it, or it reads the shown value again) writes nothing: the rounded text
    // would otherwise replace the exact value and turn the preset Custom.
    private void Commit(string text, double current, string format, double min, double max, string rangeKey, Func<double, EqBand> with)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            Error = _localization.Format(rangeKey, Format(min), Format(max));
            return;
        }

        if (value < min || value > max || double.IsNaN(value))
        {
            Error = _localization.Format(rangeKey, Format(min), Format(max));
            return;
        }

        if (value.ToString(format, CultureInfo.CurrentCulture) == current.ToString(format, CultureInfo.CurrentCulture))
        {
            Load();
            return;
        }

        _section.SetBand(_selected(), with(value));
        _commit();
        Load();
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
}
