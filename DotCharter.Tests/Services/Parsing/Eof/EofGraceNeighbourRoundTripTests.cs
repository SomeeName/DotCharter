using System.Text.Json;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofGraceNeighbourRoundTripTests
{
    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 300)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Three_hundred_seeded_charts_preserve_graces_without_inventing_other_pairs(int seed)
    {
        var project = EofGraceNeighbourCases.Create(seed);
        Assert.NotEmpty(EofGraceNeighbourCases.Eligible(project));
        var issues = EofGraceNeighbourCases.Issues(project, Import(EofExporter.Export(project).Bytes));
        Assert.True(issues.Count == 0, $"seed {seed}: {string.Join("; ", issues)}");
    }

    [Fact]
    public void The_seeded_inputs_include_a_preceding_chords_override_on_a_grace_string()
    {
        var cases = Enumerable.Range(0, 300).Select(EofGraceNeighbourCases.Create);
        var covered = cases.Any(project =>
        {
            var tech = EofNoteBuilder.Build(project.Arrangements[0], project.Beats).SelectMany(n => n.BendTechNotes).ToList();
            return EofGraceNeighbourCases.Eligible(project).Any(grace => tech.Any(t =>
                (t.Flags & (EofConstants.PgFlagSlideUp | EofConstants.PgFlagSlideDown)) != 0 &&
                (t.NoteBitmask & (1 << grace.StringIndex)) != 0 &&
                t.PositionMs + 1 == EofGraceNeighbourCases.Ms(project, grace.Position)));
        });
        Assert.True(covered);
    }

    [Fact]
    public void A_preceding_chords_one_ms_slide_tech_does_not_apply_to_the_grace()
    {
        var project = EofGraceTechniqueRoundTripTests.Project(100, 1200, "plain");
        project.Arrangements[0].Levels[0].Notes[0].StringIndex = 1;
        var chord = new Chord { Position = EofGraceNeighbourCases.At(project, 599) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, SlideTo = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, SlideTo = 8 });
        project.Arrangements[0].Levels[0].Chords.Add(chord);
        var issues = EofGraceNeighbourCases.Issues(project, Import(EofExporter.Export(project).Bytes));
        Assert.True(issues.Count == 0, string.Join("; ", issues));
    }

    [Fact]
    public void A_trill_between_both_attacks_does_not_apply_to_either_sound()
    {
        var project = EofGraceTechniqueRoundTripTests.Project(100, 1200, "plain");
        var built = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
        var writer = new EofPackWriter();
        writer.WriteU8(24);
        writer.WriteU8(6);
        for (var i = 0; i < 6; i++) writer.WriteU8(0);
        writer.WriteU32((uint)built.Count);
        foreach (var note in built) EofProGuitarNoteWriter.Write(writer, note.Raw);
        writer.WriteU16(1);
        writer.WriteU16(EofConstants.SectionTrill);
        writer.WriteU32(1);
        writer.WriteString("");
        writer.WriteU8(0);
        writer.WriteU32(610);
        writer.WriteU32(620);
        writer.WriteU32(0);
        writer.WriteU32(0);
        var imported = EofProGuitarTrackParser.Parse(new EofPackReader(writer.ToArray()),
            new EofTrackInfo { Id = 9, Name = "Lead" }, project.Beats);
        Assert.Equal(5, Assert.Single(imported.Levels[0].Notes).GraceFret);
    }

    internal static void CompareWithMain(Func<byte[], Arrangement> importMain, string output)
    {
        var rows = Enumerable.Range(0, 300).Select(seed =>
        {
            var project = EofGraceNeighbourCases.Create(seed);
            var bytes = EofExporter.Export(project).Bytes;
            var main = importMain(bytes);
            return new
            {
                seed,
                eligible = EofGraceExtendedCases.Rows(project.Arrangements[0]).Count(r => EofGraceExtendedCases.PreservesEnd(project, r)),
                mainIssues = EofGraceNeighbourCases.Issues(project, main, checkInvented: false, mainParser: true),
                branchIssues = EofGraceNeighbourCases.Issues(project, Import(bytes)),
                zeroSustain = project.Arrangements[0].Levels[0].Notes.Count(n => n.GraceFret != null && n.Sustain == FractionalPosition.Zero)
            };
        }).ToList();
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static Arrangement Import(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "dc-eof-neighbours-" + Guid.NewGuid().ToString("N") + ".eof");
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
