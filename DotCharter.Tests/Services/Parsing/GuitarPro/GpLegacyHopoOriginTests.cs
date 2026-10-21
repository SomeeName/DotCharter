using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro 3-5 stores the slur bit (note-effect fx1 0x02) on the note the
///     slur STARTS from; the destination is the next note on the same string. The
///     importer used to put <c>HammerOn</c> on that origin, so <c>1h3</c> imported
///     as a hammer on the 1 and a plain 3 (and the post-pass turned the 1 into a
///     pull-off after any higher note). The flag now lands on the destination, as
///     the gpif path's <c>HopoDestination</c> does.
/// </summary>
public sealed class GpLegacyHopoOriginTests
{
    private static List<GuitarNote> Import(params FxMeasure[] measures)
    {
        var path = LegacyGpFixtureBuilder.WriteFile("5.00", measures);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arr = Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
            return [.. Assert.Single(arr.Levels).Notes.OrderBy(n => n.Position)];
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    private static FxBeat Beat(int fret, bool origin = false, bool tied = false) =>
        new() { Duration = 2, Notes = { new FxNote(0, fret, Tied: tied, HammerOrigin: origin) } };

    [Fact]
    public void HammerOnAndPullOff_LandOnTheDestination_InsideABar()
    {
        // 1h3 3p1 (16ths on one string): the origin carries the flag, the next note is the target.
        var bar = new FxMeasure().AddVoice(Beat(1, origin: true), Beat(3, origin: true), Beat(1), FxBeat.RestBeat(0));

        var notes = Import(bar);

        Assert.Equal([1, 3, 1], notes.Select(n => n.Fret));
        Assert.Equal([HopoType.None, HopoType.HammerOn, HopoType.PullOff], notes.Select(n => n.Hopo));
    }

    [Fact]
    public void ARepeatedHammerFigure_NeverTurnsTheOriginIntoAPullOff()
    {
        // 1h3 1h3: the second origin follows a HIGHER note, which is what used to flip it to a pull-off.
        var bar = new FxMeasure().AddVoice(Beat(1, origin: true), Beat(3), Beat(1, origin: true), Beat(3));

        var notes = Import(bar);

        Assert.Equal([HopoType.None, HopoType.HammerOn, HopoType.None, HopoType.HammerOn],
            notes.Select(n => n.Hopo));
    }

    [Fact]
    public void TheDestination_MayBeInTheNextBar()
    {
        var bar1 = new FxMeasure().AddVoice(FxBeat.RestBeat(0), FxBeat.RestBeat(0), FxBeat.RestBeat(0),
            Beat(1, origin: true));
        var bar2 = new FxMeasure().AddVoice(Beat(3), FxBeat.RestBeat(0));

        var notes = Import(bar1, bar2);

        Assert.Equal([1, 3], notes.Select(n => n.Fret));
        Assert.Equal([HopoType.None, HopoType.HammerOn], notes.Select(n => n.Hopo));
    }

    [Fact]
    public void ATieBetweenOriginAndDestination_IsSkipped()
    {
        // 1 (origin) ~1 (tie continuation) 3: the flag lands on the first NON-tied note.
        var bar = new FxMeasure().AddVoice(Beat(1, origin: true), Beat(1, tied: true), Beat(3), FxBeat.RestBeat(0));

        var notes = Import(bar);

        Assert.Equal([1, 3], notes.Select(n => n.Fret));
        Assert.Equal([HopoType.None, HopoType.HammerOn], notes.Select(n => n.Hopo));
    }

    [Fact]
    public void AnOriginWithNoFollowingNote_DropsTheFlag()
    {
        var bar = new FxMeasure().AddVoice(Beat(3), Beat(1, origin: true), FxBeat.RestBeat(0), FxBeat.RestBeat(0));

        var notes = Import(bar);

        Assert.All(notes, n => Assert.Equal(HopoType.None, n.Hopo));
    }

    [Fact]
    public void GpifHopoDestination_AndGp5Origin_ImportTheSameHopoValues()
    {
        // The same figure (1h3 3p1 on string 0) written both ways.
        var gp5 = Import(new FxMeasure().AddVoice(Beat(1, origin: true), Beat(3, origin: true), Beat(1),
            FxBeat.RestBeat(0)));
        var gpif = ImportGpif([(1, false), (3, true), (1, true)]);

        Assert.Equal(gpif.Select(n => n.Fret), gp5.Select(n => n.Fret));
        Assert.Equal(gpif.Select(n => n.Hopo), gp5.Select(n => n.Hopo));
    }

    // gpif marks the DESTINATION with a HopoDestination property; the post-pass derives the pull-off.
    private static List<GuitarNote> ImportGpif((int Fret, bool Destination)[] notes)
    {
        var noteXml = string.Concat(notes.Select((n, i) =>
            $"<Note id=\"{i}\"><Properties>" +
            "<Property name=\"String\"><String>0</String></Property>" +
            $"<Property name=\"Fret\"><Fret>{n.Fret}</Fret></Property>" +
            (n.Destination ? "<Property name=\"HopoDestination\"><Enable/></Property>" : "") +
            "</Properties></Note>"));
        var beatXml = string.Concat(notes.Select((_, i) =>
            $"<Beat id=\"{i}\"><Rhythm ref=\"0\"/><Notes>{i}</Notes></Beat>"));
        var beatIds = string.Join(' ', notes.Select((_, i) => i));
        var gpif =
            "<GPIF><MasterTrack><Tracks>0</Tracks></MasterTrack>" +
            "<Tracks><Track id=\"0\"><Name>Lead</Name><Staves><Staff><Properties>" +
            "<Property name=\"Tuning\"><Pitches>40 45 50 55 59 64</Pitches></Property>" +
            "</Properties></Staff></Staves></Track></Tracks>" +
            "<MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>" +
            "<Bars><Bar id=\"0\"><Voices>0 -1 -1 -1</Voices></Bar></Bars>" +
            $"<Voices><Voice id=\"0\"><Beats>{beatIds}</Beats></Voice></Voices>" +
            $"<Beats>{beatXml}</Beats><Notes>{noteXml}</Notes>" +
            "<Rhythms><Rhythm id=\"0\"><NoteValue>16th</NoteValue></Rhythm></Rhythms></GPIF>";
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gphopo-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
            writer.Write(gpif);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arr = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList()).Arrangements.Single();
            return [.. Assert.Single(arr.Levels).Notes.OrderBy(n => n.Position)];
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }
}
