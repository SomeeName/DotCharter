using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;
using AT = AlphaTab.Model;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A Guitar Pro STACCATO imports as a strike (sustain 0) and never as an
///     accent, on both readers; a real accent stays an accent.
///     <para>
///     gpif's <c>&lt;Accent&gt;</c> element is a bit field — 0x01 staccato, 0x04 heavy
///     accent, 0x08 accent, 0x10 tenuto (alphaTab's GpifParser / GpifWriter; pinned below
///     against the writer we ship). The GP3-5 binary format keeps staccato in bit 0x01 of
///     the note-effects-2 byte (alphaTab's Gp3To5Importer.readNoteEffects); the accents are
///     the note HEADER's 0x40 (accent) and, GP5, 0x02 (heavy accent).
///     </para>
///     <para>
///     A run of repeated staccato chords keeps one hand-shape bar, which ends with the last
///     strum (half its written value, the length a staccato is played) instead of running
///     on for the whole value.
///     </para>
/// </summary>
[Collection("AlphaTab")]
public sealed class GpStaccatoImportTests
{
    private const int Staccato = 0x01;
    private const int HeavyAccent = 0x04;
    private const int Accent = 0x08;

    // ---- the bit meanings, read off the gpif writer we ship ----------------

    [Fact]
    public void AlphaTabsGpifWriter_SpellsTheArticulationsAsTheseBits()
    {
        var written = WriteWithAlphaTab(
            n => n.IsStaccato = true,
            n => n.Accentuated = AT.AccentuationType.Heavy,
            n => n.Accentuated = AT.AccentuationType.Normal,
            n => n.Accentuated = AT.AccentuationType.Tenuto);

        Assert.Equal(["1", "4", "8", "16"], written);
    }

    private static List<string?> WriteWithAlphaTab(params Action<AT.Note>[] marks) =>
        AlphaTabRuntime.RunExclusive(() => WriteGpif(marks));

    private static List<string?> WriteGpif(Action<AT.Note>[] marks)
    {
        var settings = AlphaTabRuntime.CreateSettings();
        AT.Score.ResetIds();
        var score = new AT.Score();
        var track = new AT.Track();
        var staff = new AT.Staff { ShowTablature = true };
        track.AddStaff(staff);
        score.AddTrack(track);
        staff.StringTuning.Tunings = [64, 59, 55, 50, 45, 40];
        score.AddMasterBar(new AT.MasterBar { TimeSignatureNumerator = 4, TimeSignatureDenominator = 4 });
        var bar = new AT.Bar();
        staff.AddBar(bar);
        var voice = new AT.Voice();
        bar.AddVoice(voice);
        foreach (var mark in marks)
        {
            var beat = new AT.Beat { Duration = AT.Duration.Quarter };
            var note = new AT.Note { Fret = 5, String = 1 };
            mark(note);
            beat.AddNote(note);
            voice.AddBeat(beat);
        }

        score.Finish(settings);
        var data = new AlphaTab.Exporter.Gp7Exporter().Export(score, settings);
        var bytes = new byte[(int)data.Length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)data[i];
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var gpif = zip.GetEntry("Content/score.gpif")!.Open();
        return [.. XDocument.Load(gpif).Descendants("Note").Select(n => n.Element("Accent")?.Value)];
    }

    // ---- gpif (GP6-8) -------------------------------------------------------

    private static string Note(int id, int stringIndex, int fret, int? accent = null) => $"""
        <Note id="{id}">{(accent is { } a ? $"<Accent>{a}</Accent>" : "")}<Properties>
          <Property name="String"><String>{stringIndex}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
        </Properties></Note>
        """;

    /// <summary>Rhythm refs: 0 = eighth, 1 = quarter, 2 = half. Bars are lists of beats
    /// (rhythm, note ids); a beat without notes is a rest.</summary>
    private static string Score(IReadOnlyList<(int Rhythm, int[] Notes)[]> bars, string notes)
    {
        var beats = new StringBuilder();
        var voices = new StringBuilder();
        var barXml = new StringBuilder();
        var masterBars = new StringBuilder();
        var beatId = 0;
        for (var b = 0; b < bars.Count; b++)
        {
            var ids = new List<int>();
            foreach (var (rhythm, ns) in bars[b])
            {
                var notesXml = ns.Length > 0 ? $"<Notes>{string.Join(' ', ns)}</Notes>" : "";
                beats.Append($"<Beat id=\"{beatId}\"><Rhythm ref=\"{rhythm}\" />{notesXml}</Beat>");
                ids.Add(beatId++);
            }

            voices.Append($"<Voice id=\"{b}\"><Beats>{string.Join(' ', ids)}</Beats></Voice>");
            barXml.Append($"<Bar id=\"{b}\"><Voices>{b} -1 -1 -1</Voices></Bar>");
            masterBars.Append($"<MasterBar><Time>4/4</Time><Bars>{b}</Bars></MasterBar>");
        }

        return $"""
            <GPIF>
              <Score><Title>Staccato fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks><Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves></Track></Tracks>
              <MasterBars>{masterBars}</MasterBars>
              <Bars>{barXml}</Bars>
              <Voices>{voices}</Voices>
              <Beats>{beats}</Beats>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Eighth</NoteValue></Rhythm>
                <Rhythm id="1"><NoteValue>Quarter</NoteValue></Rhythm>
                <Rhythm id="2"><NoteValue>Half</NoteValue></Rhythm>
              </Rhythms>
              <Notes>{notes}</Notes>
            </GPIF>
            """;
    }

