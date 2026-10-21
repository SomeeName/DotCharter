using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Size caps on Guitar Pro input. None of these allocates anything large:
///     the zip fixtures DECLARE a huge size in their headers (a few hundred bytes
///     on disk), and the expansion fixtures lower the cap through the internal
///     seam rather than building a quarter-million-note score.
/// </summary>
public sealed class GpSizeCapTests : IDisposable
{
    private const string MinimalScore = "<GPIF><Tracks /></GPIF>";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotcharter-gpcap-{Guid.NewGuid():N}");

    public GpSizeCapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the score entry ----------------------------------------------------

    [Fact]
    public void AScoreEntryPastTheCap_IsRefusedAsTooLarge()
    {
        var path = WriteGp(MinimalScore + new string(' ', 2000));

        var ex = Assert.Throws<ImportFileTooLargeException>(() => GpImporter.LoadScoreDocument(path, 1000, 16));

        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1000, ex.Cap);
    }

    [Fact]
    public void AScoreEntryInsideTheCap_Loads()
    {
        var path = WriteGp(MinimalScore);
        Assert.NotNull(GpImporter.LoadScoreDocument(path, 1000, 16));
    }

    [Fact]
    public void AnEntryWhoseHeaderDeclaresTwoHundredMegabytes_IsRefusedThroughThePublicEntryPoint()
    {
        var path = WriteGp(MinimalScore);
        DeclareScoreSize(path, 200u * 1024 * 1024);

        Assert.Throws<ImportFileTooLargeException>(() => new GpImporter().GetAvailableTracks(path));
        Assert.Throws<ImportFileTooLargeException>(() => new GpImporter().ImportTracksWithMetadata(path, [0]));
    }

    [Fact]
    public void TheCapsAreTheOnesTheProductShips()
    {
        Assert.Equal(128L * 1024 * 1024, ImportSizeLimits.GpScoreEntryBytes);
        Assert.Equal(1024, ImportSizeLimits.GpArchiveMaxEntries);
    }

    // ---- the member count -----------------------------------------------------

    [Fact]
    public void AnArchiveWithTooManyMembers_IsRefusedBeforeItsEntriesAreEnumerated()
    {
        var path = WriteGp(MinimalScore, extraEntries: 6);

        var ex = Assert.Throws<InvalidDataException>(() => GpImporter.LoadScoreDocument(path, 1000, 4));

        Assert.Contains("too many", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArchiveDeclaringSixtyThousandMembers_IsRefusedThroughThePublicEntryPoint()
    {
        var path = WriteGp(MinimalScore);
        var bytes = File.ReadAllBytes(path);
        var eocd = bytes.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4b, 0x05, 0x06 });
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 10, 2), 60_000);
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<InvalidDataException>(() => new GpImporter().GetAvailableTracks(path));

        Assert.Contains("too many", ex.Message, StringComparison.Ordinal);
    }

    // ---- the GP6 container ------------------------------------------------------

    [Fact]
    public void AGpxContainerPastTheCap_IsRefusedBeforeItIsRead()
    {
        var path = Path.Combine(_dir, "big.gpx");
        File.WriteAllBytes(path, new byte[4096]);

        var ex = Assert.Throws<ImportFileTooLargeException>(() => GpxContainer.ReadScore(path, 1024));

        Assert.Equal(1024, ex.Cap);
        Assert.Equal(4096, ex.Length);
    }

    // ---- repeat expansion ---------------------------------------------------------

    [Fact]
    public void RepeatsThatUnfoldPastTheBarCap_AreRefused()
    {
        // 400 bars repeated 64 times is 25,600 - past the 20,000 cap, while the
        // file itself is a few kilobytes of XML.
        var masterBars = MasterBars(400, i =>
            i == 0 ? """<Repeat start="true" end="false" />""" :
            i == 399 ? """<Repeat start="false" end="true" count="64" />""" : null);

        var ex = Assert.Throws<InvalidDataException>(() => GpMasterBarSequence.Unfold(masterBars));

        Assert.Contains("unfolds to more than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongScoreWithoutRepeats_IsNotAffected()
    {
        var masterBars = MasterBars(GpFormatBounds.MaxUnfoldedBars, _ => null);
        Assert.Equal(GpFormatBounds.MaxUnfoldedBars, GpMasterBarSequence.Unfold(masterBars).Count);
    }

    [Fact]
    public void AScoreOnePastTheBarCap_IsRefused()
    {
        var masterBars = MasterBars(GpFormatBounds.MaxUnfoldedBars + 1, _ => null);
        Assert.Throws<InvalidDataException>(() => GpMasterBarSequence.Unfold(masterBars));
    }

    // ---- notes per track ------------------------------------------------------------

    [Fact]
    public void ATrackThatEmitsMoreNotesThanTheCap_IsRefused()
    {
        var doc = XDocument.Parse(ScoreWithBeats(beatCount: 40));

        var ex = Assert.Throws<InvalidDataException>(() =>
            new GpArrangementParser { MaxNotesPerTrack = 10 }.Parse(doc, [0]));

        Assert.Contains("more than 10 notes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrackInsideTheCap_ParsesAndTheDefaultIsTheSharedBound()
    {
        var doc = XDocument.Parse(ScoreWithBeats(beatCount: 40));

        var arrangements = new GpArrangementParser { MaxNotesPerTrack = 100 }.Parse(doc, [0]);

        Assert.Single(arrangements);
        Assert.Equal(GpFormatBounds.MaxNotesPerTrack, new GpArrangementParser().MaxNotesPerTrack);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static XElement MasterBars(int count, Func<int, string?> repeatFor)
    {
        var bars = string.Concat(Enumerable.Range(0, count).Select(i =>
            $"<MasterBar><Time>4/4</Time><Bars>0</Bars>{repeatFor(i) ?? ""}</MasterBar>"));
        return XElement.Parse($"<MasterBars>{bars}</MasterBars>");
    }

    private static string ScoreWithBeats(int beatCount) => $"""
        <GPIF>
          <Score><Title>Cap Fixture</Title></Score>
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
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>{string.Join(' ', Enumerable.Repeat("0", beatCount))}</Beats></Voice></Voices>
          <Beats><Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat></Beats>
          <Notes>
            <Note id="0"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
          </Notes>
          <Rhythms><Rhythm id="0"><NoteValue>Sixteenth</NoteValue></Rhythm></Rhythms>
        </GPIF>
        """;

    private string WriteGp(string gpif, int extraEntries = 0)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.gp");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Content/score.gpif");
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                writer.Write(gpif);
            for (var i = 0; i < extraEntries; i++)
                archive.CreateEntry($"Content/extra{i}.bin");
        }

        return path;
    }

    /// <summary>Rewrites the uncompressed size of every member, in the local and
    /// the central headers, so the archive DECLARES more than it holds.</summary>
    private static void DeclareScoreSize(string path, uint size)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014b50)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24, 4), size);
            else if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x04034b50)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22, 4), size);
        }

        File.WriteAllBytes(path, bytes);
    }
}
