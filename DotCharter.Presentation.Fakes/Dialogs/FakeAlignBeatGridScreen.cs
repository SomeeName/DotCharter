using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IAlignBeatGridScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakeAlignBeatGridScreen : FakeDialog<bool>, IAlignBeatGridScreen
{
    private FakeAlignBeatGridScreen(double offsetMs, string now, string after, string estimate, bool hasEstimate)
    {
        OffsetMs = offsetMs;
        OffNowText = now;
        OffAfterText = after;
        EstimateText = estimate;
        HasEstimate = hasEstimate;
        ApplyCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IAlignBeatGridScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText)
    ];

    /// <summary>A chart with the fault: a grid 121 ms early against its own notes.</summary>
    public static FakeAlignBeatGridScreen Default() =>
        new(121.0, "40.2 ms", "0.1 ms", "Suggested move +121.0 ms, measured over 812 notes.", true);

    /// <summary>A chart with too few notes to measure.</summary>
    public static FakeAlignBeatGridScreen Empty() =>
        new(0, "—", "—", "Too few notes to measure. Enter the offset by hand.", false);

    /// <summary>The widest values the readouts take, and the German and Japanese notes.</summary>
    public static FakeAlignBeatGridScreen LongText() =>
        new(-999.9, "1234,5 ms", "1234,5 ms",
            "Vorschlag -249,9 ms, gemessen an 1024 Noten. Positiv verschiebt die Beats nach hinten; " +
            "推奨 -249.9 ms(1024個のノートから測定)。正の値でビートが後ろへ移動します。", true);

    public double OffsetMs { get; set; }

    public string OffNowText { get; }

    public string OffAfterText { get; }

    public string EstimateText { get; }

    public bool HasEstimate { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }
}
