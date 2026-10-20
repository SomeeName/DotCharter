using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     The RS "ignore" status has a home in an EOF project: the pro-guitar note's
///     EXTENDED flags dword, bit 0
///     (<c>EOF_PRO_GUITAR_NOTE_EFLAG_IGNORE</c>, EOF_SRC/song.h:130 — "a note that
///     will export to RS2 format with the 'ignore' status set"). EOF's own RS-XML
///     reader sets it from <c>ignore="1"</c> (rs_import.c:257 for a note, :1788 for
///     a chord) and its writer turns it back into <c>ignore</c> (rs.c:5760).
///     <para>
///         DotCharter's exporter dropped it instead, with a report line stating it
///         was "not representable in an EOF project". Because the `.eof` is written
///         as a save COMPANION, every Ctrl+S on a psarc- or RS-XML-sourced chart
///         forgot every ignored sound — and the false report also marked the whole
///         conversion lossy, which is what kept the round-trip parity gate from
///         covering the field at all (its lossless assert tripped first).
///     </para>
/// </summary>
public class EofIgnoreFlagTests
{
    private static ChartProject BuildProject(bool ignoreNote, bool ignoreChord)
    {
        var project = new ChartProject { Bpm = 120 };
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Name = "Lead",
            StringCount = 6
        };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var template = new ChordTemplate { Id = 0, Name = "E5" };
        template.Frets[0] = 3;
        template.Frets[1] = 5;
        arrangement.ChordTemplates.Add(template);

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(project, 2000),
            StringIndex = 2,
            Fret = 7,
            Ignore = ignoreNote
        });

        var chord = new Chord { Position = Pos(project, 4000), TemplateId = 0, Ignore = ignoreChord };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);

        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static FractionalPosition Pos(ChartProject project, double ms) => TimelineMath.PositionFromMs(ms, project.Beats);

    private static (GuitarNote Note, Chord Chord) RoundTrip(ChartProject project)
    {
        var result = EofExporter.Export(project);
        Assert.True(result.Report.IsLossless,
            "An ignored sound is representable, so the conversion must not report a loss: " +
            string.Join("; ", result.Report.Notes.Select(n => $"{n.Kind} {n.Subject}: {n.Message}")));

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-ignore-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, result.Bytes);
        try
        {
            var importer = new EofImporter();
            var tracks = importer.GetAvailableTracks(path);
            var imported = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
            var level = Assert.Single(Assert.Single(imported.Arrangements).Levels);
            return (Assert.Single(level.Notes), Assert.Single(level.Chords));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_ignored_note_and_chord_survive_the_eof_round_trip()
    {
        var (note, chord) = RoundTrip(BuildProject(ignoreNote: true, ignoreChord: true));

        Assert.True(note.Ignore);
        Assert.True(chord.Ignore);
    }

    [Fact]
    public void A_sound_that_is_not_ignored_stays_that_way()
    {
        // The eflags dword is only written when it is non-zero, so the negative
        // case walks a different byte layout than the positive one.
        var (note, chord) = RoundTrip(BuildProject(ignoreNote: false, ignoreChord: false));

        Assert.False(note.Ignore);
        Assert.False(chord.Ignore);
    }

    [Fact]
    public void The_ignore_eflag_is_bit_zero_of_the_extended_flags_dword()
    {
        // Pinned against EOF_SRC/song.h:130 — a wrong bit here is invisible in a
        // DotCharter-only round trip but changes what EOF itself reads.
        Assert.Equal(1u, EofConstants.PgEFlagIgnore);

        var raw = new EofRawProGuitarNote
        {
            NoteBitmask = 0b1,
            PositionMs = 1000,
            LengthMs = 100,
            ExtendedFlags = EofConstants.PgEFlagIgnore
        };
        raw.Frets[0] = 5;

        var w = new EofPackWriter();
        EofProGuitarNoteWriter.Write(w, raw);
        var reader = new EofPackReader(w.ToArray());
        var read = EofProGuitarNoteReader.Read(reader);

        Assert.True(reader.Eof); // Byte-exact: the extended dword and nothing more.
        Assert.Equal(EofConstants.PgEFlagIgnore, read.ExtendedFlags);
        // The record only carries the dword because the marker bit says so.
        Assert.NotEqual(0u, read.Flags & EofConstants.NoteFlagExtended);
    }
}
