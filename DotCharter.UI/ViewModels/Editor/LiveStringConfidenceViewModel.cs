using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>One string's cell in the analysis monitor's confidence strip: its tuning
/// label, a meter as wide as the detector's certainty on it, and the number in a tooltip.</summary>
public sealed partial class LiveStringConfidenceItem(int stringIndex) : ObservableObject
{
    /// <summary>The meter's full width: a certainty of 1 fills it.</summary>
    public const double MeterWidth = 28;

    /// <summary>The chart string (0 = the lowest).</summary>
    public int StringIndex { get; } = stringIndex;

    /// <summary>The string's highway colour slot (<see cref="StringColorSlots.For" />) on the
    /// arrangement's string count — a 7-string's low E is red, its low B grey.</summary>
    [ObservableProperty] private int _colorSlot;

    [ObservableProperty] private string _label = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FillWidth))]
    private double _certainty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FillWidth))]
    [NotifyPropertyChangedFor(nameof(CellOpacity))]
    [NotifyPropertyChangedFor(nameof(IsSure))]
    private bool _hasData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSure))]
    private bool _isUnsure;

    [ObservableProperty] private string _toolTip = string.Empty;

    /// <summary>Judged, and not unsure: the meter wears the green fill. Neither fill shows on a
    /// string with nothing judged.</summary>
    public bool IsSure => HasData && !IsUnsure;

    public double FillWidth => HasData ? MeterWidth * Math.Clamp(Certainty, 0, 1) : 0;

    /// <summary>A string with nothing judged on it recently recedes rather than reading as empty.</summary>
    public double CellOpacity => HasData ? 1.0 : 0.45;
}

/// <summary>
/// The analysis monitor's per-string confidence strip, UI-thread only. The worker
///     samples <see cref="LiveStringConfidenceTracker" /> into the tick snapshot; <see cref="Apply" />
///     turns the readings into one cell per string of the active arrangement.
/// </summary>
public sealed class LiveStringConfidenceViewModel
{
    private readonly ILocalizationService _localization;

    public LiveStringConfidenceViewModel(ILocalizationService localization)
    {
        _localization = localization;
        Apply([], LabelsFor(null));
    }

    public ObservableCollection<LiveStringConfidenceItem> Strings { get; } = [];

    /// <summary>The strings to show, lowest first: the arrangement's own tuning names when it
    /// spells one per string, else the standard names for its string count (6 without one).</summary>
    public static IReadOnlyList<string> LabelsFor(Arrangement? arrangement)
    {
        var count = Math.Clamp(arrangement is { StringCount: > 0 } a ? a.StringCount : 6, 1,
            LiveStringConfidenceTracker.MaxStrings);
        return arrangement?.Tuning is { } tuning && tuning.Length == count
            ? tuning
            : TuningUtilities.GetStringLabels(count);
    }

    /// <summary>Shows <paramref name="readings" /> (indexed by string; a missing one is "nothing
    /// judged") on one cell per label.</summary>
    public void Apply(IReadOnlyList<LiveStringConfidenceReading> readings, IReadOnlyList<string> labels)
    {
        while (Strings.Count > labels.Count) Strings.RemoveAt(Strings.Count - 1);
        while (Strings.Count < labels.Count) Strings.Add(new LiveStringConfidenceItem(Strings.Count));

        for (var i = 0; i < labels.Count; i++)
        {
            var reading = i < readings.Count ? readings[i] : new LiveStringConfidenceReading(i, 0, 0f);
            var item = Strings[i];
            item.Label = labels[i];
            item.ColorSlot = StringColorSlots.For(i, labels.Count);
            item.Certainty = reading.Certainty;
            item.HasData = reading.HasData;
            item.IsUnsure = reading.IsUnsure;
            item.ToolTip = ToolTipFor(labels[i], reading);
        }
    }

    private string ToolTipFor(string label, LiveStringConfidenceReading reading)
    {
        if (!reading.HasData)
        {
            return _localization.Format("LivePlay_Monitor_StringConfidenceNone", label,
                LiveStringConfidenceTracker.WindowMs / 1000);
        }

        var text = _localization.Plural("LivePlay_Monitor_StringConfidenceOne", "LivePlay_Monitor_StringConfidence",
            reading.Samples, label, reading.Certainty);
        return reading.IsUnsure ? text + Environment.NewLine + _localization["LivePlay_Monitor_StringConfidenceUnsure"] : text;
    }
}
