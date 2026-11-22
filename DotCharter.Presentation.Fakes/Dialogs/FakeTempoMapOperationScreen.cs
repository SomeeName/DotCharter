using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ITempoMapOperationScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakeTempoMapOperationScreen : FakeDialog<bool>, ITempoMapOperationScreen
{
    private TempoMapOperationChoice? _selected;

    private FakeTempoMapOperationScreen(string title, string subtitle, string label, double value, double maximum,
        string format, TempoMapOperation operation = TempoMapOperation.AddSilenceAtStart)
    {
        if (operation == TempoMapOperation.InsertMeasuresAtPlayhead)
        {
            Choices = [new(operation, title)];
            ShowsChoices = false;
            ShowsMeasureTempo = true;
        }
        SelectedChoice = Choices.Single(choice => choice.Operation == operation);
        Title = title;
        Subtitle = subtitle;
        Label = label;
        Value = value;
        Maximum = maximum;
        FormatString = format;
        ApplyCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<ITempoMapOperationScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(AtPlayheadVariant, AtPlayhead)
    ];

    /// <summary>The variant opened for Insert measures at playhead: one verb, no choice row.</summary>
    public const string AtPlayheadVariant = "AtPlayhead";

    public static FakeTempoMapOperationScreen AtPlayhead() =>
        new("Insert measures at playhead", "Inserts empty bars before the bar at the playhead; everything after it moves back.",
            "Measure count", 2, 128, "0", TempoMapOperation.InsertMeasuresAtPlayhead);

    /// <summary>The measure-count operation at its largest value.</summary>
    public static FakeTempoMapOperationScreen Full() =>
        new("Add measures at end", "Adds empty bars after the last beat; nothing moves.", "Measure count", 128, 128, "0", TempoMapOperation.AddMeasuresAtEnd);

    public static FakeTempoMapOperationScreen Default() =>
        new("Start & end", "Shifts the whole chart later so the song does not start on the first frame.",
            "Silence (seconds)", 2.5, 600, "0.###");

    public static FakeTempoMapOperationScreen Empty() => new("", "", "", 0, 600, "0.###");

    public static FakeTempoMapOperationScreen LongText() =>
        new("Takte am Anfang hinzufügen, ohne die vorhandenen Taktartwechsel zu verschieben",
            "Fügt vor dem ersten Takt leere Takte im Tempo des ersten Schlags ein. 既存の拍子変更はそのまま保持され、" +
            "譜面全体が後ろにずれます。", "Anzahl der hinzuzufügenden Takte (小節数)", 128, 128, "0", TempoMapOperation.AddMeasuresAtStart);

    public string Title { get; }

    public string OperationLabel => "What to change";

    public IReadOnlyList<TempoMapOperationChoice> Choices { get; } =
    [
        new(TempoMapOperation.DefaultStartSilence, "Set default start silence"),
        new(TempoMapOperation.AddSilenceAtStart, "Add silence at beginning"),
        new(TempoMapOperation.AddSilenceAtEnd, "Add silence at end"),
        new(TempoMapOperation.AddMeasuresAtStart, "Add measures at start"),
        new(TempoMapOperation.AddMeasuresAtEnd, "Add measures at end")
    ];

    public bool ShowsChoices { get; } = true;

    public TempoMapOperationChoice? SelectedChoice
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string Subtitle { get; }

    public string Label { get; }

    public double Value { get; set; }

    public double Minimum => 0;

    public double Maximum { get; }

    public string FormatString { get; }

    public bool ShowsMeasureTempo { get; }

    public double BeatsPerMeasure { get; set; } = 3;

    public IReadOnlyList<int> BeatUnits { get; } = [2, 4, 8, 16];

    public int BeatUnit { get; set; } = 4;

    public double Bpm { get; set; } = 96.5;

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }
}
