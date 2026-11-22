using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IFixChartScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakeFixChartScreen : FakeDialog<bool>, IFixChartScreen
{
    private FakeFixChartScreen(string[] errors, string[] polish, bool checking, string total)
    {
        ErrorsChanges = [.. errors.Select(IFixChartChange (t) => new Row(t))];
        PolishChanges = [.. polish.Select(IFixChartChange (t) => new Row(t))];
        ErrorsSummary = errors.Length == 0 ? "Nothing to change." : $"{errors.Length} kinds of change";
        PolishSummary = polish.Length == 0 ? "Nothing to change." : $"{polish.Length} kinds of change";
        FixErrors = true;
        FixPolish = true;
        IsChecking = checking;
        TotalSummary = total;
        CanApply = !checking && (errors.Length + polish.Length) > 0;
        ApplyCommand = CanApply ? Answer(true) : Disabled();
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IFixChartScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    public static FakeFixChartScreen Default() => new(
        ["Duplicate notes and chords removed: 12", "Hand positions added at phrase starts: 4", "Chord template fingerings cleaned up: 2"],
        ["Hand positions restated at phrase starts: 9", "End phrase added or fixed: 1"], false, "Apply makes 28 changes as one undo step.");

    /// <summary>Nothing to fix: Apply is off and says why.</summary>
    public static FakeFixChartScreen Empty() => new([], [], false, "Nothing to change.");

    /// <summary>Every kind at once, with the long German and Japanese wording.</summary>
    public static FakeFixChartScreen LongText() => new(
        ["Notizen, Akkorde und Phrasen wieder in zeitliche Reihenfolge gebracht: 1.204", "Griffbrettpositionen an Phrasenanfängen ergänzt: 4.096",
            "Töne, die von Tonwechseln verwendet werden, deklariert: 3", "譜面の時間順に並べ直した音符・コード・フレーズ: 1204"],
        ["Handformen unter Akkord-Slides korrigiert: 17", "Überstehender Inhalt nach dem Ende des Audios abgeschnitten: 22"], false,
        "Anwenden nimmt 5.346 Änderungen in einem einzigen Rückgängig-Schritt vor.");

    /// <summary>Every kind either operation reports, at once: the lists at their longest.</summary>
    public static FakeFixChartScreen Full() => new(
        [.. new[]
        {
            "Notes, chords and phrases put back in time order", "Invalid chords repaired", "Duplicate notes and chords removed",
            "Linked notes joined", "Note lengths corrected", "Bends outside the allowed range removed", "Slides corrected",
            "Notes below the capo raised", "Hand positions added at phrase starts", "Hand shapes added over chords",
            "Chord template fingerings cleaned up", "Tones used by tone changes declared"
        }.Select((t, i) => $"{t}: {i + 3}")],
        [.. new[]
        {
            "Content past the end of the audio trimmed", "Extra beats after the audio removed", "Hand positions restated at phrase starts",
            "End phrase added or fixed", "Chord note sustains evened out", "Unneeded link-next targets removed",
            "Invalid link-nexts removed", "Hand shapes under chord slides fixed", "Hand shape lengths tidied"
        }.Select((t, i) => $"{t}: {i + 1}")],
        false, "Apply makes 120 changes as one undo step.");

    public static FakeFixChartScreen Busy() => new([], [], true, "Checking what would change…");

    public bool FixErrors { get; set; }

    public bool FixPolish { get; set; }

    public string ErrorsSummary { get; }

    public IReadOnlyList<IFixChartChange> ErrorsChanges { get; }

    public bool HasErrorsChanges => ErrorsChanges.Count > 0;

    public string PolishSummary { get; }

    public IReadOnlyList<IFixChartChange> PolishChanges { get; }

    public bool HasPolishChanges => PolishChanges.Count > 0;

    public bool IsChecking { get; }

    public string TotalSummary { get; }

    public bool CanApply { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed record Row(string Text) : IFixChartChange;
}
