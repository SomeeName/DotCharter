using System.IO.Compression;
using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The repeat UNFOLD both Guitar Pro importers share. A repeat that is not
///     unfolded is not a cosmetic loss: the bars it would have added are simply
///     absent, so every later note in the chart sits early by that many bars.
/// </summary>
public sealed class GpRepeatUnfoldTests
{
    private static XElement MasterBars(int count, Func<int, string?> repeatFor)
    {
        var bars = string.Concat(Enumerable.Range(0, count).Select(i =>
            $"<MasterBar><Time>4/4</Time><Bars>0</Bars>{repeatFor(i) ?? ""}</MasterBar>"));
        return XElement.Parse($"<MasterBars>{bars}</MasterBars>");
    }

    /// <summary>
    ///     A <c>:|</c> with no <c>|:</c> jumps back to the START of the piece —
    ///     GP's own playback and alphaTab's. The scan used to break out when it
    ///     found no <c>start="true"</c> anywhere and emit all 16 bars verbatim,
    ///     dropping the whole second pass.
    /// </summary>
    [Fact]
    public void ARepeatCloseWithNoOpen_JumpsBackToTheFirstBar()
    {
        // 16 bars; the 8th (index 7) carries `:|` with count=2 and nothing else does.
        var masterBars = MasterBars(16, i => i == 7 ? """<Repeat start="false" end="true" count="2"/>""" : null);

        var unfolded = GpMasterBarSequence.Unfold(masterBars);

        // 8 bars played twice + the 8 straight bars that follow.
        Assert.Equal(24, unfolded.Count);
        Assert.Equal(Enumerable.Range(0, 8).Concat(Enumerable.Range(0, 8)).Concat(Enumerable.Range(8, 8)),
            unfolded.Select(b => b.OriginalIndex));
        Assert.Equal(2, unfolded[8].RepeatPass);
    }

    /// <summary>
    ///     …and after an earlier close it re-opens at the bar AFTER it, not at
    ///     bar 0 — otherwise the second repeat would swallow the first section
    ///     again.
    /// </summary>
    [Fact]
    public void ASecondCloseWithNoOpen_ReOpensAfterThePreviousClose()
    {
        var masterBars = MasterBars(4, i =>
            i is 1 or 3 ? """<Repeat start="false" end="true" count="2"/>""" : null);

        var unfolded = GpMasterBarSequence.Unfold(masterBars);

        Assert.Equal(new[] { 0, 1, 0, 1, 2, 3, 2, 3 }, unfolded.Select(b => b.OriginalIndex));
    }

    /// <summary>An explicit open still wins over the implicit one.</summary>
    [Fact]
    public void AnExplicitOpen_IsUnaffected()
    {
        var masterBars = MasterBars(4, i => i switch
        {
            2 => """<Repeat start="true" end="false"/>""",
            3 => """<Repeat start="false" end="true" count="2"/>""",
            _ => null
        });

        var unfolded = GpMasterBarSequence.Unfold(masterBars);

        Assert.Equal(new[] { 0, 1, 2, 3, 2, 3 }, unfolded.Select(b => b.OriginalIndex));
    }

    /// <summary>An open with no close stays straight playback — the malformed
    /// direction must not lose bars either.</summary>
    [Fact]
    public void AnOpenWithNoClose_StaysStraight()
    {
        var masterBars = MasterBars(4, i => i == 1 ? """<Repeat start="true" end="false"/>""" : null);

        Assert.Equal(new[] { 0, 1, 2, 3 }, GpMasterBarSequence.Unfold(masterBars).Select(b => b.OriginalIndex));
    }

    // ---- the GP3/4/5 BINARY path ---------------------------------------
    // It read the repeat-start bit, the end-of-repeat count and the volta byte
    // and threw all three away, so `|: 8 bars :|` imported eight bars short and
    // every later note landed early by that much.

    private static FxMeasure WholeNote(int fret, Action<FxMeasure>? configure = null)
    {
        var measure = new FxMeasure().AddVoice(FxBeat.Note(-2, stringIndex: 0, fret: fret));
        configure?.Invoke(measure);
        return measure;
    }

