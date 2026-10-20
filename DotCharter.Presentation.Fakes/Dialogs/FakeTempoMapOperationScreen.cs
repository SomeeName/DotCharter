using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ITempoMapOperationScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakeTempoMapOperationScreen : FakeDialog<bool>, ITempoMapOperationScreen
{
    private FakeTempoMapOperationScreen(string title, string subtitle, string label, double value, double maximum,
        string format)
    {
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
        new(FakeVariantNames.LongText, LongText)
    ];

    public static FakeTempoMapOperationScreen Default() =>
        new("Add silence at start", "Shifts the whole chart later so the song does not start on the first frame.",
            "Silence (seconds)", 2.5, 600, "0.###");

    public static FakeTempoMapOperationScreen Empty() => new("", "", "", 0, 600, "0.###");

    public static FakeTempoMapOperationScreen LongText() =>
        new("Takte am Anfang hinzufügen, ohne die vorhandenen Taktartwechsel zu verschieben",
            "Fügt vor dem ersten Takt leere Takte im Tempo des ersten Schlags ein. 既存の拍子変更はそのまま保持され、" +
            "譜面全体が後ろにずれます。", "Anzahl der hinzuzufügenden Takte (小節数)", 128, 128, "0");

    public string Title { get; }

    public string Subtitle { get; }

    public string Label { get; }

    public double Value { get; set; }

    public double Minimum => 0;

    public double Maximum { get; }

    public string FormatString { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }
}
