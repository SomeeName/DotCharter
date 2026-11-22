using System.Text.Json;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofGeneratedGraceAttachmentsTests
{
    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 300)];

    [Fact]
    public void The_seeded_corpus_draws_every_exportable_attachment_family()
    {
        var charts = Enumerable.Range(0, 300).Select(EofGraceEmissionCases.Create).ToList();
        var records = charts.SelectMany(p => EofNoteBuilder.Build(p.Arrangements[0], p.Beats)).ToList();
        var tech = records.SelectMany(n => n.BendTechNotes).ToList();
        Assert.Contains(tech, n => (n.Flags & EofConstants.PgFlagBend) != 0);
        Assert.Contains(tech, n => (n.Flags & EofConstants.PgFlagSlideUp) != 0);
        Assert.Contains(records, n => n.Raw.GhostBitmask != 0);
        Assert.Contains(records, n => n.Fingers.Count != 0);
        Assert.Contains(records, n => (n.Raw.ExtendedFlags & EofConstants.PgEFlagFingerless) != 0);
        Assert.Contains(records, n => (n.Raw.Flags & EofConstants.PgFlagUnpitchedSlide) != 0);
        Assert.Contains(records, n => (n.Raw.Flags & EofConstants.PgFlagLinkNext) != 0);
        Assert.Contains(records, n => n.Raw.Name.EndsWith("-nop", StringComparison.Ordinal));
        Assert.All(charts, p => Assert.Equal(2, p.Arrangements[0].Levels.Count));
        Assert.Equal(new[] { 0, 2 }, charts.Select(p => p.Arrangements[0].Capo).Distinct().Order());
        Assert.Equal(new[] { 6, 7, 8 }, charts.Select(p => p.Arrangements[0].StringCount).Distinct().Order());
        Assert.Contains(charts, p => p.Arrangements[0].Levels[0].Chords.Count == 2);
        Assert.Contains(charts, p => p.Arrangements[0].Levels[0].Chords[0].ChordNotes[0].StringIndex != 0);
        Assert.Contains(charts.SelectMany(p => p.Arrangements[0].ChordTemplates), t => t.Arpeggio);
        Assert.Contains(charts.SelectMany(p => p.Arrangements[0].ChordTemplates), t => !t.Arpeggio);
        Assert.Contains(charts, p => p.Arrangements[0].PhraseIterations.Count != 0);
        Assert.Contains(charts, p => EofMoverPhrases.PlanExport(p.Arrangements[0], p.Beats.Count).Count != 0);
    }

    [Fact]
    public void The_B5_mixed_slide_keeps_the_chord_and_both_slide_targets()
    {
        var project = EofGraceChordCases.NamedProject();
        var chord = project.Arrangements[0].Levels[0].Chords[0];
        chord.ChordNotes[0].GraceFret = null;
        chord.ChordNotes[0].SlideTo = 9;
        chord.ChordNotes[1].GraceFret = 7;
        chord.ChordNotes[1].SlideTo = 12;
        var back = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);
        Assert.Empty(back.Levels[0].Notes);
        var target = Assert.Single(back.Levels[0].Chords);
        Assert.Equal(new int?[] { 9, 12 }, target.ChordNotes.Select(n => n.SlideTo));
        Assert.Equal(new int?[] { null, 7 }, target.ChordNotes.Select(n => n.GraceFret));
        Assert.Equal(600, target.Position.GetPositionMs(project.Beats));
        Assert.All(target.ChordNotes, n => Assert.Equal(1800, (target.Position + n.Sustain).GetPositionMs(project.Beats)));
        Assert.Equal("B5", back.ChordTemplates.Single(t => t.Id == target.TemplateId).Name);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Every_seed_keeps_the_complete_authored_notes_chords_names_techniques_and_times(int seed)
    {
        var project = EofGraceEmissionCases.Create(seed);
        var back = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);
        var issues = EofMusicComparison.Issues(project, back);
        Assert.True(issues.Count == 0, $"seed {seed}: {string.Join(", ", issues)}");
    }

    [Fact]
    public void Zero_sustain_graces_import_without_the_ornament_length()
    {
        var project = EofGraceTechniqueRoundTripTests.Project(100, 0, "plain");
        var note = Assert.Single(EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes).Levels[0].Notes);
        Assert.Equal(5, note.GraceFret);
        Assert.Equal(0, note.EndPosition.GetPositionMs(project.Beats) - note.Position.GetPositionMs(project.Beats));
    }

    [Fact]
    public void Interleaved_generated_chord_overrides_belong_to_their_own_targets()
    {
        var project = EofGraceChordCases.NamedProject();
        var level = project.Arrangements[0].Levels[0];
        var first = level.Chords[0];
        first.ChordNotes[0].GraceFret = null;
        first.ChordNotes[0].SlideTo = 9;
        first.ChordNotes[1].GraceFret = 7;
        first.ChordNotes[1].SlideTo = 12;
        foreach (var tone in first.ChordNotes) tone.VibratoStrength = 0;
        var second = first.Clone();
        second.Position = EofGraceNeighbourCases.At(project, 650);
        level.Chords.Add(second);
        var back = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);
        var issues = EofMusicComparison.Issues(project, back);
        Assert.True(issues.Count == 0, string.Join(", ", issues));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("moved")]
    [InlineData("duration")]
    [InlineData("frets")]
    [InlineData("flags")]
    public void Only_overrides_the_exporter_can_emit_are_recognised(string edit)
    {
        var project = EofGraceChordCases.NamedProject();
        var chord = project.Arrangements[0].Levels[0].Chords[0];
        chord.ChordNotes[0].GraceFret = null;
        chord.ChordNotes[0].SlideTo = 9;
        chord.ChordNotes[1].GraceFret = 7;
        chord.ChordNotes[1].SlideTo = 12;
        var built = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
        var input = new EofGraceGuardCases.Input();
        input.Notes.Clear();
        input.Notes.AddRange(built.Select(n => n.Raw));
        var tech = Assert.Single(built.SelectMany(n => n.BendTechNotes));
        if (edit == "moved") tech.PositionMs++;
        if (edit == "duration") tech.LengthMs++;
        if (edit == "frets") tech.Frets[1] = 7;
        if (edit == "flags") tech.Flags |= EofConstants.PgFlagVibrato;
        input.Tech.Add(tech);
        var back = EofGraceGuardCases.Import(EofGraceGuardCases.Track(input), project.Beats);
        Assert.Equal(edit == "original", EofGraceGuardCases.HasGrace(back));
        Assert.Equal(edit == "original" ? 0 : 1, back.Levels[0].Notes.Count);
    }

    internal static void CompareWithMain(Func<byte[], Arrangement> main, string output)
    {
        var rows = Enumerable.Range(0, 300).Select(seed =>
        {
            var project = EofGraceEmissionCases.Create(seed);
            var bytes = EofExporter.Export(project).Bytes;
            return new { seed, branchIssues = EofMusicComparison.Issues(project, EofGraceNeighbourRoundTripTests.Import(bytes)), mainIssues = EofMusicComparison.Issues(project, main(bytes)) };
        });
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        var zero = EofGraceTechniqueRoundTripTests.Project(100, 0, "plain");
        var zeroBytes = EofExporter.Export(zero).Bytes;
        var branchZero = EofGraceNeighbourRoundTripTests.Import(zeroBytes).Levels[0].Notes.Single();
        var mainZero = main(zeroBytes).Levels[0].Notes.Single();
        File.WriteAllText(output + ".zero.json", JsonSerializer.Serialize(new
        {
            authored = 0,
            branch = branchZero.EndPosition.GetPositionMs(zero.Beats) - branchZero.Position.GetPositionMs(zero.Beats),
            main = mainZero.EndPosition.GetPositionMs(zero.Beats) - mainZero.Position.GetPositionMs(zero.Beats)
        }));
    }
}