    private static (Arrangement Arrangement, GpImportMetadata Metadata) ImportLegacy(
        string version, IReadOnlyList<FxMeasure> measures)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version, measures);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
            return (Assert.Single(result.Arrangements), result.Metadata);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    ///     <c>|: m0 m1 :|</c> then a straight bar. Three authored measures play
    ///     as five, and the notes of the repeated pair appear TWICE — the second
    ///     time a whole section later.
    /// </summary>
    [Theory]
    [InlineData("5.10")]
    [InlineData("5.00")]
    [InlineData("4.06")]
    [InlineData("3.00")]
    public void ALegacyRepeat_UnfoldsIntoRealBars(string version)
    {
        var measures = new[]
        {
            WholeNote(1, m => m.RepeatStart = true),
            WholeNote(2, m => m.RepeatTotalPasses = 2),
            WholeNote(3)
        };

        var (arr, meta) = ImportLegacy(version, measures);

        Assert.Equal(5, meta.Bars.Count);
        var level = Assert.Single(arr.Levels);
        // 4/4 bars, so each bar start is 4 quarter-note beats on.
        Assert.Equal(
            new[] { (0, 1), (4, 2), (8, 1), (12, 2), (16, 3) },
            level.Notes.OrderBy(n => n.Position).Select(n => (n.Position.BeatId, n.Fret)));
    }

    /// <summary>
    ///     The volta byte is a bitmask over passes: <c>|: m0 m1 [1. m2] [2. m3] :|</c>
    ///     plays m2 only on pass 1 and m3 only on pass 2.
    /// </summary>
    [Theory]
    [InlineData("5.10")]
    [InlineData("4.06")]
    public void ALegacyAlternateEnding_PlaysOnItsOwnPassOnly(string version)
    {
        var (arr, meta) = ImportLegacy(version, AlternateEndingMeasures());

        Assert.Equal(6, meta.Bars.Count);
        var level = Assert.Single(arr.Levels);
        Assert.Equal(
            new[] { (0, 1), (4, 2), (8, 3), (12, 1), (16, 2), (20, 4) },
            level.Notes.OrderBy(n => n.Position).Select(n => (n.Position.BeatId, n.Fret)));
    }

    private static FxMeasure[] AlternateEndingMeasures() =>
    [
        WholeNote(1, m => m.RepeatStart = true),
        WholeNote(2),
        WholeNote(3, m => m.AlternateEndingsMask = 0b01),
        WholeNote(4, m =>
        {
            m.AlternateEndingsMask = 0b10;
            m.RepeatTotalPasses = 2;
        })
    ];

    /// <summary>
    ///     The gpif twin of the case above. Both importers unfold through
    ///     <see cref="GpMasterBarSequence" />, so they must agree on the bar
    ///     count — the binary path silently disagreeing is the whole bug.
    /// </summary>
    [Fact]
    public void TheGpifTwin_AgreesOnTheBarCount()
    {
        var legacy = ImportLegacy("5.10", AlternateEndingMeasures());
        var gpif = ImportGpifMetadata(RepeatScore());

        Assert.Equal(6, gpif.Bars.Count);
        Assert.Equal(legacy.Metadata.Bars.Count, gpif.Bars.Count);
    }

    private static string RepeatScore() =>
        """
        <GPIF>
          <Score><Title>Repeat Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Guitar</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                <Property name="CapoFret"><Fret>0</Fret></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars>
            <MasterBar><Time>4/4</Time><Bars>0</Bars><Repeat start="true" end="false" /></MasterBar>
            <MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
            <MasterBar><Time>4/4</Time><Bars>0</Bars><AlternateEndings>1</AlternateEndings></MasterBar>
            <MasterBar><Time>4/4</Time><Bars>0</Bars><AlternateEndings>2</AlternateEndings><Repeat start="false" end="true" count="2" /></MasterBar>
          </MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>0</Beats></Voice></Voices>
          <Beats><Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat></Beats>
          <Notes>
            <Note id="0"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
          </Notes>
          <Rhythms><Rhythm id="0"><NoteValue>Whole</NoteValue></Rhythm></Rhythms>
        </GPIF>
        """;

    private static GpImportMetadata ImportGpifMetadata(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gprepeat-{Guid.NewGuid():N}.gp");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Content/score.gpif");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(gpif);
        }

        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList()).Metadata;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
