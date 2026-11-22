using System.Text.Json;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofGraceKindPolicyTests
{
    [Fact]
    public void A_merged_tremolo_run_keeps_the_grace_and_both_original_notes()
    {
        var project = ExtendedProject(100, "tremolo after");
        var notes = Import(EofExporter.Export(project).Bytes).Levels[0].Notes;
        Assert.Equal(2, notes.Count);
        Assert.Equal(5, notes[0].GraceFret);
        Assert.All(notes, n => Assert.True(n.Tremolo));
        Assert.Equal(new[] { 600.0, 2400.0 }, notes.Select(n => n.Position.GetPositionMs(project.Beats)));
        Assert.Equal(new[] { 1800.0, 3000.0 }, notes.Select(n => n.EndPosition.GetPositionMs(project.Beats)));
    }

    [Fact]
    public void A_quantised_bend_keeps_the_grace_and_mains_rounded_curve()
    {
        var project = ExtendedProject(100, "bend 1.25");
        var note = Assert.Single(Import(EofExporter.Export(project).Bytes).Levels[0].Notes);
        Assert.Equal(5, note.GraceFret);
        Assert.Equal(new[] { 1.0, 0.5, 0.0 }, note.BendValues.Select(b => b.Step));
        Assert.Equal(new[] { 600.0, 900.0, 1200.0 }, note.BendValues.Select(b => b.Position.GetPositionMs(project.Beats)));
    }

    [Fact]
    public void Extended_unchanged_techniques_and_exportable_contexts_keep_their_graces()
    {
        var rows = new List<Action>();
        foreach (var (_, project) in ExtendedRows()) rows.Add(() => AssertExtended(project));
        Assert.Multiple(rows.ToArray());
    }

    internal static void CompareWithMain(Func<byte[], Arrangement> importMain, string outputPath)
    {
        var rows = new List<object>();
        var differences = new List<string>();
        foreach (var (label, project) in AllRows())
        {
            var bytes = EofExporter.Export(project).Bytes;
            var actual = JsonSerializer.Serialize(Import(bytes));
            var expected = JsonSerializer.Serialize(importMain(bytes));
            var equal = expected == actual;
            var main = JsonDocument.Parse(expected).RootElement.Clone();
            var branch = JsonDocument.Parse(actual).RootElement.Clone();
            rows.Add(new { label, equal, main, branch });
            if (!equal) differences.Add(label);
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        // The report must list any differences, including improvements from earlier rounds.
        Console.WriteLine($"Main comparison: {rows.Count} rows; {differences.Count} differences: {string.Join(", ", differences)}");
    }

    private static IEnumerable<(string Label, ChartProject Project)> AllRows()
    {
        var techniques = new[] { "plain", "bend at onset", "bend later", "prebend", "slide", "unpitched slide",
            "vibrato", "tremolo", "harmonic", "palm mute", "hammer on", "pull off", "tap", "accent" };
        foreach (var bpm in new[] { 100, 200 })
        {
            foreach (var sustain in new[] { 0, 1200 })
            {
                foreach (var technique in techniques)
                    yield return ($"{bpm}/{sustain}/{technique}", EofGraceTechniqueRoundTripTests.Project(bpm, sustain, technique));
            }
        }
        foreach (var row in ExtendedRows()) yield return row;
    }

    private static IEnumerable<(string Label, ChartProject Project)> ExtendedRows()
    {
        var variants = new[] { "tremolo before", "tremolo after", "tremolo both", "bend 0.25", "bend 0.5",
            "bend 1.25", "bend 2.5", "bend 3", "bend beyond next", "adjacent grace",
            "arpeggio", "handshape", "anchor", "tone", "ghost grip", "ghost fingers", "phrase", "moveR" };
        foreach (var bpm in new[] { 100, 200 })
        {
            foreach (var variant in variants) yield return ($"{bpm}/1200/{variant}", ExtendedProject(bpm, variant));
        }
    }

    private static ChartProject ExtendedProject(int bpm, string variant)
    {
        var project = EofGraceTechniqueRoundTripTests.Project(bpm, 1200, "plain");
        var arrangement = project.Arrangements[0];
        var level = arrangement.Levels[0];
        var note = level.Notes[0];
        if (variant.StartsWith("tremolo", StringComparison.Ordinal))
        {
            note.Tremolo = true;
            if (variant is "tremolo before" or "tremolo both") level.Notes.Insert(0, Sound(project, 0, 300, tremolo: true));
            if (variant is "tremolo after" or "tremolo both") level.Notes.Add(Sound(project, 2400, 3000, tremolo: true));
        }
        else if (variant.StartsWith("bend ", StringComparison.Ordinal))
        {
            var strength = variant switch
            {
                "bend 0.25" => 0.25,
                "bend 0.5" => 0.5,
                "bend 1.25" => 1.25,
                "bend 2.5" => 2.5,
                _ => 3
            };
            note.BendValues.Add(new BendValue(At(project, 600), strength));
            note.BendValues.Add(new BendValue(At(project, 900), Math.Min(0.5, strength)));
            note.BendValues.Add(new BendValue(At(project, 1200), 0));
            if (variant == "bend beyond next") level.Notes.Add(Sound(project, 900, 1500));
        }
        else if (variant == "adjacent grace")
        {
            var next = Sound(project, 900, 1500);
            next.GraceFret = 3;
            level.Notes.Add(next);
        }
        else if (variant is "arpeggio" or "handshape" or "ghost grip" or "ghost fingers")
        {
            var template = new ChordTemplate { Id = 0, Arpeggio = variant == "arpeggio", Frets = { [0] = 7, [1] = 9 } };
            if (variant.StartsWith("ghost", StringComparison.Ordinal)) template.GhostMask = ChordTemplate.GhostBit(1);
            if (variant == "ghost fingers") template.Fingers[1] = 2;
            arrangement.ChordTemplates.Add(template);
            var start = 600 + (int)EofGraceTiming.LengthAt(note.Position, project.Beats) / 2;
            level.HandShapes.Add(new HandShape { Position = At(project, start), EndPosition = At(project, 1800), TemplateId = 0 });
        }
        else if (variant == "anchor")
        {
            level.Anchors.Add(new FretAnchor(At(project, 600), 7));
        }
        else if (variant == "tone")
        {
            arrangement.ToneChanges.Add(new ToneChange { Position = At(project, 600), ToneName = "tone" });
        }
        else if (variant == "phrase")
        {
            arrangement.EventPoints.Add(new EventPoint { Position = At(project, 600), Phrase = "phrase" });
        }
        else if (variant == "moveR")
        {
            level.Notes.Add(Sound(project, 2400, 3000));
            arrangement.EventPoints.Add(new EventPoint { Position = At(project, 0), Phrase = "moveR2" });
        }
        return project;
    }

    private static void AssertExtended(ChartProject project)
    {
        var source = project.Arrangements[0].Levels[0].Notes;
        var notes = Import(EofExporter.Export(project).Bytes).Levels[0].Notes;
        Assert.Equal(source.Count, notes.Count);
        Assert.Equal(source.Count(n => n.GraceFret != null), notes.Count(n => n.GraceFret != null));
        for (var i = 0; i < source.Count; i++)
        {
            Assert.Equal(source[i].GraceFret, notes[i].GraceFret);
            Assert.Equal(source[i].Tremolo, notes[i].Tremolo);
        }
        var original = source.First(n => n.GraceFret == 5);
        var actual = notes.First(n => n.GraceFret == 5);
        Assert.Equal(original.BendValues.Count, actual.BendValues.Count);
        for (var i = 0; i < original.BendValues.Count; i++)
        {
            Assert.Equal(Math.Round(original.BendValues[i].Step * 2) / 2, actual.BendValues[i].Step);
            Assert.InRange(Math.Abs(original.BendValues[i].Position.GetPositionMs(project.Beats) -
                actual.BendValues[i].Position.GetPositionMs(project.Beats)), 0, 1);
        }
    }

    private static GuitarNote Sound(ChartProject project, int start, int end, bool tremolo = false) =>
        new() { Position = At(project, start), Sustain = At(project, end) - At(project, start), Fret = 7, StringIndex = 0, Tremolo = tremolo };

    private static FractionalPosition At(ChartProject project, int ms) => TimelineMath.PositionFromImportedMs(ms, project.Beats);

    private static Arrangement Import(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "dc-grace-kind-" + Guid.NewGuid().ToString("N") + ".eof");
        File.WriteAllBytes(path, bytes);
        try
        {
            return Assert.Single(new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
