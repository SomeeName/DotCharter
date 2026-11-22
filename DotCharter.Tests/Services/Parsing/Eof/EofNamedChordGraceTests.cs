using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofNamedChordGraceTests
{
    [Theory]
    [InlineData("B5", false)]
    [InlineData("Am7", false)]
    [InlineData("D/F#", false)]
    [InlineData("Open fifth", false)]
    [InlineData("123456789012345678901234567890", false)]
    [InlineData("B5", true)]
    public void A_named_chord_keeps_every_tone_name_grace_onset_and_sustain(string name, bool nop)
    {
        var project = EofGraceChordCases.NamedProject(name, nop);
        var built = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
        Assert.Equal(new uint[] { 600, 675 }, built.Select(n => n.Raw.PositionMs));
        Assert.Equal(name + (nop ? "-nop" : ""), built[1].Raw.Name);
        var imported = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);
        var issues = EofGraceChordCases.Issues(project, imported, mainParser: false);
        Assert.True(issues.Count == 0, string.Join("; ", issues));
        Assert.Empty(imported.Levels[0].Notes);
        var chord = Assert.Single(imported.Levels[0].Chords);
        Assert.Equal(new[] { 7, 9 }, chord.ChordNotes.Select(n => n.Fret));
        Assert.Equal(new int?[] { 5, null }, chord.ChordNotes.Select(n => n.GraceFret));
        Assert.InRange(Math.Abs(chord.Position.GetPositionMs(project.Beats) - 600), 0, 1);
        Assert.All(chord.ChordNotes, n => Assert.InRange(Math.Abs(
            (chord.Position + n.Sustain).GetPositionMs(project.Beats) - chord.Position.GetPositionMs(project.Beats) - 1200), 0, 1));
    }

    [Fact]
    public void Seeded_chord_cases_cover_names_tone_counts_and_grace_locations()
    {
        var chords = Enumerable.Range(0, 300).Select(seed => EofGraceNeighbourCases.Create(seed).Arrangements[0])
            .Select(a => (Arrangement: a, Chord: a.Levels[0].Chords.Single(c => c.ChordNotes.Count(n => n.GraceFret != null) == 1))).ToList();
        var names = chords.Select(c => c.Arrangement.ChordTemplates.Single(t => t.Id == c.Chord.TemplateId).Name).ToList();
        Assert.Equal(150, names.Count(n => n.Length != 0));
        Assert.Equal(EofGraceChordCases.Names.Order(), names.Where(n => n.Length != 0).Distinct().Order());
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, chords.Select(c => c.Chord.ChordNotes.Count).Distinct().Order());
        Assert.Contains(chords, c => c.Chord.ChordNotes.First().GraceFret != null);
        Assert.Contains(chords, c => c.Chord.ChordNotes.Last().GraceFret != null);
        Assert.Contains(chords, c => c.Chord.ChordNotes.Skip(1).SkipLast(1).Any(n => n.GraceFret != null));
    }

    [Fact]
    public void Two_graced_strings_stay_an_unnamed_played_chord_ornament_and_a_target()
    {
        var project = EofGraceNeighbourCases.Create(0);
        var built = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
        var ornament = Assert.Single(built, n => n.Raw.PositionMs == 21000);
        Assert.Empty(ornament.Raw.Name);
        Assert.Equal(2, System.Numerics.BitOperations.PopCount(ornament.Raw.NoteBitmask));
        var imported = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);
        var issues = EofGraceChordCases.Issues(project, imported, mainParser: false);
        Assert.True(issues.Count == 0, string.Join("; ", issues));
    }
}
