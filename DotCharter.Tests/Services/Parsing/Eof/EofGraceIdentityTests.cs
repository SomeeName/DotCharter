using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofGraceIdentityTests
{
    [Fact]
    public void Exported_chord_names_contain_only_names_authored_in_the_chart()
    {
        var project = Project([0, 600, 1200, 1800, 2400, 3000]);
        AddSounds(project, new FractionalPosition(1), 0, chord: true);
        var arrangement = project.Arrangements[0];
        var template = new ChordTemplate { Id = 1, Name = "Authored chord" };
        template.Frets[0] = 7;
        template.Frets[1] = 9;
        arrangement.ChordTemplates.Add(template);
        arrangement.Levels[0].Chords[0].TemplateId = template.Id;
        var exported = EofNoteBuilder.Build(arrangement, project.Beats);
        Assert.Equal(2, exported.Count);
        Assert.Empty(exported[0].Raw.Name);
        Assert.Equal(template.Name, exported[1].Raw.Name);
        template.Name = "[DotCharter grace]";
        Assert.Empty(EofNoteBuilder.Build(arrangement, project.Beats)[1].Raw.Name);
        template.Name = "Authored chord";

        var ghostProject = Project([0, 600, 1200, 1800, 2400, 3000]);
        AddSounds(ghostProject, new FractionalPosition(1), 0, chord: false);
        var ghostArrangement = ghostProject.Arrangements[0];
        ghostArrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Frets = { [0] = 5, [1] = 7 }, GhostMask = ChordTemplate.GhostBit(1)
        });
        ghostArrangement.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(2), TemplateId = 0
        });
        foreach (var candidate in new[] { project, ghostProject })
        {
            var source = candidate.Arrangements[0];
            var authoredNames = source.ChordTemplates.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var note in EofNoteBuilder.Build(source, candidate.Beats))
            {
                // Build has already attached handshape ghosts. Inspect the final wire record.
                var writer = new EofPackWriter();
                EofProGuitarNoteWriter.Write(writer, note.Raw);
                var raw = EofProGuitarNoteReader.Read(new EofPackReader(writer.ToArray()));
                if ((raw.NoteBitmask & (raw.NoteBitmask - 1)) == 0) continue;
                Assert.True(raw.Name.Length == 0 || authoredNames.Contains(raw.Name), $"Unrequested manual chord name: {raw.Name}");
            }
        }
        var carrier = Assert.Single(EofNoteBuilder.Build(ghostArrangement, ghostProject.Beats), n => n.Raw.GhostBitmask != 0);
        Assert.Equal((600u, (byte)3, (byte)2, (byte)5, (byte)7),
            (carrier.Raw.PositionMs, carrier.Raw.NoteBitmask, carrier.Raw.GhostBitmask, carrier.Raw.Frets[0], carrier.Raw.Frets[1]));
        Assert.Empty(carrier.Raw.Name);
        var back = Assert.Single(RoundTrip(ghostProject).Arrangements);
        Assert.Equal(2, back.Levels[0].Notes.Count);
        Assert.All(back.Levels[0].Notes, n => Assert.Null(n.GraceFret));
        var shape = Assert.Single(back.Levels[0].HandShapes);
        var grip = back.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(new Dictionary<int, int> { [0] = 5, [1] = 7 }, grip.Frets);
        Assert.Equal(ChordTemplate.GhostBit(1), grip.GhostMask);
    }

    [Fact]
    public void Played_sixteenth_slide_at_tempo_change_keeps_both_notes()
    {
        var project = Project([0, 1192, 1792, 2392]);
        AddSounds(project, new FractionalPosition(1), 4, chord: false);

        var imported = RoundTrip(project);
        var notes = Assert.Single(Assert.Single(imported.Arrangements).Levels).Notes;
        Assert.Equal(2, notes.Count);
        Assert.Equal(1192, notes[0].Position.GetPositionMs(project.Beats));
        Assert.Equal(1342, notes[1].Position.GetPositionMs(project.Beats));
        Assert.Equal(1342, (notes[0].Position + notes[0].Sustain).GetPositionMs(project.Beats));
        Assert.Equal(7, notes[0].SlideTo);
        Assert.All(notes, note => Assert.Null(note.GraceFret));
    }

    [Fact]
    public void Grace_across_abrupt_tempo_change_survives_two_milliseconds_of_rounding()
    {
        var project = Project([0, 1600, 1700, 1800, 1900]);
        var position = new FractionalPosition(1, new Fraction(3, 2000));
        AddSounds(project, position, 0, chord: false);

        AssertRoundTrip(project, position, 0, chord: false);
    }

    [Fact]
    public void Fixed_tempo_maps_preserve_graces_and_played_sixteenth_and_thirty_second_slides()
    {
        var maps = new double[][]
        {
            [0, 600, 1200, 1800, 2400, 3000],
            [0, 1192, 1792, 2392, 2992, 3592],
            [0, 1600, 1700, 1800, 1900, 2000],
            [0, 1536, 2688, 3840, 4992, 6144]
        };
        var positions = new[]
        {
            new FractionalPosition(0, new Fraction(1999, 2000)),
            new FractionalPosition(1, new Fraction(3, 2000)),
            new FractionalPosition(1, new Fraction(9, 256))
        };
        foreach (var map in maps)
        {
            foreach (var position in positions)
            {
                foreach (var chord in new[] { false, true })
                {
                    foreach (var subdivision in new[] { 0, 4, 8 })
                    {
                        var project = Project(map);
                        AddSounds(project, position, subdivision, chord);
                        AssertRoundTrip(project, position, subdivision, chord);
                    }
                }
            }
        }

        // Only single-note ornaments carry the editor-visible identity.
        // A chord name would leak into EOF's Rocksmith chord templates.
        foreach (var chord in new[] { false, true })
        {
            var project = Project(maps[0]);
            AddSounds(project, positions[0], 0, chord);
            var raw = EofNoteBuilder.Build(project.Arrangements[0], project.Beats)[0].Raw;
            var writer = new EofPackWriter();
            EofProGuitarNoteWriter.Write(writer, raw);
            var reader = new EofPackReader(writer.ToArray());
            var restored = EofProGuitarNoteReader.Read(reader);
            Assert.Equal(chord ? "" : "[DotCharter grace]", restored.Name);
            Assert.Equal(raw.PositionMs, restored.PositionMs);
            Assert.Equal(raw.LengthMs, restored.LengthMs);
            Assert.True(reader.Eof);
        }
    }

    private static ChartProject Project(double[] times)
    {
        var project = new ChartProject();
        foreach (var time in times) project.Beats.Add(new BeatMarker { Time = time });
        project.Arrangements.Add(new Arrangement { Name = "Lead", Levels = { new ChartLevel() } });
        return project;
    }

    private static void AddSounds(ChartProject project, FractionalPosition position, int subdivision, bool chord)
    {
        var level = project.Arrangements[0].Levels[0];
        var duration = subdivision == 0 ? new FractionalPosition(2) : new FractionalPosition(0, new Fraction(1, subdivision));
        var first = new GuitarNote
        {
            Position = position, Sustain = duration, StringIndex = 0,
            Fret = subdivision == 0 ? 7 : 5, GraceFret = subdivision == 0 ? 5 : null,
            SlideTo = subdivision == 0 ? null : 7, LinkNext = subdivision != 0
        };
        if (chord) AddChord(level, first);
        else level.Notes.Add(first);
        if (subdivision == 0) return;
        var target = new GuitarNote { Position = position + duration, Sustain = new FractionalPosition(1), StringIndex = 0, Fret = 7 };
        if (chord) AddChord(level, target);
        else level.Notes.Add(target);
    }

    private static void AddChord(ChartLevel level, GuitarNote note)
    {
        var chord = new Chord { Position = note.Position };
        for (var s = 0; s < 2; s++)
        {
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = s, Fret = note.Fret + s * 2, Sustain = note.Sustain,
                GraceFret = note.GraceFret + s * 2, SlideTo = note.SlideTo + s * 2, LinkNext = note.LinkNext
            });
        }
        level.Chords.Add(chord);
    }

    private static EofImportResult RoundTrip(ChartProject project)
    {
        var path = EofExporterTests.WriteTempEof(project);
        try
        {
            return new EofImporter().ImportTracksWithMetadata(path, [9]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertRoundTrip(ChartProject project, FractionalPosition position, int subdivision, bool chord)
    {
        var imported = RoundTrip(project);
        var beats = imported.Metadata.Beats.Select(b => new BeatMarker { Time = b.TimeMs }).ToList();
        var level = Assert.Single(Assert.Single(imported.Arrangements).Levels);
        if (chord && subdivision == 0)
        {
            Assert.Equal(2, level.Chords.Count);
            var ornament = level.Chords[0];
            var target = level.Chords[1];
            Assert.Equal(new[] { 5, 7 }, ornament.ChordNotes.Select(n => n.Fret));
            Assert.Equal(new[] { 7, 9 }, target.ChordNotes.Select(n => n.Fret));
            Assert.All(level.Chords.SelectMany(c => c.ChordNotes), n => Assert.Null(n.GraceFret));
            var onset = ornament.Position.GetPositionMs(beats);
            Assert.InRange(Math.Abs(onset - position.GetPositionMs(project.Beats)), 0, 1);
            var targetMs = Math.Round(position.GetPositionMs(project.Beats)) + EofGraceTiming.LengthAt(position, project.Beats);
            Assert.Equal(targetMs, Math.Round(target.Position.GetPositionMs(beats)));
            foreach (var note in target.ChordNotes)
            {
                Assert.InRange(Math.Abs((target.Position + note.Sustain).GetPositionMs(beats) -
                    (position + new FractionalPosition(2)).GetPositionMs(project.Beats)), 0, 1);
            }
            return;
        }
        var notes = chord ? level.Chords.SelectMany(c => c.ChordNotes.Select(n => (c.Position, Note: (IGuitarSound)n, n.GraceFret))).ToList()
            : level.Notes.Select(n => (n.Position, Note: (IGuitarSound)n, n.GraceFret)).ToList();
        var strings = chord ? 2 : 1;
        Assert.Equal(strings * (subdivision == 0 ? 1 : 2), notes.Count);
        foreach (var (actualPosition, note, graceFret) in notes)
        {
            if (subdivision == 0)
            {
                Assert.Equal(5 + note.StringIndex * 2, graceFret);
                Assert.Equal(7 + note.StringIndex * 2, note.Fret);
                Assert.InRange(Math.Abs(actualPosition.GetPositionMs(beats) - position.GetPositionMs(project.Beats)), 0, 1);
                Assert.InRange(Math.Abs((actualPosition + note.Sustain).GetPositionMs(beats) - (position + new FractionalPosition(2)).GetPositionMs(project.Beats)), 0, 1);
            }
            else
            {
                Assert.Null(graceFret);
                var start = note.Fret == 5 + note.StringIndex * 2 ? position : position + new FractionalPosition(0, new Fraction(1, subdivision));
                Assert.InRange(Math.Abs(actualPosition.GetPositionMs(beats) - start.GetPositionMs(project.Beats)), 0, 1);
            }
        }
    }
}
