using System.Text;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Services.Export.Sng;

/// <summary>Writes positional, N-column SNXR charts from the shared slot-order SNG model.</summary>
internal static class RsxrSngWriter
{
    public static void WritePlain(RocksmithSng sng, Stream output)
    {
        if (sng.Extension == null)
        {
            RocksmithSngWriter.WritePlainPc(sng, output);
            return;
        }

        output.Write(SngPacker.PackPlain(WriteUnpacked(sng)));
        output.Flush();
    }

    public static byte[] WriteUnpacked(RocksmithSng sng)
    {
        var extension = sng.Extension ?? throw new ArgumentException("An SNXR chart requires an extended arrangement.", nameof(sng));
        var type = extension.Instrument == RsExtensionBlock.InstrumentBass ? ArrangementType.Bass : ArrangementType.Lead;
        var slots = Enumerable.Range(0, extension.StringCount)
            .Select(i => RsxrStringLayout.SlotOf(i, extension.StringCount, type)).ToArray();
        var templateRow = 0;
        var techniqueRow = 0;
        sbyte StringIndex(sbyte slot) => slot == -1 ? slot : (sbyte)RsxrStringLayout.StringOf(slot, extension.StringCount, type);
        sbyte StringMask(sbyte mask)
        {
            var result = 0;
            for (var i = 0; i < slots.Length; i++)
                if (((byte)mask & (1 << slots[i])) != 0) result |= 1 << i;
            return unchecked((sbyte)result);
        }

        var records = new RocksmithSngWriter.RecordWriters(
            (writer, template) => WriteTemplate(writer, template, sng, slots, templateRow++),
            (writer, techniques) => WriteTechniques(writer, techniques, extension, slots, techniqueRow++),
            (writer, section) => RocksmithSngWriter.WriteSection(writer, section, StringMask),
            (writer, level) => RocksmithSngWriter.WriteLevel(writer, level, StringIndex),
            (writer, meta) => RocksmithSngWriter.WriteMetaData(writer, meta, [.. slots.Select(slot => meta.Tuning[slot])]));
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
        {
            writer.Write("SNXR"u8);
            writer.Write((byte)RsxrStringLayout.FormatVersion);
            writer.Write(extension.Instrument);
            writer.Write((byte)extension.StringCount);
            writer.Write((byte)0);
            RocksmithSngWriter.WriteBody(writer, sng, records);
            var padding = 256 + Math.Max(0, 6 - slots.Length) * (6 * sng.Chords.Count + 396 * sng.ChordNotes.Count + 2);
            writer.Write(new byte[padding]);
        }

        return memory.ToArray();
    }

    private static RsExtColumn Column(RsExtensionBlock extension, int row, int slot) =>
        extension.Templates is { } templates ? templates[row][extension.ColumnOf(slot)] : RsExtColumn.None;

    private static void WriteTemplate(BinaryWriter writer, RsChordTemplate chord, RocksmithSng sng, int[] slots, int row)
    {
        var extension = sng.Extension!;
        var pitch = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = extension.StringCount };
        foreach (var slot in slots) pitch.TuningPitches.Add(new TuningOffset(sng.MetaData.Tuning[slot]));
        writer.Write(chord.Mask);
        foreach (var slot in slots)
            writer.Write(slot < 6 ? Value(chord.Frets, slot, (sbyte)-1) : Column(extension, row, slot).Fret);
        foreach (var slot in slots)
            writer.Write(slot < 6 ? Value(chord.Fingers, slot, (sbyte)-1) : Column(extension, row, slot).Finger);
        foreach (var slot in slots)
        {
            var fret = slot < 6 ? -1 : Column(extension, row, slot).Fret;
            writer.Write(slot < 6 ? Value(chord.Notes, slot, 0)
                : fret < 0 ? -1 : SoundingPitch.MidiFor(pitch,
                    RsxrStringLayout.StringOf(slot, extension.StringCount, ArrangementType.Lead), fret) ?? -1);
        }

        RocksmithSngWriter.WriteFixedString(writer, chord.Name, 32);
    }

    private static T Value<T>(T[] values, int index, T fallback) => index < values.Length ? values[index] : fallback;

    private static void WriteTechniques(BinaryWriter writer, RsChordNotes chord, RsExtensionBlock extension, int[] slots, int row)
    {
        var columns = slots.Select(slot => slot < 6
            ? new RsExtTech(chord.Mask[slot], chord.SlideTo[slot], chord.SlideUnpitchTo[slot], chord.Vibrato[slot], chord.BendData[slot])
            : extension.ChordNotes is { } techniques ? techniques[row][extension.ColumnOf(slot)] : RsExtTech.Empty).ToArray();
        foreach (var column in columns) writer.Write(column.Mask);
        foreach (var column in columns) RocksmithSngWriter.WriteBendData32(writer, column.Bends);
        foreach (var column in columns) writer.Write(column.SlideTo);
        foreach (var column in columns) writer.Write(column.SlideUnpitchTo);
        foreach (var column in columns) writer.Write(column.Vibrato);
    }
}
