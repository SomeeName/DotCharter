namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>
///     The Shapes panel's fixtures: an old chart as an import leaves it, never a tidy one. The
///     proportions follow a real user's chart: most shapes unnamed, about a quarter unused, and
///     one unnamed lead grip carried as several copies in two fingerings.
/// </summary>
internal static class FakeShapePanelData
{
    private static readonly FakeShapeIssue Duplicate = new("Same grip as “A5”", "Merge");
    private static readonly FakeShapeIssue NearAm = new("Nearly “Am”: other fingers on D and G", "Review");
    private static readonly FakeShapeIssue Drifted = new("2 hand shapes don't match their chords", "Sync");
    private static readonly FakeShapeIssue NoFingers = new("No fingering", "Finger");

    public static List<FakeShapePanelRow> OldChart(FakeShapePanelScreen panel) =>
    [
        new(FakeShapeGrips.LeadGrip, 31, 12, null, panel),
        new(FakeShapeGrips.LeadGrip, 4, 1, Duplicate with { Text = "Same grip as “x x 9 8 11 x” (3 copies)" }, panel),
        new(FakeShapeGrips.LeadGripOtherFingers, 2, 0, NearAm with { Text = "Nearly “x x 9 8 11 x”: other fingers" }, panel),
        new(FakeShapeGrips.Am, 22, 4, null, panel),
        new(FakeShapeGrips.AmOtherFingers, 2, 1, NearAm, panel),
        new(FakeShapeGrips.A5, 18, 6, null, panel),
        new(FakeShapeGrips.A5Copy, 6, 2, Duplicate, panel),
        new(FakeShapeGrips.C, 12, 3, Drifted, panel),
        new(FakeShapeGrips.G, 10, 3, null, panel),
        new(FakeShapeGrips.PowerNoName, 9, 2, null, panel),
        new(FakeShapeGrips.D, 7, 2, NoFingers with { Text = "No fingering on 3 strings" }, panel),
        new(FakeShapeGrips.DyadNoName, 0, 0, null, panel),
        new(FakeShapeGrips.Em, 8, 2, null, panel),
        new(FakeShapeGrips.F, 4, 1, Drifted with { Text = "1 hand shape doesn't match its chords" }, panel),
        new(FakeShapeGrips.E5, 15, 5, null, panel),
        new(FakeShapeGrips.Bm7b5, 0, 0, null, panel),
        new(FakeShapeGrips.Cadd9, 3, 0, null, panel),
        new(FakeShapeGrips.LeadGrip with { Frets = [-1, -1, 7, 6, 9, -1] }, 0, 0, null, panel),
        new(FakeShapeGrips.E5 with { Name = "" }, 0, 0, null, panel)
    ];

    public static List<FakeShapePanelRow> LongNames(FakeShapePanelScreen panel) =>
    [
        new(FakeShapeGrips.F with { Name = "Gesperrter Septakkord mit Barré im ersten Bund (Import)" }, 142, 38,
            new FakeShapeIssue("Gleicher Griff wie „F“ — andere Finger auf der D-Saite", "Prüfen"), panel),
        new(FakeShapeGrips.A5 with { Name = "ドロップDのパワーコード(ブリッジミュート)" }, 317, 64,
            new FakeShapeIssue("2つのハンドシェイプがコードと一致しません", "同期"), panel),
        new(FakeShapeGrips.PowerNoName, 1204, 311, null, panel),
        .. OldChart(panel)
    ];

    /// <summary>Ninety shapes, built by index so every run is the same: every third a duplicate, every seventh unused.</summary>
    public static List<FakeShapePanelRow> Many(FakeShapePanelScreen panel, int count)
    {
        FakeShape[] grips =
        [
            FakeShapeGrips.Am, FakeShapeGrips.C, FakeShapeGrips.G, FakeShapeGrips.D, FakeShapeGrips.Em,
            FakeShapeGrips.F, FakeShapeGrips.A5, FakeShapeGrips.E5, FakeShapeGrips.PowerNoName, FakeShapeGrips.Bm7b5
        ];
        var rows = new List<FakeShapePanelRow>(count);
        for (var i = 0; i < count; i++)
        {
            var grip = grips[i % grips.Length];
            var shift = i / grips.Length;
            var shape = grip with
            {
                Name = grip.Name.Length == 0 ? "" : shift == 0 ? grip.Name : $"{grip.Name} ({shift + 1})",
                Frets = grip.Frets.Select(f => f < 0 ? f : f + shift % 3).ToArray()
            };
            var unused = i % 7 == 6;
            var issue = unused ? null : i % 3 == 2 ? Duplicate with { Text = $"Same grip as “{FakeShapeGrips.Title(grip)}”" }
                : i % 11 == 4 ? Drifted : null;
            rows.Add(new FakeShapePanelRow(shape, unused ? 0 : 2 + i * 5 % 31, unused ? 0 : i % 4, issue, panel));
        }

        return rows;
    }
}
