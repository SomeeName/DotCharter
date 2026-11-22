using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

internal static class EofGraceChordCases
{
    internal static readonly string[] Names = ["B5", "Am7", "D/F#", "Open fifth", "123456789012345678901234567890"];

    public static void Add(ChartProject project, Arrangement arrangement, int at, int highestString, int seed)
    {
        var count = 2 + seed / 2 % 5;
        var grace = (seed / 10 % 3) switch { 0 => count - 1, 1 => count / 2, _ => 0 };
        var name = seed % 2 == 0 ? "" : Names[seed / 2 % Names.Length];
        AddChord(project, arrangement, at, highestString, count, grace, name, twoGraces: false);
        var target = arrangement.Levels[0].Chords[^1];
        foreach (var note in target.ChordNotes) note.SlideTo = note.Fret + (note.GraceFret != null ? 3 : 2);
        AddChord(project, arrangement, at + 3000, highestString, count, grace, name, twoGraces: true);
    }

    public static ChartProject NamedProject(string name = "B5", bool nop = false)
    {
        var project = EofGraceTechniqueRoundTripTests.Project(100, 1200, "plain");
        var arrangement = project.Arrangements[0];
        arrangement.Levels[0].Notes.Clear();
        AddChord(project, arrangement, 600, 1, 2, 0, name, twoGraces: false);
        var chord = arrangement.Levels[0].Chords[0];
        chord.ChordNotes[0].Fret = 7;
        chord.ChordNotes[0].GraceFret = 5;
        chord.ChordNotes[1].Fret = 9;
        var template = arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        template.Frets[0] = 7;
        template.Frets[1] = 9;
        template.NopInRs = nop;
        return project;
    }

    private static void AddChord(ChartProject project, Arrangement arrangement, int at, int highestString,
        int count, int grace, string name, bool twoGraces)
    {
        var template = new ChordTemplate { Id = arrangement.ChordTemplates.Count, Name = name };
        arrangement.ChordTemplates.Add(template);
        var chord = new Chord { Position = EofGraceNeighbourCases.At(project, at), TemplateId = template.Id };
        var sustain = EofGraceNeighbourCases.At(project, at + 1200) - chord.Position;
        for (var i = 0; i < count; i++)
        {
            var s = highestString - count + 1 + i;
            var fret = 5 + i * 2;
            template.Frets[s] = fret;
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = s, Fret = fret, Sustain = sustain,
                GraceFret = i == grace || twoGraces && i == (grace + 1) % count ? fret - 2 : null
            });
        }
        arrangement.Levels[0].Chords.Add(chord);
    }

    public static bool IsMultiGrace(ChartProject project, EofGraceExtendedCases.GraceRow row) => row.Chord &&
        project.Arrangements[0].Levels.Single(l => l.DifficultyIndex == row.Difficulty).Chords.Any(c =>
            c.Position == row.Position && c.ChordNotes.Count(n => n.GraceFret != null) > 1);

    public static List<string> Issues(ChartProject project, Arrangement imported, bool mainParser)
    {
        var issues = new List<string>();
        var source = project.Arrangements[0];
        foreach (var level in source.Levels)
        {
            foreach (var chord in level.Chords.Where(c => c.ChordNotes.Any(n => n.GraceFret != null)))
            {
                var template = source.ChordTemplates.Single(t => t.Id == chord.TemplateId);
                var actualLevel = imported.Levels.Single(l => l.DifficultyIndex == level.DifficultyIndex);
                var multiple = chord.ChordNotes.Count(n => n.GraceFret != null) > 1;
                var displaced = multiple && !mainParser;
                var targetMs = EofGraceNeighbourCases.Ms(project, chord.Position) +
                               (displaced ? EofGraceTiming.LengthAt(chord.Position, project.Beats) : 0);
                var target = actualLevel.Chords.SingleOrDefault(c => Near(project, c.Position, targetMs));
                var kind = multiple ? "multi-grace wire representation" : "named chord target";
                if (target == null)
                {
                    issues.Add($"{kind}: target attack or chord missing");
                    continue;
                }
                var actualTemplate = imported.ChordTemplates.Single(t => t.Id == target.TemplateId);
                if (actualTemplate.Name != template.Name || actualTemplate.NopInRs != template.NopInRs)
                    issues.Add($"{kind}: authored name or Nop changed");
                if (target.ChordNotes.Count != chord.ChordNotes.Count) issues.Add($"{kind}: lost chord tones");
                foreach (var note in chord.ChordNotes)
                {
                    var back = target.ChordNotes.SingleOrDefault(n => n.StringIndex == note.StringIndex);
                    if (back == null || back.Fret != note.Fret || back.GraceFret != (displaced ? null : note.GraceFret) ||
                        back.SlideTo != note.SlideTo || back.UnpitchedSlide != note.UnpitchedSlide ||
                        !Near(project, target.Position + back.Sustain, EofGraceNeighbourCases.Ms(project, chord.Position + note.Sustain)))
                    {
                        issues.Add($"{kind}: changed string {note.StringIndex} fret, grace or end");
                    }
                }
                if (displaced) CheckOrnament(project, actualLevel, chord, issues);
            }
        }
        return issues;
    }

    private static void CheckOrnament(ChartProject project, ChartLevel imported, Chord chord, List<string> issues)
    {
        var onset = EofGraceNeighbourCases.Ms(project, chord.Position);
        var graceLength = EofGraceTiming.LengthAt(chord.Position, project.Beats);
        var ornament = imported.Chords.SingleOrDefault(c => Near(project, c.Position, onset));
        var graced = chord.ChordNotes.Where(n => n.GraceFret != null).ToList();
        if (ornament == null || ornament.ChordNotes.Count != graced.Count)
        {
            issues.Add("multi-grace wire representation: missing played ornament");
            return;
        }
        foreach (var note in graced)
        {
            var back = ornament.ChordNotes.SingleOrDefault(n => n.StringIndex == note.StringIndex);
            if (back == null || back.Fret != note.GraceFret || back.SlideTo != note.Fret || !back.LinkNext ||
                back.GraceFret != null || !Near(project, ornament.Position + back.Sustain, onset + graceLength))
            {
                issues.Add("multi-grace wire representation: changed played ornament tone or timing");
            }
        }
    }

    private static bool Near(ChartProject project, FractionalPosition position, double ms) =>
        Math.Abs(EofGraceNeighbourCases.Ms(project, position) - ms) <= 1;
}
