using System.Buffers.Binary;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro 6 <c>.gpx</c> files: a BCFS sector file system, normally BCFZ
///     compressed, holding <c>score.gpif</c>. Every <c>.gpx</c> in the tab corpus failed as
///     "not a zip". The fixtures here are SYNTHETIC — a minimal GP6-shaped gpif packed by a
///     test-side encoder that writes both BCFZ token kinds (literal runs and back references).
/// </summary>
public sealed class GpxContainerTests
{
    private const int SectorSize = 0x1000;

    /// <summary>A 5-string bass in GP6's layout: the tuning sits in
    /// <c>Track/Properties</c>, not <c>Track/Staves/Staff/Properties</c> as in GP7. The
    /// comment pads the score past one sector so the file spans several.</summary>
    private static string Gp6Score() => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <GPIF>
          <!-- {new string('x', 2 * SectorSize)} -->
          <Score><Title>GPX fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks><Track id="0"><Name>Bass</Name><Properties>
            <Property name="Tuning"><Pitches>23 28 33 38 43</Pitches></Property>
          </Properties></Track></Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
            <MasterBar><Time>4/4</Time><Bars>1</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar><Bar id="1"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>0 1 2 3</Beats></Voice></Voices><!-- both bars play voice 0 -->
          <Beats>
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
            <Beat id="3"><Rhythm ref="0" /><Notes>3</Notes></Beat>
          </Beats>
          <Notes>{Note(0, 0, 3)}{Note(1, 1, 5)}{Note(2, 2, 7)}{Note(3, 4, 2)}</Notes>
          <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
        </GPIF>
        """;

    private static string Note(int id, int str, int fret) => $"""
        <Note id="{id}"><Properties>
          <Property name="String"><String>{str}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
        </Properties></Note>
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGp6File_ImportsItsScore(bool compressed)
    {
        var fileSystem = Bcfs(("misc.xml", "<Misc/>"u8.ToArray()), ("score.gpif", Encoding.UTF8.GetBytes(Gp6Score())));
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpx-{Guid.NewGuid():N}.gpx");
        File.WriteAllBytes(path, compressed ? Bcfz(fileSystem) : fileSystem);
        try
        {
            var importer = new GpImporter();
            var track = Assert.Single(importer.GetAvailableTracks(path));
            Assert.Equal("Bass", track.Name);

            var result = importer.ImportTracksWithMetadata(path, [track.Id]);

            var bass = Assert.Single(result.Arrangements);
            Assert.Equal(5, bass.StringCount);
            Assert.Equal([-5, -5, -5, -5, -4], bass.TuningPitches.Select(t => t.Step)); // standard B E A D G
            Assert.Equal([3, 5, 7, 2, 3, 5, 7, 2], bass.Levels[0].Notes.OrderBy(n => n.Position).Select(n => n.Fret));
            Assert.Equal(2, result.Metadata.Bars.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The decoder reproduces the encoder's input exactly, over both token kinds.</summary>
    [Fact]
    public void Decompress_InvertsTheEncoder()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"<Beat id=\"{i % 37}\"/>")));

        Assert.Equal(data, GpxContainer.Decompress(Bcfz(data)));
    }

    /// <summary>Hostile containers end as <see cref="InvalidDataException" /> (or no score),
    /// never as an out-of-range read or a runaway allocation.</summary>
    [Fact]
    public void AHostileContainer_IsInvalidData()
    {
        // A back reference before the start of the output.
        var bits = new BitWriter();
        bits.Msb(1, 1);
        bits.Msb(4, 4);
        bits.Lsb(9, 4);
        bits.Lsb(3, 4);
        Assert.Throws<InvalidDataException>(() => GpxContainer.Decompress(Header(100, bits.ToArray())));

        // A declared length far past any score.
        Assert.Throws<InvalidDataException>(() => GpxContainer.Decompress(Header(int.MaxValue, [])));

        // Not a file system once inflated.
        Assert.Throws<InvalidDataException>(() => GpxContainer.ReadFile(Bcfz("JUNKJUNK"u8.ToArray()), "score.gpif"));

        // A file entry whose sector list points past the end: no data, no crash.
        var fileSystem = Bcfs(("score.gpif", [1, 2, 3]));
        BinaryPrimitives.WriteInt32LittleEndian(fileSystem.AsSpan(4 + SectorSize + 0x94), 999);
        Assert.Empty(GpxContainer.ReadFile(fileSystem, "score.gpif")!);
    }

    // ---- the test-side container writer ----------------------------------

    /// <summary>"BCFS" + a header sector, then per file one entry sector followed by its
    /// data sectors.</summary>
    private static byte[] Bcfs(params (string Name, byte[] Data)[] files)
    {
        var sectors = new List<byte[]> { new byte[SectorSize] };
        foreach (var (name, data) in files)
        {
            var entry = new byte[SectorSize];
            BinaryPrimitives.WriteInt32LittleEndian(entry, 2);
            Encoding.UTF8.GetBytes(name).CopyTo(entry, 0x04);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(0x8C), data.Length);
            sectors.Add(entry);
            var pointer = 0x94;
            for (var start = 0; start < data.Length; start += SectorSize)
            {
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(pointer), sectors.Count);
                pointer += 4;
                var sector = new byte[SectorSize];
                data.AsSpan(start, Math.Min(SectorSize, data.Length - start)).CopyTo(sector);
                sectors.Add(sector);
            }
        }

        return [.. "BCFS"u8.ToArray(), .. sectors.SelectMany(s => s)];
    }

    /// <summary>A greedy BCFZ encoder: a back reference for any run of 4+ bytes seen in the
    /// last 32 KiB, literal runs of up to 3 bytes otherwise.</summary>
    private static byte[] Bcfz(byte[] data)
    {
        var bits = new BitWriter();
        var literals = new List<byte>();

        void FlushLiterals()
        {
            for (var i = 0; i < literals.Count; i += 3)
            {
                var count = Math.Min(3, literals.Count - i);
                bits.Msb(0, 1);
                bits.Lsb(count, 2);
                for (var k = 0; k < count; k++) bits.Msb(literals[i + k], 8);
            }

            literals.Clear();
        }

        var position = 0;
        while (position < data.Length)
        {
            var (bestOffset, bestLength) = (0, 0);
            for (var offset = 1; offset <= Math.Min(position, 0x7FFF); offset++)
            {
                var length = 0;
                while (length < offset && length < 0x7FFF && position + length < data.Length &&
                       data[position + length - offset] == data[position + length])
                {
                    length++;
                }

                if (length > bestLength) (bestOffset, bestLength) = (offset, length);
                if (bestLength >= 64) break;
            }

            if (bestLength >= 4)
            {
                FlushLiterals();
                bits.Msb(1, 1);
                bits.Msb(15, 4);
                bits.Lsb(bestOffset, 15);
                bits.Lsb(bestLength, 15);
                position += bestLength;
            }
            else
            {
                literals.Add(data[position++]);
            }
        }

        FlushLiterals();
        return Header(data.Length, bits.ToArray());
    }

    private static byte[] Header(int length, byte[] stream)
    {
        var header = new byte[8];
        "BCFZ"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), length);
        return [.. header, .. stream];
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _used = 8;

        private void Bit(int bit)
        {
            if (_used == 8)
            {
                _bytes.Add(0);
                _used = 0;
            }

            if (bit != 0) _bytes[^1] |= (byte)(0x80 >> _used);
            _used++;
        }

        public void Msb(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--) Bit((value >> i) & 1);
        }

        public void Lsb(int value, int count)
        {
            for (var i = 0; i < count; i++) Bit((value >> i) & 1);
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
