
namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>One scope as the fake states it: its caption, what it reaches, how many uses.</summary>
internal sealed record FakeScopeSpec(string Label, string Detail, int Reach, int HandShapes = -1);

/// <summary>Everything one editor variant shows.</summary>
internal sealed record FakeShapeEditSpec(
    FakeShape Before,
    FakeShape After,
    string[] StringNames,
    string OpenedFrom,
    FakeScopeSpec[] Scopes,
    int SelectedScope,
    int TotalUses,
    int HandShapes,
    int Bars,
    IReadOnlyList<int> UseBars,
    int OriginBar,
    string Suggested,
    int MatchUses = 0,
    string MatchName = "",
    string ResultOverride = "",
    string ApplyOverride = "");

/// <summary>The fixtures: an old chart's Am used all over the song, and the edge cases.</summary>
internal static class FakeShapeEditSpecs
{
    private static readonly string[] Standard = ["E", "A", "D", "G", "B", "e"];

    public static FakeShapeEditSpec AmToAm7(int selected) => new(
        FakeShapeGrips.Am, FakeShapeGrips.Am7 with { Name = "Am" }, Standard,
        "Opened from the chord at bar 12, beat 3",
        [
            new("This chord", "1 chord · bar 12", 1),
            new("Selection", "4 chords · bars 9–16", 4),
            new("Every use", "14 chords on 3 levels", 14)
        ],
        selected, 14, 3, 64, [4, 5, 9, 11, 12, 13, 16, 28, 29, 32, 44, 45, 48, 60], 12, "Am7");

    public static FakeShapeEditSpec SingleUse() => new(
        FakeShapeGrips.Bm7b5, FakeShapeGrips.Bm7b5, Standard,
        "Opened from the chord at bar 30, beat 1",
        [
            new("This chord", "1 chord · bar 30", 1),
            new("Selection", "Nothing selected", 0),
            new("Every use", "Only this chord", 1)
        ],
        0, 1, 0, 64, [30], 30, "");

    public static FakeShapeEditSpec LongNames() => new(
        FakeShapeGrips.F with { Name = "Gesperrter Septakkord mit Barré im ersten Bund (Import)" },
        FakeShapeGrips.F with { Name = "ドロップDのパワーコード(ブリッジミュート)", Fingers = [1, 3, 4, 2, 1, -1] },
        Standard,
        "Geöffnet vom Akkord in Takt 112, Schlag 4 — Strophe 2 (Doppelgriff)",
        [
            new("Nur dieser Akkord", "1 Akkord · Takt 112", 1),
            new("Auswahl", "17 Akkorde · Takte 97–128 und 141–160", 17),
            new("Jede Verwendung", "142 Akkorde auf 9 Schwierigkeitsstufen", 142)
        ],
        2, 142, 38, 160, Enumerable.Range(0, 160).Where(b => b % 3 != 1).ToList(), 112, "Fmaj7/C");

    public static FakeShapeEditSpec EightString() => new(
        FakeShapeGrips.EightString, FakeShapeGrips.EightStringEdited,
        ["F#", "B", "E", "A", "D", "G", "B", "e"],
        "Opened from the chord at bar 187, beat 2",
        [
            new("This chord", "1 chord · bar 187", 1),
            new("Selection", "36 chords · bars 160–220", 36),
            new("Every use", "212 chords on 3 levels", 212)
        ],
        1, 212, 51, 220, Enumerable.Range(0, 220).Where(b => b % 4 != 3).ToList(), 187, "F#5add4");

    public static FakeShapeEditSpec Dyad() => new(
        FakeShapeGrips.DyadNoName, FakeShapeGrips.DyadNoName with { Fingers = [-1, -1, 3, 1, -1, -1] }, Standard,
        "Opened from the chord at bar 41, beat 2",
        [
            new("This chord", "1 chord · bar 41", 1),
            new("Selection", "Nothing selected", 0),
            new("Every use", "6 chords", 6)
        ],
        2, 6, 2, 64, [40, 41, 42, 52, 53, 54], 41, "");

