using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A tab importer SUBTRACTS a standard tuning to get per-string offsets and
///     <see cref="TuningUtilities.GetTuningNames" /> ADDS one back to name them.
///     They have to be the same table, and for 8-string tracks they were not:
///     all three tab importers carried a private 4 / 5 / 7 switch that fell back
///     to the SIX-string reference for everything else, while the namer already
///     knew an 8-string starts on F#. A standard-tuned 8-string therefore
///     imported with offsets <c>-10 -10 -10 -10 -9 -9 0 0</c> and came out named
///     <c>G# C# F# B F Bb B E</c> — wrong in both halves, and not even consistent
///     with each other, so the tuning chip, the tuner, chord naming and every
///     exported <c>stringN</c> disagreed with the notes.
///     <para>
///     There is ONE table now (<see cref="TuningUtilities.GetStandardMidiPitches" />)
///     and the namer derives its pitch classes from it. That includes 5 strings
///     (E A D G B): a B E A D G bass imports as <c>-5 -5 -5 -5 -4</c>
///     (<see cref="GpFiveStringBassTuningTests" />).
///     </para>
/// </summary>
public sealed class ExtendedRangeTuningImportTests
{
    // ---- the shared reference table ---------------------------------------

    /// <summary>
    ///     The property the whole bug is about: subtracting the reference from
    ///     the reference must name the reference. Any string count for which
    ///     that does not hold is a count whose importer and namer disagree.
    /// </summary>
    [Theory]
    [InlineData(4, new[] { "E", "A", "D", "G" })]
    [InlineData(6, new[] { "E", "A", "D", "G", "B", "E" })]
    [InlineData(7, new[] { "B", "E", "A", "D", "G", "B", "E" })]
    [InlineData(8, new[] { "F#", "B", "E", "A", "D", "G", "B", "E" })]
    [InlineData(9, new[] { "C#", "F#", "B", "E", "A", "D", "G", "B", "E" })]
    [InlineData(10, new[] { "C#", "F#", "B", "E", "A", "D", "G", "B", "E", "E" })]
    public void TheStandardPitchesAndTheNamesAreOneTable(int stringCount, string[] expected)
    {
        var standard = TuningUtilities.GetStandardMidiPitches(stringCount);

        Assert.Equal(stringCount, standard.Length);
        Assert.Equal(expected, TuningUtilities.GetTuningNames(stringCount, new int[stringCount]));
        // …and an importer's own subtraction against that table is all zeros.
        var offsets = standard.Select((p, i) => p - standard[i]).ToArray();
        Assert.Equal(expected, TuningUtilities.GetTuningNames(stringCount, offsets));
    }

    // ---- gpif (GP6+ container) --------------------------------------------

    private static string Gpif(string pitches) => $"""
        <GPIF>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>{pitches}</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars>
            <MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
          </MasterBars>
          <Bars>
            <Bar id="0"><Voices>-1 -1 -1 -1</Voices></Bar>
          </Bars>
        </GPIF>
        """;

    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gprange-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static Arrangement ImportOneTrack(string pitches)
    {
        var path = WriteGpFile(Gpif(pitches));
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList()).Arrangements.Single();
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    [Theory]
    // F# B E A D G B E — standard 8-string.
    [InlineData("30 35 40 45 50 55 59 64", 8, new[] { "F#", "B", "E", "A", "D", "G", "B", "E" })]
    // C# F# B E A D G B E — standard 9-string: every string survives the import.
    [InlineData("25 30 35 40 45 50 55 59 64", 9, new[] { "C#", "F#", "B", "E", "A", "D", "G", "B", "E" })]
    // B E A D G B E — standard 7-string (a regression guard: this row existed).
    [InlineData("35 40 45 50 55 59 64", 7, new[] { "B", "E", "A", "D", "G", "B", "E" })]
    // E A D G B e — standard 6-string.
    [InlineData("40 45 50 55 59 64", 6, new[] { "E", "A", "D", "G", "B", "E" })]
    public void AGpifStandardTuning_ImportsAsAllZeroOffsets(
        string pitches, int expectedStrings, string[] expectedNames)
    {
        var arrangement = ImportOneTrack(pitches);

        Assert.Equal(expectedStrings, arrangement.StringCount);
        Assert.Equal(expectedNames, arrangement.Tuning);
        Assert.All(arrangement.TuningPitches, t => Assert.Equal(0, t.Step));
    }

    /// <summary>An 8-string in drop-E must move exactly its lowest string, and
    /// the letters must follow the offsets rather than a different table.</summary>
    [Fact]
    public void AnEightStringDropTuning_MovesOnlyTheLowestString()
    {
        // F# down two semitones to E; the other seven untouched.
        var arrangement = ImportOneTrack("28 35 40 45 50 55 59 64");

        Assert.Equal(8, arrangement.StringCount);
        Assert.Equal(-2, arrangement.TuningPitches[0].Step);
        Assert.All(arrangement.TuningPitches.Skip(1), t => Assert.Equal(0, t.Step));
        Assert.Equal(["E", "B", "E", "A", "D", "G", "B", "E"], arrangement.Tuning);
    }

    /// <summary>
    ///     The owner's case (#742): an imported extended-range tab is TUNED to the
    ///     pitches the tab states — the gutter names them, the tuner asks for them and
    ///     live judging expects them. Every consumer used to read string 0 as E2 and
    ///     the 7th string as a high A4, so a 7-string's gutter read E A D G B E A.
    /// </summary>
    [Theory]
    [InlineData("35 40 45 50 55 59 64", new[] { "B", "E", "A", "D", "G", "B", "E" })]
    [InlineData("33 40 45 50 55 59 64", new[] { "A", "E", "A", "D", "G", "B", "E" })]
    [InlineData("30 35 40 45 50 55 59 64", new[] { "F#", "B", "E", "A", "D", "G", "B", "E" })]
    [InlineData("25 30 35 40 45 50 55 59 64", new[] { "C#", "F#", "B", "E", "A", "D", "G", "B", "E" })]
    public void AnImportedExtendedRangeTab_IsTunedAndJudgedAtTheTabsOwnPitches(string pitches, string[] gutter)
    {
        var tab = pitches.Split(' ').Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var arrangement = ImportOneTrack(pitches);

        Assert.Equal(gutter, DotCharter.UI.Controls.TimelineGutterControl.StringNamesFor(arrangement, tab.Length));
        Assert.Equal(tab, DotCharter.Tone.Audio.Tuner.TuningCheckTargets.Build(arrangement).Select(t => t.Midi));
        Assert.Equal(tab, Enumerable.Range(0, tab.Length)
            .Select(s => DotCharter.Services.Audio.LivePlay.LivePitchAcceptance.SoundingMidi(arrangement, s, 0) ?? -1));
        Assert.Equal(tab, Enumerable.Range(0, tab.Length).Select(s => SoundingPitch.MidiFor(arrangement, s, 0) ?? -1));
    }
}
