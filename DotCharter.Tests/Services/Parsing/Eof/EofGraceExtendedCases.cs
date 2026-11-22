using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

internal static class EofGraceExtendedCases
{
    internal sealed record GraceRow(int Difficulty, int StringIndex, int Fret, int GraceFret,
        FractionalPosition Position, FractionalPosition EndPosition, bool Chord);

    private static readonly int[] CloseGaps = [10, 25, 50, 75];

    public static ChartProject Extend(ChartProject project, int seed)
    {
        var arrangement = project.Arrangements[0];
        arrangement.Capo = seed / 2 % 2 * 2;
        arrangement.StringCount = 7 + seed / 4 % 2;
        arrangement.Tuning = arrangement.StringCount == 7
            ? ["B", "E", "A", "D", "G", "B", "e"]
            : ["F#", "B", "E", "A", "D", "G", "B", "e"];
        var before = seed % 2 == 0 ? 600 : 300;
        project.Beats.Clear();
        var at = 0;
        for (var i = 0; i < 96; i++)
        {
            project.Beats.Add(new BeatMarker { Time = at });
            at += at < 12000 ? before : 900;
        }
        var gap = CloseGaps[seed % CloseGaps.Length];
        var count = 2 + seed / 4 % 2;
        AddCluster(project, arrangement.Levels[0], 11980, gap, count, arrangement.StringCount - 1, seed);
        EofGraceChordCases.Add(project, arrangement, 18000, arrangement.StringCount - 1, seed);
        var second = new ChartLevel { DifficultyIndex = 1 };
        AddCluster(project, second, 24000, gap, count, arrangement.StringCount - 2, seed + 1);
        arrangement.Levels.Add(second);
        return project;
    }

    private static void AddCluster(ChartProject project, ChartLevel level, int start, int gap, int count, int s, int seed)
    {
        for (var i = 0; i < count; i++)
        {
            var at = start + i * gap;
            var sustain = i == count - 1 ? 1200 : (seed + i) % 2 == 0 ? Math.Max(1, gap / 2) : gap * 2;
            level.Notes.Add(new GuitarNote
            {
                Position = EofGraceNeighbourCases.At(project, at),
                Sustain = EofGraceNeighbourCases.At(project, at + sustain) - EofGraceNeighbourCases.At(project, at),
                StringIndex = s, Fret = 7 + i * 2, GraceFret = 5 + i * 2
            });
        }
    }

    public static IEnumerable<GraceRow> Rows(Arrangement arrangement)
    {
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes.Where(n => n.GraceFret != null))
            {
                yield return new GraceRow(level.DifficultyIndex, note.StringIndex, note.Fret, note.GraceFret!.Value,
                    note.Position, note.EndPosition, false);
            }
            foreach (var chord in level.Chords)
            {
                foreach (var note in chord.ChordNotes.Where(n => n.GraceFret != null))
                {
                    yield return new GraceRow(level.DifficultyIndex, note.StringIndex, note.Fret, note.GraceFret!.Value,
                        chord.Position, chord.Position + note.Sustain, true);
                }
            }
        }
    }

    // A sustain shorter than the ornament, or one millisecond longer, cannot be written
    // exactly: the displaced target keeps one millisecond and folds back to exactly the
    // ornament (zero sustain stays zero, EofGraceZeroSustainRoundTripTests). Exclude that
    // entire rule (including zero sustain), never particular seeds or neighbours.
    // Round 4 explicitly requires multi-string ornaments to stay unnamed and
    // unfolded (otherwise the marker leaks into EOF's Rocksmith chord names).
    // Their complete two-sound representation is checked by EofGraceChordCases.
    public static bool PreservesEnd(ChartProject project, GraceRow row)
    {
        if (EofGraceChordCases.IsMultiGrace(project, row)) return false;
        var sustain = EofGraceNeighbourCases.Ms(project, row.EndPosition) - EofGraceNeighbourCases.Ms(project, row.Position);
        var ornament = EofGraceTiming.LengthAt(row.Position, project.Beats);
        return sustain >= ornament && sustain != ornament + 1;
    }

    public static List<string> Issues(ChartProject project, Arrangement imported, bool checkInvented, bool mainParser)
    {
        var issues = new List<string>();
        var expected = Rows(project.Arrangements[0]).ToList();
        var actual = Rows(imported).ToList();
        foreach (var row in expected.Where(r => PreservesEnd(project, r)))
        {
            var matches = actual.Where(r => Same(project, row, r)).ToList();
            var kind = row.Chord ? "chord" : "single";
            if (matches.Count != 1)
            {
                issues.Add($"missing {kind} grace: difficulty {row.Difficulty}, string {row.StringIndex}, onset {EofGraceNeighbourCases.Ms(project, row.Position)}");
                continue;
            }
            if (Math.Abs(EofGraceNeighbourCases.Ms(project, matches[0].EndPosition) - EofGraceNeighbourCases.Ms(project, row.EndPosition)) > 1)
                issues.Add($"changed {kind} end: difficulty {row.Difficulty}, onset {EofGraceNeighbourCases.Ms(project, row.Position)}");
        }
        if (checkInvented && actual.Any(r => !expected.Any(e => Same(project, e, r)))) issues.Add("invented grace");
        issues.AddRange(EofGraceChordCases.Issues(project, imported, mainParser));
        return issues;
    }

    private static bool Same(ChartProject project, GraceRow expected, GraceRow actual) =>
        expected.Difficulty == actual.Difficulty && expected.StringIndex == actual.StringIndex &&
        expected.Fret == actual.Fret && expected.GraceFret == actual.GraceFret && expected.Chord == actual.Chord &&
        Math.Abs(EofGraceNeighbourCases.Ms(project, expected.Position) - EofGraceNeighbourCases.Ms(project, actual.Position)) <= 1;
}