    /// <summary>Three ticked copies of one grip in two fingerings, fingered alike: they become one shape.</summary>
    public static FakeShapeEditSpec SelectedCopies() => new(
        FakeShapeGrips.LeadGripOtherFingers, FakeShapeGrips.LeadGrip, Standard,
        "Opened on 3 ticked shapes in the Shapes panel",
        [
            new("This shape", "2 chords", 2, 0),
            new("3 selected shapes", "37 chords · 13 hand shapes", 37, 13)
        ],
        1, 37, 13, 64, [2, 3, 6, 7, 10, 11, 18, 19, 22, 23, 34, 35, 38, 39, 50, 51], 6, "",
        ResultOverride: "The 3 shapes end up identical, so they become one shape; the 2 emptied copies are removed in the same undo step.",
        ApplyOverride: "Apply to 3 shapes (37 chords)");

    public static FakeShapeEditSpec HandShape() => new(
        FakeShapeGrips.C, FakeShapeGrips.Cadd9 with { Name = "C" }, Standard,
        "Opened from the hand shape over bars 20–21",
        [
            new("This hand shape", "1 hand shape · its chords keep C", 1, 1),
            new("Hand shape and its chords", "1 hand shape · 4 chords", 4, 1),
            new("Every use", "22 chords · 6 hand shapes", 22, 6)
        ],
        1, 22, 6, 64, [3, 4, 20, 21, 22, 36, 37, 38, 50, 51], 20, "Cadd9");
}

/// <summary>The sentences the editor prints, in the language of the variant.</summary>
internal sealed record FakeShapeEditWords(
    Func<int, int, string, string> Preview,
    Func<int, int, string, string, string> Result,
    Func<int, int, string> Apply,
    Func<int, bool, string> HandShapes,
    Func<string, int, string> Match)
{
    public static readonly FakeShapeEditWords English = new(
        (reach, rest, name) => rest == 0
            ? $"{reach} {Chords(reach)} change"
            : $"{reach} {Chords(reach)} change · {rest} keep “{name}”",
        (reach, total, name, match) => match.Length > 0
            ? $"The {reach} {Chords(reach)} move onto “{match}”; no new shape is made."
            : reach >= total
                ? $"“{name}” itself changes, so every use plays the new grip."
                : $"A new shape is made for {(reach == 1 ? "this chord" : $"these {reach} chords")}; the other {total - reach} keep “{name}”.",
        (reach, total) => reach >= total && total > 1 ? $"Apply to every use ({total})"
            : reach == 1 ? "Apply to this chord" : $"Apply to {reach} chords",
        (handShapes, sync) => handShapes == 0 ? "No hand shape covers these chords."
            : sync ? $"{handShapes} hand {(handShapes == 1 ? "shape over these chords follows" : "shapes over these chords follow")} the new grip."
            : $"{handShapes} hand {(handShapes == 1 ? "shape keeps" : "shapes keep")} the old grip and will no longer match their chords.",
        (name, uses) => $"These frets and fingers are already “{name}” ({uses} uses).");

    public static readonly FakeShapeEditWords German = new(
        (reach, rest, name) => rest == 0 ? $"{reach} Akkorde ändern sich" : $"{reach} Akkorde ändern sich · {rest} behalten „{name}“",
        (reach, total, name, _) => reach >= total
            ? $"„{name}“ selbst ändert sich, also spielt jede Verwendung den neuen Griff."
            : $"Für diese {reach} Akkorde wird eine neue Form angelegt; die übrigen {total - reach} behalten „{name}“.",
        (reach, total) => reach >= total ? $"Auf jede Verwendung anwenden ({total})" : $"Auf {reach} Akkorde anwenden",
        (handShapes, sync) => sync
            ? $"{handShapes} Handformen über diesen Akkorden folgen dem neuen Griff."
            : $"{handShapes} Handformen behalten den alten Griff und passen nicht mehr zu ihren Akkorden.",
        (name, uses) => $"Diese Bünde und Finger sind bereits „{name}“ ({uses} Verwendungen).");

    private static string Chords(int n) => n == 1 ? "chord" : "chords";
}
