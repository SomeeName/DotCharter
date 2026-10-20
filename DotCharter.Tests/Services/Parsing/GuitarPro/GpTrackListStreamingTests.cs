using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     <c>GpImporter.GetAvailableTracks</c> populates the import dialog's track
///     picker, and it used to build the WHOLE <c>score.gpif</c> DOM — every bar,
///     voice, beat, note and rhythm of the score — to read a handful of
///     <c>&lt;Track&gt;&lt;Name&gt;</c> elements, then throw it away so that
///     <c>ImportTracksWithMetadata</c> could inflate and parse the same document
///     again when the user pressed OK (three times over, on the resync path,
///     which imports the old file as well as the new one). A large gpif inflates
///     to tens of megabytes of XML.
///     <para>
///     GPIF writes <c>&lt;Tracks&gt;</c> before the bulk sections, so the picker
///     reads it with a streaming reader and stops. Two claims: the streamed
///     answer is the DOM's answer, and the read really does stop early —
///     counted in BYTES pulled off the (decompressed) stream, which needs no
///     clock.
///     </para>
/// </summary>
public sealed class GpTrackListStreamingTests
{
    /// <summary>The projection the streaming reader replaced, verbatim, as the
    /// specification.</summary>
    private static List<(int Id, string Name)> DomTracks(string xml)
    {
        var doc = XDocument.Parse(xml);
        var result = new List<(int, string)>();
        var tracksNode = doc.Root?.Element("Tracks");
        if (tracksNode == null) return result;

        foreach (var trackNode in tracksNode.Elements("Track"))
        {
            if (int.TryParse(trackNode.Attribute("id")?.Value, CultureInfo.InvariantCulture, out var id))
            {
                var name = GpTrackNames.Normalize(trackNode.Element("Name")?.Value);
                result.Add((id, name.Length > 0 ? name : $"Track {id}"));
            }
        }

        return result;
    }

    private static List<(int Id, string Name)> Streamed(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return GpImporter.ReadTracks(stream).Select(t => (t.Id, t.Name)).ToList();
    }

    /// <summary>A gpif head shaped like the real thing: doubled spaces in a name
    /// (real GP files carry them and the dialog matches BY NAME), a track with
    /// no name, one with an empty name, one whose id is unparseable, sibling
    /// elements before and inside Track, and a nested "Name" that is NOT a
    /// direct child.</summary>
    private const string ScoreHead =
        """
        <GPIF>
          <GPRevision>7</GPRevision>
          <Score><Title>T</Title><Artist>A</Artist></Score>
          <MasterTrack><Automations><Automation><Value>120</Value></Automation></Automations></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Electric  Guitar</Name>
              <ShortName>EG</ShortName>
              <Staves><Staff><Properties><Property name="Tuning"><Name>inner</Name></Property></Properties></Staff></Staves>
            </Track>
            <Track id="1"><ShortName>B</ShortName></Track>
            <Track id="2"><Name></Name></Track>
            <Track id="not-a-number"><Name>Ignored</Name></Track>
            <Track id="4"><Name>Vocals</Name></Track>
          </Tracks>
        """;

    private static string Score(string bulk = "") => ScoreHead + "\n" + bulk + "\n</GPIF>";

    [Fact]
    public void TheStreamedTrackListIsTheOneTheDomProjectionProduced()
    {
        var xml = Score();

        var expected = DomTracks(xml);
        Assert.Equal(expected, Streamed(xml));

        // Not vacuous: the fixture really exercises the four shapes.
        Assert.Equal(4, expected.Count);
        Assert.Equal((0, "Electric Guitar"), expected[0]); // doubled space collapsed
        Assert.Equal((1, "Track 1"), expected[1]); // no <Name>
        Assert.Equal((2, "Track 2"), expected[2]); // empty <Name>
        Assert.Equal((4, "Vocals"), expected[3]); // the unparseable id was dropped
    }

    [Fact]
    public void ItStopsAtTheEndOfTracksInsteadOfReadingTheWholeScore()
    {
        // A body far larger than the head, in the position GPIF puts the bulk
        // sections: after <Tracks>.
        var bulk = new StringBuilder("<MasterBars>");
        for (var i = 0; i < 20_000; i++)
            bulk.Append("<MasterBar><Time>4/4</Time><Bars>").Append(i).Append("</Bars></MasterBar>");
        bulk.Append("</MasterBars>");

        var xml = Score(bulk.ToString());
        var bytes = Encoding.UTF8.GetBytes(xml);
        Assert.True(bytes.Length > 1_000_000, "the fixture's bulk section is too small to prove anything");

        using var counting = new CountingStream(new MemoryStream(bytes));
        var tracks = GpImporter.ReadTracks(counting);

        Assert.Equal(DomTracks(xml), tracks.Select(t => (t.Id, t.Name)).ToList());

        // The head is ~1 KB; the reader's own buffering makes the exact figure a
        // block size, not a byte count, so the claim is "a small fraction",
        // not "exactly the head". Reading the DOM consumed all of it.
        Assert.True(counting.BytesRead < bytes.Length / 4,
            $"the track read pulled {counting.BytesRead} of {bytes.Length} bytes — it did not stop at </Tracks>");
    }

    /// <summary>Counts the bytes actually pulled through, so "it stops early" is
    /// an assertion rather than a claim.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
