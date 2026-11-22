using System.Text;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public class EofExporterTests
{
    internal static string WriteTempEof(ChartProject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-export-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, EofExporter.Export(project).Bytes);
        return path;
    }

    [Fact]
    public void Grace_at_a_tempo_boundary_survives_millisecond_quantization()
    {
        var project = new ChartProject();
        foreach (var time in new[] { 0.0, 1536.0, 2688.0 })
            project.Beats.Add(new BeatMarker { Time = time });
        var position = new FractionalPosition(1, new Fraction(9, 256));
        var original = new GuitarNote
        {
            Position = position, Sustain = new FractionalPosition(0, new Fraction(1, 4)),
            StringIndex = 0, Fret = 7, GraceFret = 5
        };
        project.Arrangements.Add(new Arrangement { Name = "Lead", Levels = { new ChartLevel { Notes = { original } } } });
        var path = WriteTempEof(project);
        try
        {
            var imported = new EofImporter().ImportTracksWithMetadata(path, [9]);
            var note = Assert.Single(Assert.Single(Assert.Single(imported.Arrangements).Levels).Notes);
            var beats = imported.Metadata.Beats.Select(b => new BeatMarker { Time = b.TimeMs }).ToList();

            Assert.Equal(5, note.GraceFret);
            Assert.Equal(7, note.Fret);
            Assert.InRange(Math.Abs(note.Position.GetPositionMs(beats) - position.GetPositionMs(project.Beats)), 0, 1);
            Assert.InRange(Math.Abs((note.Position + note.Sustain).GetPositionMs(beats) - (position + original.Sustain).GetPositionMs(project.Beats)), 0, 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(30)]
    [InlineData(120)]
    public void GracePairs_RoundTripAtSlowAndNormalTempos(int bpm)
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        project.Arrangements.Clear();
        for (var i = 0; i < project.Beats.Count; i++)
        {
            project.Beats[i].Time = i * 60000.0 / bpm;
            project.Beats[i].Bpm = bpm;
        }

        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), Sustain = new FractionalPosition(1),
            StringIndex = 0, Fret = 7, GraceFret = 5
        });
        var chord = new Chord { Position = new FractionalPosition(4) };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 7, GraceFret = 5, Sustain = new FractionalPosition(1)
        });
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 1, Fret = 9, GraceFret = 7, Sustain = new FractionalPosition(1)
        });
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        var path = WriteTempEof(project);
        try
        {
            var imported = Assert.Single(new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements);
            var importedLevel = Assert.Single(imported.Levels);
            var note = Assert.Single(importedLevel.Notes);
            Assert.Equal(5, note.GraceFret);
            Assert.Equal(new FractionalPosition(2), note.Position);
            Assert.Equal(new FractionalPosition(1), note.Sustain);
            Assert.Equal(2, importedLevel.Chords.Count);
            var ornament = importedLevel.Chords[0];
            var target = importedLevel.Chords[1];
            Assert.Equal(new FractionalPosition(4), ornament.Position);
            Assert.Equal(new[] { 5, 7 }, ornament.ChordNotes.Select(n => n.Fret));
            Assert.Equal(new[] { 7, 9 }, target.ChordNotes.Select(n => n.Fret));
            Assert.All(importedLevel.Chords.SelectMany(c => c.ChordNotes), n => Assert.Null(n.GraceFret));
            Assert.Equal(Math.Round(ornament.Position.GetPositionMs(project.Beats)) +
                EofGraceTiming.LengthAt(new FractionalPosition(4), project.Beats), Math.Round(target.Position.GetPositionMs(project.Beats)));
            Assert.All(target.ChordNotes, n => Assert.Equal(5 * 60000.0 / bpm,
                (target.Position + n.Sustain).GetPositionMs(project.Beats), 6));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ordinary_sixteenth_slide_at_100_bpm_stays_two_played_notes()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        project.Arrangements.Clear();
        for (var i = 0; i < project.Beats.Count; i++)
        {
            project.Beats[i].Time = i * 600.0;
            project.Beats[i].Bpm = 100;
        }
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel
        {
            Notes =
            {
                new GuitarNote { Position = new FractionalPosition(1), Sustain = new FractionalPosition(0, new Fraction(1, 4)), StringIndex = 0, Fret = 5, SlideTo = 7, LinkNext = true },
                new GuitarNote { Position = new FractionalPosition(1, new Fraction(1, 4)), Sustain = new FractionalPosition(0, new Fraction(1, 4)), StringIndex = 0, Fret = 7 }
            }
        });
        project.Arrangements.Add(arrangement);
        var path = WriteTempEof(project);
        try
        {
            var imported = new EofImporter().ImportTracksWithMetadata(path, [9]);
            var notes = Assert.Single(Assert.Single(imported.Arrangements).Levels).Notes;
            Assert.Equal(2, notes.Count);
            Assert.Equal(600, notes[0].Position.GetPositionMs(project.Beats));
            Assert.Equal(750, (notes[0].Position + notes[0].Sustain).GetPositionMs(project.Beats));
            Assert.Equal(750, notes[1].Position.GetPositionMs(project.Beats));
            Assert.Equal(7, notes[0].SlideTo);
            Assert.All(notes, note => Assert.Null(note.GraceFret));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FoldingGracePairs_KeepsEachSurvivingChordsOwnFingering()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        project.Arrangements.Clear();
        var arrangement = new Arrangement { Name = "Lead", Capo = 2 };
        for (var i = 0; i < 3; i++)
        {
            var template = new ChordTemplate { Id = i };
            template.Frets[0] = 7;
            template.Frets[1] = 9;
            template.Fingers[0] = i == 0 ? 0 : i;
            template.Fingers[1] = i + 2;
            arrangement.ChordTemplates.Add(template);
            var chord = new Chord { Position = new FractionalPosition(2 + i * 2), TemplateId = i };
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = 0, Fret = 7, GraceFret = i == 0 ? 5 : null, Sustain = new FractionalPosition(1)
            });
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = 1, Fret = 9, GraceFret = i == 0 ? 7 : null, Sustain = new FractionalPosition(1)
            });
            if (i != 1) arrangement.Levels.Add(new ChartLevel { DifficultyIndex = i == 0 ? 0 : 1 });
            arrangement.Levels[^1].Chords.Add(chord);
        }

        project.Arrangements.Add(arrangement);
        var path = WriteTempEof(project);
        try
        {
            var imported = Assert.Single(new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements);
            var chords = imported.Levels.SelectMany(l => l.Chords).OrderBy(c => c.Position).ToList();
            Assert.Equal(4, chords.Count);
            var targets = chords.Where(c => c.ChordNotes[0].Fret == 7).ToList();
            Assert.Equal(3, targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var template = imported.ChordTemplates.Single(t => t.Id == targets[i].TemplateId);
                Assert.Equal(arrangement.ChordTemplates[i].Fingers, template.Fingers);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_StartsWithEofProjectHeader()
    {
        var bytes = EofExporter.Export(EofExportTestCharts.BuildRepresentativeProject()).Bytes;
        Assert.Equal("EOFSONH", Encoding.ASCII.GetString(bytes, 0, 7));
        Assert.Equal(0, bytes[7]); // NUL-padded 16-byte header.
    }

    [Fact]
    public void Export_RepresentativeProject_IsLossless()
    {
        var result = EofExporter.Export(EofExportTestCharts.BuildRepresentativeProject());
        Assert.True(result.Report.IsLossless,
            "Expected no conversion notes but got: " +
            string.Join("; ", result.Report.Notes.Select(n => $"{n.Kind} {n.Subject}: {n.Message}")));
    }

    [Fact]
    public void Export_TrackListing_SurfacesAssignedProGuitarSlots()
    {
        var path = WriteTempEof(EofExportTestCharts.BuildRepresentativeProject());
        try
        {
            // The importer must walk EVERY track of the scaffold to list them —
            // this exercises stream alignment across the whole file.
            var tracks = new EofImporter().GetAvailableTracks(path);

            Assert.Equal(3, tracks.Count(t => !t.IsVocals));
            // The fixture's lyrics list PART VOCALS too (#232).
            Assert.Single(tracks, t => t.IsVocals);
            // Bass → PART REAL_BASS (8), Lead → PART REAL_GUITAR (9),
            // Rhythm → PART REAL_GUITAR_22 (12); names carried as alt names.
            Assert.Contains(tracks, t => t is { Id: 8, Name: "Bass" });
            Assert.Contains(tracks, t => t is { Id: 9, Name: "Lead" });
            Assert.Contains(tracks, t => t is { Id: 12, Name: "Rhythm" });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_Metadata_RoundTripsThroughImporter()
    {
        var path = WriteTempEof(EofExportTestCharts.BuildRepresentativeProject());
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, [9]);

            Assert.Equal("Round Trip", result.Metadata.Title);
            Assert.Equal("The Exporters", result.Metadata.Artist);
            Assert.Equal("Parity", result.Metadata.Album);
            Assert.Equal("2026", result.Metadata.Year);
            Assert.Equal("DotCharter", result.Metadata.Charter);
            Assert.Equal("Metal", result.Metadata.Genre);
            Assert.Equal("song.ogg", result.Metadata.AudioFileName);
            Assert.Equal(32, result.Metadata.Beats.Count);
            Assert.All(result.Metadata.Beats, b => Assert.Equal(120.0, b.Bpm, 3));
            // Exact beat times survive via the floating-point timing block.
            Assert.Equal(500.0, result.Metadata.Beats[1].TimeMs, 9);
            Assert.True(result.Metadata.Beats[4].IsMeasureStart);
            Assert.False(result.Metadata.Beats[5].IsMeasureStart);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     EOF's crazy status (EOF_NOTE_FLAG_CRAZY, song.h:34) is our "Pass other": on a chord it holds the
    ///     full chord box in Rocksmith, so an EOF chart that uses it must keep it both ways.
    /// </summary>
    [Fact]
    public void Export_CrazyStatus_SurvivesRoundTrip_OnNotesAndChords()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var level = project.Arrangements.First(a => a.Name == "Lead").Levels[0];
        var crazyNote = level.Notes.OrderBy(n => n.Position).First();
        var crazyChord = level.Chords.OrderBy(c => c.Position).First();
        crazyNote.PassOtherNotes = true;
        crazyChord.PassOtherNotes = true;

        var path = WriteTempEof(project);
        try
        {
            var imported = Assert.Single(new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements).Levels[0];
            var notes = imported.Notes.OrderBy(n => n.Position).ToList();
            var chords = imported.Chords.OrderBy(c => c.Position).ToList();
            Assert.True(notes[0].PassOtherNotes);
            Assert.True(chords[0].PassOtherNotes);
            Assert.All(notes.Skip(1), n => Assert.False(n.PassOtherNotes));
            Assert.All(chords.Skip(1), c => Assert.False(c.PassOtherNotes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_ArpeggioVsHandshape_FlagSurvivesRoundTrip()
    {
        // EOF keeps handshapes IN the arpeggio section array; the
        // RS_ARP_HANDSHAPE flag (2) is the only discriminator. Exporting every
        // handshape with flags 0 turned the whole chart into arpeggio phrases
        // in EOF ("-arp" templates, crazy note status).
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var lead = project.Arrangements.First(a => a.Name == "Lead");
        var level = lead.Levels[0];
        var arpTemplate = new ChordTemplate
        {
            Id = lead.ChordTemplates.Count,
            Name = "SlideArp",
            Arpeggio = true,
            Frets = new Dictionary<int, int> { { 0, 3 }, { 2, 5 } },
            Fingers = new Dictionary<int, int> { { 0, 0 }, { 2, 2 } }
        };
        lead.ChordTemplates.Add(arpTemplate);
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(18),
            EndPosition = new FractionalPosition(19),
            TemplateId = arpTemplate.Id,
            Name = "Arpeggio"
        });

        var path = WriteTempEof(project);
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, [9]);
            var imported = Assert.Single(result.Arrangements);
            var shapes = imported.Levels[0].HandShapes.OrderBy(h => h.Position).ToList();
            Assert.Equal(2, shapes.Count);

            ChordTemplate TemplateOf(HandShape hs) => imported.ChordTemplates.First(t => t.Id == hs.TemplateId);
            Assert.False(TemplateOf(shapes[0]).Arpeggio); // plain handshape (flag 2)
            Assert.True(TemplateOf(shapes[1]).Arpeggio);  // true arpeggio (flag 0)
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_GraceFretHint_SurvivesRoundTripAsAnEofGracePair()
    {
        // A GraceFret hint exports the way EOF's own GP import writes graces —
        // a short slide-in note + the displaced target — and the importer folds
        // that exact shape back into the hint. Previously the hint was silently
        // dropped (the EOF file simply lost the ornament).
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var lead = project.Arrangements.First(a => a.Name == "Lead");
        lead.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(20),
            Sustain = new FractionalPosition(1),
            StringIndex = 2,
            Fret = 7,
            GraceFret = 5
        });
        var sourceNoteCount = lead.Levels[0].Notes.Count;

        var path = WriteTempEof(project);
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, [9]);
            var imported = Assert.Single(result.Arrangements);
            var graced = Assert.Single(imported.Levels[0].Notes, n => n.GraceFret != null);
            Assert.Equal(5, graced.GraceFret);
            Assert.Equal(7, graced.Fret);
            Assert.Equal(2, graced.StringIndex);
            Assert.Equal(new FractionalPosition(20), graced.Position);
            Assert.Equal(sourceNoteCount, imported.Levels[0].Notes.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A chord on beat 22 of the representative Lead level whose two
    /// strings carry the given grace frets (null = no hint on that string).</summary>
    private static Chord AddGracedChord(Arrangement lead, int? graceOnString1, int? graceOnString2)
    {
        var chord = new Chord { Position = EofExportTestCharts.Pos(22) };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 1, Fret = 7, Sustain = EofExportTestCharts.Pos(1), GraceFret = graceOnString1
        });
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 2, Fret = 9, Sustain = EofExportTestCharts.Pos(1), GraceFret = graceOnString2
        });
        lead.Levels[0].Chords.Add(chord);
        return chord;
    }

    [Fact]
    public void Export_ChordGraceHints_SurviveRoundTripAsAnEofGracePair()
    {
        // Chord ornaments stay unnamed to avoid exporting a synthetic manual
        // chord name from EOF to Rocksmith. Import keeps both played chords.
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var lead = project.Arrangements.First(a => a.Name == "Lead");
        AddGracedChord(lead, graceOnString1: 5, graceOnString2: 7); // uniform +2
        var sourceChordCount = lead.Levels[0].Chords.Count;

        var path = WriteTempEof(project);
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, [9]);
            var imported = Assert.Single(result.Arrangements);

            var position = EofExportTestCharts.Pos(22);
            var onset = position.GetPositionMs(project.Beats);
            var targetTime = onset + EofGraceTiming.LengthAt(position, project.Beats);
            var ornament = Assert.Single(imported.Levels[0].Chords, c => c.Position == position);
            var target = Assert.Single(imported.Levels[0].Chords,
                c => Math.Round(c.Position.GetPositionMs(project.Beats)) == targetTime);
            Assert.Equal(new[] { 5, 7 }, ornament.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
            Assert.Equal(new[] { 7, 9 }, target.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
            Assert.All(ornament.ChordNotes.Concat(target.ChordNotes), n => Assert.Null(n.GraceFret));
            Assert.Equal(sourceChordCount + 1, imported.Levels[0].Chords.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_ChordGraceWithNonUniformIntervals_IsReportedAndNotWritten()
    {
        // EOF stores ONE slide end fret per note and projects that one distance
        // onto every string, so a chord grace whose strings travel different
        // intervals is NOT representable. It must land on the report rather than
        // be written as a wrong shape — and it must not add a phantom note.
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var lead = project.Arrangements.First(a => a.Name == "Lead");
        AddGracedChord(lead, graceOnString1: 5, graceOnString2: 8); // +2 and +1
        var sourceChordCount = lead.Levels[0].Chords.Count;

        var result = EofExporter.Export(project);
        Assert.Contains(result.Report.Notes,
            n => n.Kind == EofConversionKind.Dropped && n.Message.Contains("Chord grace ornament"));

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-export-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, result.Bytes);
        try
        {
            var imported = Assert.Single(
                new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements);
            Assert.Equal(sourceChordCount, imported.Levels[0].Chords.Count);
            Assert.All(imported.Levels[0].Chords,
                c => Assert.All(c.ChordNotes, cn => Assert.Null(cn.GraceFret)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_WithoutChordGraceHints_IsUnchanged_NoGracePairIsWritten()
    {
        // The additive-change proof: the representative chart has chords but no
        // ChordNote.GraceFret, so the chord-grace materialization never fires —
        // the export stays lossless and the reimport returns the same chords and
        // notes, with no hint invented and no note added or folded away.
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var lead = project.Arrangements.First(a => a.Name == "Lead");
        var sourceChordCount = lead.Levels[0].Chords.Count;
        var sourceNoteCount = lead.Levels[0].Notes.Count;

        var result = EofExporter.Export(project);
        Assert.True(result.Report.IsLossless,
            "Expected no conversion notes but got: " +
            string.Join("; ", result.Report.Notes.Select(n => $"{n.Kind} {n.Subject}: {n.Message}")));

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-export-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, result.Bytes);
        try
        {
            var imported = Assert.Single(
                new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements);
            Assert.Equal(sourceChordCount, imported.Levels[0].Chords.Count);
            Assert.Equal(sourceNoteCount, imported.Levels[0].Notes.Count);
            Assert.All(imported.Levels[0].Chords,
                c => Assert.All(c.ChordNotes, cn => Assert.Null(cn.GraceFret)));
            Assert.All(imported.Levels[0].Notes, n => Assert.Null(n.GraceFret));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_MultiPointBendCurve_SurvivesRoundTripViaTechNotes()
    {
        // A bend curve that is not the simple start→peak two-point shape used
        // to fold to its peak strength; the shape/timing now rides the tech-
        // note custom block (ID 7), exactly like real EOF stores GP bends.
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var lead = project.Arrangements.First(a => a.Name == "Lead");
        var note = new GuitarNote
        {
            Position = new FractionalPosition(22),
            Sustain = new FractionalPosition(1),
            StringIndex = 1,
            Fret = 8
        };
        note.BendValues.Add(new BendValue(new FractionalPosition(22), 0));
        note.BendValues.Add(new BendValue(new FractionalPosition(22, new Fraction(1, 2)), 1.0));
        note.BendValues.Add(new BendValue(new FractionalPosition(23), 2.0));
        lead.Levels[0].Notes.Add(note);

        var path = WriteTempEof(project);
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, [9]);
            var imported = Assert.Single(result.Arrangements);
            var bent = Assert.Single(imported.Levels[0].Notes,
                n => n.Position == new FractionalPosition(22) && n.StringIndex == 1);

            Assert.Equal(3, bent.BendValues.Count);
            Assert.Equal(0.0, bent.BendValues[0].Step);
            Assert.Equal(1.0, bent.BendValues[1].Step);
            Assert.Equal(new FractionalPosition(22, new Fraction(1, 2)), bent.BendValues[1].Position);
            Assert.Equal(2.0, bent.BendValues[2].Step);
            Assert.Equal(new FractionalPosition(23), bent.BendValues[2].Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (ChartProject Project, Chord Chord) ProjectWithChordSlides(params (int String, int Fret, int SlideTo)[] strings)
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var lead = project.Arrangements.First(a => a.Name == "Lead");
        var template = new ChordTemplate
        {
            Id = 77,
            Name = "Dbl",
            Frets = strings.ToDictionary(x => x.String, x => x.Fret)
        };
        lead.ChordTemplates.Add(template);
        var chord = new Chord { Position = new FractionalPosition(30), TemplateId = 77 };
        foreach (var (str, fret, slideTo) in strings)
        {
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = str, Fret = fret, Sustain = new FractionalPosition(1), SlideTo = slideTo
            });
        }

        lead.Levels[0].Chords.Add(chord);
        return (project, chord);
    }

    [Fact]
    public void Export_ChordWithMixedSlideDistances_RoundTripsPerStringSlides()
    {
        // Double-stop G 8->9 + D 9->11: one slideend per chord used to project +1 on both.
        var (project, _) = ProjectWithChordSlides((3, 8, 9), (2, 9, 11));
        var result = EofExporter.Export(project);
        Assert.DoesNotContain(result.Report.Notes, n => n.Message.Contains("slide distances"));
        Assert.DoesNotContain(result.Report.Notes, n => n.Message.Contains("per-string slide"));

        var path = WriteTempEof(project);
        try
        {
            var imported = Assert.Single(new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements);
            var chord = Assert.Single(imported.Levels[0].Chords, c => c.Position == new FractionalPosition(30));
            Assert.Equal(9, chord.ChordNotes.Single(n => n.StringIndex == 3).SlideTo);
            Assert.Equal(11, chord.ChordNotes.Single(n => n.StringIndex == 2).SlideTo);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_ChordWithUniformSlide_WritesNoSlideTechNotes()
    {
        var (project, _) = ProjectWithChordSlides((3, 8, 10), (2, 9, 11));
        var uniform = EofExporter.Export(project);
        var mixed = EofExporter.Export(ProjectWithChordSlides((3, 8, 9), (2, 9, 11)).Project);

        // The mixed chord adds a tech-note block (ID 7) with one tech note; the uniform one
        // must add none, so it is strictly smaller than the mixed export.
        Assert.True(uniform.Bytes.Length < mixed.Bytes.Length);
        Assert.DoesNotContain(uniform.Report.Notes, n => n.Message.Contains("slide"));

        var path = WriteTempEof(project);
        try
        {
            var imported = Assert.Single(new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements);
            var chord = Assert.Single(imported.Levels[0].Chords, c => c.Position == new FractionalPosition(30));
            Assert.Equal(10, chord.ChordNotes.Single(n => n.StringIndex == 3).SlideTo);
            Assert.Equal(11, chord.ChordNotes.Single(n => n.StringIndex == 2).SlideTo);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_MidiOffset_IsTheFirstBeatsPosition()
    {
        // EOF's invariant (gp_import.c:5649 / beat.c:132): the OGG profile's
        // midi_offset IS beat 0's position. Writing 0 while beat 0 sits at
        // 10 s (baked leading silence) trips EOF's tempo-map-corruption alert
        // on every open, and accepting its repair desyncs the chart by 10 s.
        var project = EofExportTestCharts.BuildRepresentativeProject();
        foreach (var beat in project.Beats) beat.Time += 10_000;

        var path = WriteTempEof(project);
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, [9]);
            Assert.Equal(10_000u, result.Metadata.MidiOffsetMs);
            Assert.Equal(10_000.0, result.Metadata.Beats[0].TimeMs, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_EmptyProjectWithoutBeats_StillListsNoTracks()
    {
        var path = WriteTempEof(new ChartProject());
        try
        {
            var tracks = new EofImporter().GetAvailableTracks(path);
            Assert.Empty(tracks); // Scaffold-only project: no pro guitar notes anywhere.
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AssignTracks_BassOverflowsIntoGuitarSlots()
    {
        var project = new ChartProject();
        for (var i = 0; i < 3; i++)
            project.Arrangements.Add(new Arrangement { Name = $"Bass {i}", ArrangementType = ArrangementType.Bass });

        var assignments = EofExportSession.AssignTracks(project);

        Assert.Equal(new[] { 8, 9, 11 }, assignments.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void AssignTracks_UsesBonusSlotForThirdGuitar()
    {
        var project = new ChartProject();
        for (var i = 0; i < 3; i++)
            project.Arrangements.Add(new Arrangement { Name = $"Guitar {i}", ArrangementType = ArrangementType.Lead });

        var assignments = EofExportSession.AssignTracks(project);

        Assert.Equal(new[] { 9, 12, 14 }, assignments.Keys.OrderBy(k => k).ToArray());
    }
}
