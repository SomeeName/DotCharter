using System.IO.Compression;
using System.Text;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The app reads a 5-string arrangement as E A D G B (the 6-string row,
///     truncated; <c>StandardOpenStrings</c>). Both Guitar Pro importers used to
///     measure a 5-string track against B E A D G, so a standard 5-string bass
///     imported with offsets 0 and every open string sounded 4-5 semitones high.
///     They now subtract the domain reference, so B E A D G imports as
///     <c>-5 -5 -5 -5 -4</c> and sounds (and is named) correctly; the GP and
///     alphaTex exporters add the same reference back.
/// </summary>
public sealed class GpFiveStringBassTuningTests
{
    private static readonly int[] StandardBass5 = [23, 28, 33, 38, 43];

    private static Arrangement ImportGpif(string pitches)
    {
        var gpif =
            "<GPIF><MasterTrack><Tracks>0</Tracks></MasterTrack>" +
            "<Tracks><Track id=\"0\"><Name>Bass</Name><Staves><Staff><Properties>" +
            $"<Property name=\"Tuning\"><Pitches>{pitches}</Pitches></Property>" +
            "</Properties></Staff></Staves></Track></Tracks>" +
            "<MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>" +
            "<Bars><Bar id=\"0\"><Voices>-1 -1 -1 -1</Voices></Bar></Bars></GPIF>";
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpbass5-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
            writer.Write(gpif);
        return Import(path);
    }

    private static Arrangement ImportGp5(int[] tuningHighFirst)
    {
        var bar = new FxMeasure().AddVoice(
            FxBeat.Note(0, 0, 0), FxBeat.Note(0, 1, 0), FxBeat.Note(0, 2, 0), FxBeat.Note(0, 3, 0));
        var path = LegacyGpFixtureBuilder.WriteFile("5.00", [bar], trackName: "Bass", stringCount: 5,
            tuningHighFirst: tuningHighFirst);
        return Import(path);
    }

    private static Arrangement Import(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()).Single();
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    private static int[] Sounding(Arrangement arr) =>
        [.. Enumerable.Range(0, arr.StringCount).Select(s => SoundingPitch.MidiFor(arr, s, 0) ?? -1)];

    [Fact]
    public void Gpif_StandardFiveStringBass_SoundsBEADG()
    {
        var arr = ImportGpif("23 28 33 38 43");

        Assert.Equal(StandardBass5, Sounding(arr));
        Assert.Equal(["B", "E", "A", "D", "G"], arr.Tuning);
        Assert.Equal([-5, -5, -5, -5, -4], arr.TuningPitches.Select(t => t.Step));
    }

    [Fact]
    public void Gp5_StandardFiveStringBass_SoundsBEADG()
    {
        // GP stores the tuning highest string first.
        var arr = ImportGp5([43, 38, 33, 28, 23]);

        Assert.Equal(StandardBass5, Sounding(arr));
        Assert.Equal(["B", "E", "A", "D", "G"], arr.Tuning);
        Assert.Equal([-5, -5, -5, -5, -4], arr.TuningPitches.Select(t => t.Step));
    }

    [Fact]
    public void Gpif_AndGp5_FiveStringBassWholeStepDown_AreTwoLowerOnEveryString()
    {
        var expected = StandardBass5.Select(p => p - 2).ToArray();

        Assert.Equal(expected, Sounding(ImportGpif("21 26 31 36 41")));
        Assert.Equal(expected, Sounding(ImportGp5([41, 36, 31, 26, 21])));
    }

    [Fact]
    public void FiveStringBass_ExportsTheSameGpPitchesItWasImportedFrom()
    {
        var arr = ImportGpif("23 28 33 38 43");

        Assert.Equal([43.0, 38.0, 33.0, 28.0, 23.0], GpExporter.BuildTuningHighToLow(arr));
    }
}
