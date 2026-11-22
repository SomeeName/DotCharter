using System.Buffers.Binary;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrSngWriterTests
{
    [Theory]
    [InlineData(false, 7, 6, new short[] { 7, 1, 2, 3, 4, 5, 6 })]
    [InlineData(false, 8, 7, new short[] { 8, 7, 1, 2, 3, 4, 5, 6 })]
    [InlineData(true, 5, 4, new short[] { 5, 1, 2, 3, 4 })]
    [InlineData(true, 6, 4, new short[] { 5, 1, 2, 3, 4, 6 })]
    public void Small_chart_pins_header_lowest_note_section_bit_tuning_and_padding(bool bass, int strings, int lowSlot, short[] expectedTuning)
    {
        var sng = new RocksmithSng
        {
            Extension = bass ? RsExtensionBlock.ForBass(strings) : RsExtensionBlock.ForStrings(strings),
            MetaData = new RsMetaData { Tuning = [.. Enumerable.Range(1, bass ? 6 : strings).Select(i => (short)i)] }
        };
        var level = new RsLevel();
        level.Notes.Add(new RsNote { StringIndex = (sbyte)lowSlot, Fret = 3 });
        level.Notes.Add(new RsNote { StringIndex = -1, ChordId = 0 });
        sng.Levels.Add(level);
        var mask = new sbyte[36];
        mask[0] = unchecked((sbyte)(1 << lowSlot));
        sng.Sections.Add(new RsSection("riff", 1, 0, 5) { StringMask = mask });
        var bytes = RsxrSngWriter.WriteUnpacked(sng);
        Assert.Equal(new byte[] { 83, 78, 88, 82, 1, bass ? (byte)1 : (byte)0, (byte)strings, 0 }, bytes[..8]);
        var layout = RsxrSngLayout.Walk(bytes, strings, 8);
        Assert.Equal(0, bytes[layout.NoteStrings[0].Start]);
        Assert.Equal(255, bytes[layout.NoteStrings[1].Start]);
        Assert.Equal(1, bytes[layout.Sections[0].Start + 52]);
        Assert.Equal(strings, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(layout.Tuning.Start)));
        Assert.Equal(expectedTuning, Enumerable.Range(0, strings).Select(i =>
            BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(layout.Tuning.Start + 4 + 2 * i))));
        var padding = 256 + Math.Max(0, 6 - strings) * 2;
        Assert.Equal(layout.End + padding, bytes.Length);
        Assert.All(bytes[layout.End..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 4)]
    public void Stock_plain_charts_are_byte_identical(bool bass, int strings)
    {
        var built = RsxrTestSupport.Chart(bass, strings);
        var sng = SngFromDomainMapper.BuildInstrumental(built.Project.Arrangements[0], built.Project.Beats,
            built.SongLengthMs, RsxrTestSupport.ConversionTime).Sng;
        using var expected = new MemoryStream();
        using var actual = new MemoryStream();
        RocksmithSngWriter.WritePlainPc(sng, expected);
        RsxrSngWriter.WritePlain(sng, actual);
        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.Throws<ArgumentException>(() => RsxrSngWriter.WriteUnpacked(sng));
    }
}
