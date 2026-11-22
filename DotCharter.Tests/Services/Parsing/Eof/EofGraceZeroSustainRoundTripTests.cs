using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     #643: the EOF writer displaces a graced target by the ornament and the reader folds
///     the pair back. A target that had no sustain must come back with none, whatever the
///     ornament's length at that tempo, for a single note and for a chord.
/// </summary>
public sealed class EofGraceZeroSustainRoundTripTests
{
    // The ornament is a thirty-second at the local tempo: 75 ms at 100 BPM, 38 ms at
    // 200 BPM and 187 ms at 40 BPM, so the three rows fold three different lengths.
    [Theory]
    [InlineData(100, 75)]
    [InlineData(200, 38)]
    [InlineData(40, 187)]
    public void A_zero_sustain_note_with_a_grace_keeps_sustain_zero(int bpm, int ornamentMs)
    {
        var project = EofGraceTechniqueRoundTripTests.Project(bpm, 0, "plain");
        var authored = project.Arrangements[0].Levels[0].Notes[0];
        Assert.Equal((uint)ornamentMs, EofGraceTiming.LengthAt(authored.Position, project.Beats));

        var back = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);

        var note = Assert.Single(back.Levels.SelectMany(level => level.Notes));
        Assert.Equal(600, Ms(project, note.Position));
        Assert.Equal(7, note.Fret);
        Assert.Equal(5, note.GraceFret);
        Assert.Equal(FractionalPosition.Zero, note.Sustain);
        Assert.Equal(600, Ms(project, note.EndPosition));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(40)]
    public void A_zero_sustain_chord_with_one_graced_tone_keeps_sustain_zero(int bpm)
    {
        var project = ZeroSustainChord(bpm);

        var back = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);

        Assert.Empty(back.Levels.SelectMany(level => level.Notes));
        var chord = Assert.Single(back.Levels.SelectMany(level => level.Chords));
        Assert.Equal(600, Ms(project, chord.Position));
        Assert.Equal([7, 9], chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
        Assert.Equal([5, null], chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.GraceFret));
        Assert.All(chord.ChordNotes, n => Assert.Equal(FractionalPosition.Zero, n.Sustain));
        Assert.Equal(600, Ms(project, chord.EndPosition));
    }

    // At 100 BPM the ornament is 75 ms. The writer cannot say "shorter than the ornament"
    // (the target's wire length cannot be negative) and zero means "no sustain", so every
    // sustain from 1 ms to one millisecond past the ornament writes one millisecond, which
    // reads back as exactly the ornament: a sustain EQUAL to the ornament, the one on the
    // beat grid, is the identity; 1 ms, 74 ms and 76 ms come back as 75 ms.
    [Theory]
    [InlineData(1, 675)]
    [InlineData(50, 675)]
    [InlineData(74, 675)]
    [InlineData(75, 675)]
    [InlineData(76, 675)]
    public void A_sustain_up_to_one_past_the_ornament_comes_back_as_the_ornament(int sustainMs, int endMs)
    {
        var project = EofGraceTechniqueRoundTripTests.Project(100, sustainMs, "plain");

        var raw = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
        var back = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);

        Assert.Equal([(600u, 75u), (675u, 1u)], raw.Select(n => (n.Raw.PositionMs, n.Raw.LengthMs)));
        var note = Assert.Single(back.Levels.SelectMany(level => level.Notes));
        Assert.Equal(5, note.GraceFret);
        Assert.Equal(600, Ms(project, note.Position));
        Assert.Equal(endMs, Ms(project, note.EndPosition));
    }

    [Theory]
    [InlineData(77)]
    [InlineData(150)]
    [InlineData(1000)]
    public void A_sustain_longer_than_the_ornament_plus_one_round_trips_to_the_millisecond(int sustainMs)
    {
        var project = EofGraceTechniqueRoundTripTests.Project(100, sustainMs, "plain");

        var raw = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
        var back = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);

        Assert.Equal([(600u, 75u), (675u, (uint)sustainMs - 75u)], raw.Select(n => (n.Raw.PositionMs, n.Raw.LengthMs)));
        var note = Assert.Single(back.Levels.SelectMany(level => level.Notes));
        Assert.Equal(600 + sustainMs, Ms(project, note.EndPosition));
    }

    // The file EOF itself reads: a target without sustain is written with length zero, not
    // the one millisecond a sustained target keeps. The round trips above cannot see this
    // half alone (a writer and a reader changed together would still round-trip).
    [Fact]
    public void The_writer_keeps_a_zero_sustain_target_at_wire_length_zero()
    {
        var single = EofGraceTechniqueRoundTripTests.Project(100, 0, "plain");
        var chord = ZeroSustainChord(100);

        foreach (var project in new[] { single, chord })
        {
            var raw = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
            Assert.Equal([(600u, 75u), (675u, 0u)], raw.Select(n => (n.Raw.PositionMs, n.Raw.LengthMs)));
        }
    }

    private static int Ms(ChartProject project, FractionalPosition position) =>
        TimelineMath.ToWholeMs(position.GetPositionMs(project.Beats));

    private static ChartProject ZeroSustainChord(int bpm)
    {
        var project = EofGraceTechniqueRoundTripTests.Project(bpm, 0, "plain");
        var arrangement = project.Arrangements[0];
        arrangement.Levels[0].Notes.Clear();
        var template = new ChordTemplate { Id = 0, Name = "" };
        template.Frets[0] = 7;
        template.Frets[1] = 9;
        arrangement.ChordTemplates.Add(template);
        arrangement.Levels[0].Chords.Add(new Chord
        {
            Position = TimelineMath.PositionFromImportedMs(600, project.Beats),
            TemplateId = 0,
            ChordNotes =
            {
                new ChordNote { StringIndex = 0, Fret = 7, GraceFret = 5 },
                new ChordNote { StringIndex = 1, Fret = 9 }
            }
        });
        return project;
    }
}
