using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ITunerScreen" />; Close answers true.</summary>
public sealed class FakeTunerScreen : FakeDialog<bool>, ITunerScreen
{
    private FakeTunerScreen(IReadOnlyList<ITunerString> strings, string note, double cents, bool inTune,
        string pitchShiftInfo = "", string statusHint = "", string tunedCount = "", bool allTuned = false)
    {
        Strings = strings;
        HasInput = statusHint.Length == 0;
        StatusHint = statusHint;
        PitchShiftInfo = pitchShiftInfo;
        HasReading = note.Length > 0;
        NoteText = HasReading ? note : "–";
        CentsText = HasReading ? $"{cents:+0;−0;±0} ¢" : "";
        IsInTune = inTune;
        CentsOff = HasReading ? cents : 0;
        TunedCountLabel = tunedCount;
        AllTuned = allTuned;
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<ITunerScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>Standard guitar half tuned, the A string heard 12 ¢ sharp; the untouched strings say Pluck.</summary>
    public static FakeTunerScreen Default() => new(
    [
        new Row("E2", tuned: true, cents: 1),
        new Row("A2", nearest: true, cents: 12),
        new Row("D3", tuned: true, cents: -2),
        new Row("G3"),
        new Row("B3", cents: -31),
        new Row("E4")
    ], "A2", 12, false, tunedCount: "2 of 6 in tune");

    /// <summary>No input stream: the notice replaces the needle and the rows.</summary>
    public static FakeTunerScreen Empty() => new([], "", 0, false,
        statusHint: "Audio engine not running – configure an input device in Settings first");

    /// <summary>German drop-pedal strip and Japanese notice, the longest copy the dialog carries.</summary>
    public static FakeTunerScreen LongText() => new(
    [
        new Row("D2", nearest: true, cents: -4),
        new Row("G2"),
        new Row("C3"),
        new Row("F3"),
        new Row("A3"),
        new Row("D4")
    ], "D2", -4, true,
        pitchShiftInfo: "Drop-Pedal aktiv: −2 HT – das Stimmgerät hört das verschobene Signal",
        tunedCount: "6本中0本が合致");

    /// <summary>A seven-string, every string confirmed, the drop pedal on.</summary>
    public static FakeTunerScreen Full() => new(
    [
        new Row("B1", tuned: true, cents: 2),
        new Row("E2", tuned: true, cents: -1),
        new Row("A2", tuned: true, cents: 0),
        new Row("D3", tuned: true, cents: 3),
        new Row("G3", tuned: true, cents: -2),
        new Row("B3", tuned: true, cents: 1),
        new Row("E4", nearest: true, tuned: true, cents: 0)
    ], "E4", 0, true, pitchShiftInfo: "Drop pedal active: −1 st — the tuner hears the shifted signal",
        tunedCount: "", allTuned: true);

    public string PitchShiftInfo { get; }

    public bool IsPitchShiftActive => PitchShiftInfo.Length > 0;

    public string StatusHint { get; }

    public bool HasInput { get; }

    public bool HasReading { get; }

    public string NoteText { get; }

    public string CentsText { get; }

    public bool IsInTune { get; }

    public double CentsOff { get; }

    public IReadOnlyList<ITunerString> Strings { get; }

    public string TunedCountLabel { get; }

    public bool AllTuned { get; }

    public ICommand CloseCommand { get; }

    private sealed class Row(string label, bool nearest = false, bool tuned = false, double? cents = null)
        : ObservableObject, ITunerString
    {
        public string Label { get; } = label;

        public bool IsNearest { get; } = nearest;

        public bool IsTuned { get; } = tuned;

        public double CentsOff { get; } = cents ?? 0;

        public bool HasReading { get; } = cents.HasValue;
    }
}