    private static ChartLevel ImportGpif(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpstacc-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
        {
            writer.Write(gpif);
        }

        try
        {
            var result = new GpImporter().ImportTracksWithMetadata(path, [0], GpImportSettings.Default);
            return Assert.Single(result.Arrangements).Levels[^1];
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    /// <summary>Bar 1: staccato half, accented half. Bar 2: heavy-accented half, a
    /// staccato eighth, an eighth rest, a plain quarter. Bar 3: three identical staccato
    /// strums and a quarter rest. Bar 4: a half note, a half rest.</summary>
    [Fact]
    public void Gpif_StaccatoIsAStrikeWithoutAccent_AndAccentsStayAccents()
    {
        var notes = Note(0, 3, 5, Staccato) + Note(1, 3, 7, Accent) + Note(2, 3, 9, HeavyAccent)
            + Note(3, 3, 5, Staccato) + Note(4, 3, 7)
            + Note(5, 0, 3, Staccato) + Note(6, 1, 5, Staccato)
            + Note(7, 0, 3, Staccato) + Note(8, 1, 5, Staccato)
            + Note(9, 0, 3, Staccato) + Note(10, 1, 5, Staccato)
            + Note(11, 3, 2);
        var level = ImportGpif(Score(
        [
            [(2, [0]), (2, [1])],
            [(2, [2]), (0, [3]), (0, []), (1, [4])],
            [(1, [5, 6]), (1, [7, 8]), (1, [9, 10]), (1, [])],
            [(2, [11]), (2, [])]
        ], notes));

        AssertSinglesAndStrums(level);
    }

    // ---- GP3-5 binary -------------------------------------------------------

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void Gp3To5_StaccatoIsAStrikeWithoutAccent_AndAccentsStayAccents(string version)
    {
        static FxBeat Single(int duration, FxNote note) => new() { Duration = duration, Notes = { note } };
        static FxBeat Strum() => FxBeat.Chord(0, new FxNote(0, 3, Staccato: true), new FxNote(1, 5, Staccato: true));

        var path = LegacyGpFixtureBuilder.WriteFile(version,
        [
            new FxMeasure().AddVoice(Single(-1, new FxNote(3, 5, Staccato: true)), Single(-1, new FxNote(3, 7, Accent: true))),
            new FxMeasure().AddVoice(Single(-1, new FxNote(3, 9, Accent: true)), Single(1, new FxNote(3, 5, Staccato: true)),
                FxBeat.RestBeat(1), FxBeat.Note(0, 3, 7)),
            new FxMeasure().AddVoice(Strum(), Strum(), Strum(), FxBeat.RestBeat(0)),
            new FxMeasure().AddVoice(FxBeat.Note(-1, 3, 2), FxBeat.RestBeat(-1))
        ]);
        try
        {
            var importer = new GpImporter();
            var result = importer.ImportTracksWithMetadata(path,
                [.. importer.GetAvailableTracks(path).Select(t => t.Id)], GpImportSettings.Default);
            AssertSinglesAndStrums(Assert.Single(result.Arrangements).Levels[^1]);
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    // ---- the shared expectations ---------------------------------------------

    private static void AssertSinglesAndStrums(ChartLevel level)
    {
        var singles = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(6, singles.Count);

        // The staccato half: a strike, and NOT an accent.
        Assert.Equal(FractionalPosition.Zero, singles[0].Sustain);
        Assert.False(singles[0].Accent, "staccato is not an accent");
        // The accented half and the heavy-accented half stay accents and keep their length.
        Assert.True(singles[1].Accent);
        Assert.True(singles[1].Sustain > FractionalPosition.Zero, "an accent is not a staccato");
        Assert.True(singles[2].Accent);
        Assert.True(singles[2].Sustain > FractionalPosition.Zero);
        // A staccato eighth before a short rest: the short-rest rule must not hand the
        // accent back to a note the tab already marked staccato.
        Assert.Equal(new FractionalPosition(6), singles[3].Position);
        Assert.False(singles[3].Accent, "an authored staccato never becomes an accent");
        Assert.Equal(FractionalPosition.Zero, singles[3].Sustain);
        Assert.False(singles[4].Accent);

        // Three identical staccato strums at 8, 9 and 10: strikes, no accent, one bar that
        // ends with the last strum (half its written quarter) instead of a quarter after it.
        var strums = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(3, strums.Count);
        Assert.All(strums, c =>
        {
            Assert.False(c.Accent);
            Assert.All(c.ChordNotes, cn => Assert.Equal(FractionalPosition.Zero, cn.Sustain));
            Assert.All(c.ChordNotes, cn => Assert.False(cn.Accent));
        });
        var bar = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(8), bar.Position);
        Assert.Equal(new FractionalPosition(0, new Fraction(21, 2)), bar.EndPosition);
    }
}
