using System.Buffers.Binary;
using System.Text;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #777: the RS7X block's byte layout and every validation rule of the rs7string
///     mod's spec (<c>docs/sng-extension.md</c> §2, §3), on hand-built blocks. The two
///     examples of §7 are pinned byte for byte; a block that breaks any rule reads as
///     ABSENT (the reader falls back), never as half a block.
/// </summary>
public sealed partial class Rs7ExtensionBlockFormatTests
{
    /// <summary>§7, 7 strings: two templates (C#5 with string 6 fret 2 finger 1; one
    /// without string 6), one ChordNotes record whose string-6 entry is sustained.</summary>
    private const string SevenStringExample =
        "52533758 0100 1000 4e000000 03000000" +
        "53545253 0100 0100 05000000 00 07 01 00 06" +
        "43484454 0100 0100 08000000 02000000 0201 ffff" +
        "43484e54 0100 0000 0d000000 01000000 00200000 ff ff 0000 00";

    /// <summary>§7, 8 strings: one template (F#5: string 6 fret 2 finger 2, string 7
    /// open), no ChordNotes records.</summary>
    private const string EightStringExample =
        "52533758 0100 1000 46000000 03000000" +
        "53545253 0100 0100 06000000 00 08 02 00 06 07" +
        "43484454 0100 0100 08000000 01000000 0202 00ff" +
        "43484e54 0100 0000 04000000 00000000";

    public static TheoryData<string, string> BrokenBlocks => new()
    {
        { "not RS7X", "52533759" + new string('0', 24) },
        { "block size short", Convert.ToHexString(Block([Strs7()])[..^1]) },
        { "block size long", Convert.ToHexString([.. Block([Strs7()]), 0]) },
        { "version 2", "52533758 0200 1000 10000000 00000000" },
        { "header size 8", "52533758 0100 0800 10000000 00000000" },
        { "first section not STRS", Convert.ToHexString(Block([Section("CHDT", [0, 0, 0, 0], critical: true)])) },
        { "STRS twice", Convert.ToHexString(Block([Strs7(), Strs7()])) },
        { "section past the end", Convert.ToHexString(Block([Strs7()], sectionCount: 2)) },
        { "bass", Convert.ToHexString(Block([Section("STRS", [1, 7, 1, 0, 6], critical: true)])) },
        { "6 strings", Convert.ToHexString(Block([Section("STRS", [0, 6, 0, 0], critical: true)])) },
        { "8 strings on a 7-entry tuning", Convert.ToHexString(Block([Section("STRS", [0, 8, 2, 0, 6, 7], critical: true)])) },
        { "column not 6", Convert.ToHexString(Block([Section("STRS", [0, 7, 1, 0, 7], critical: true)])) },
        { "STRS trailing byte", Convert.ToHexString(Block([Section("STRS", [0, 7, 1, 0, 6, 0], critical: true)])) },
        { "CHDT count", Convert.ToHexString(Block([Strs7(), Section("CHDT", [2, 0, 0, 0, 3, 2, 3, 2], critical: true)])) },
        { "CHDT short", Convert.ToHexString(Block([Strs7(), Section("CHDT", [1, 0, 0, 0, 3], critical: true)])) },
        { "CHDT fret 25", Convert.ToHexString(Block([Strs7(), Section("CHDT", [1, 0, 0, 0, 25, 0xff], critical: true)])) },
        { "CHDT finger 5", Convert.ToHexString(Block([Strs7(), Section("CHDT", [1, 0, 0, 0, 3, 5], critical: true)])) },
        { "CHDT finger without fret", Convert.ToHexString(Block([Strs7(), Section("CHDT", [1, 0, 0, 0, 0xff, 1], critical: true)])) },
        { "CHNT count", Convert.ToHexString(Block([Strs7(), Section("CHNT", [2, 0, 0, 0, .. EmptyTech, .. EmptyTech])])) },
        { "CHNT 33 bends", Convert.ToHexString(Block([Strs7(), Section("CHNT", [1, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 0, 0, 33])])) },
        { "CHNT slide 30", Convert.ToHexString(Block([Strs7(), Section("CHNT", [1, 0, 0, 0, 0, 0x08, 0, 0, 30, 0xff, 0, 0, 0])])) },
        { "CHNT trailing byte", Convert.ToHexString(Block([Strs7(), Section("CHNT", [1, 0, 0, 0, .. EmptyTech, 0])])) }
    };

    // ---- builders ------------------------------------------------------------------

    private static RsExtensionBlock SevenStringBlock()
    {
        var block = RsExtensionBlock.ForStrings(7);
        block.Templates!.Add([new RsExtColumn(2, 1)]);
        block.Templates.Add([RsExtColumn.None]);
        block.ChordNotes!.Add([RsExtTech.Empty with { Mask = RsNoteMask.Sustain }]);
        return block;
    }

    private static RsExtensionBlock EightStringBlock()
    {
        var block = RsExtensionBlock.ForStrings(8);
        block.Templates!.Add([new RsExtColumn(2, 2), new RsExtColumn(0, -1)]);
        return block;
    }

    private static RsExtensionBlock BendBlock()
    {
        var block = RsExtensionBlock.ForStrings(7);
        block.ChordNotes!.Add([new RsExtTech(RsNoteMask.Bend, -1, -1, 0, [new RsBendValue(1.5f, 1f), new RsBendValue(1.75f, 0f)])]);
        return block;
    }

    private static readonly byte[] EmptyTech = [0, 0, 0, 0, 0xff, 0xff, 0, 0, 0];

    private static byte[] Strs7() => Section("STRS", [0, 7, 1, 0, 6], critical: true);

    private static RocksmithSng Song(int tuning, int templates, int chordNotes)
    {
        var sng = new RocksmithSng { MetaData = new RsMetaData { Tuning = new short[tuning] } };
        for (var i = 0; i < templates; i++)
            sng.Chords.Add(new RsChordTemplate(0, new sbyte[6], new sbyte[6], "x"));
        for (var i = 0; i < chordNotes; i++) sng.ChordNotes.Add(new RsChordNotes());
        return sng;
    }

    private static byte[] Section(string id, byte[] payload, bool critical = false, ushort version = 1)
    {
        var bytes = new byte[12 + payload.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), critical ? (ushort)1 : (ushort)0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)payload.Length);
        payload.CopyTo(bytes, 12);
        return bytes;
    }

    private static byte[] Block(byte[][] sections, int extraHeader = 0, int? sectionCount = null)
    {
        var header = 16 + extraHeader;
        var body = sections.SelectMany(s => s).ToArray();
        var bytes = new byte[header + body.Length];
        "RS7X"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)header);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)(sectionCount ?? sections.Length));
        body.CopyTo(bytes, header);
        return bytes;
    }

    private static byte[] Hex(string spaced) => Convert.FromHexString(spaced.Replace(" ", "", StringComparison.Ordinal));

    private static List<string> SectionIds(byte[] block) => [.. Sections(block).Select(s => s.Id)];

    private static List<int> SectionFlags(byte[] block) => [.. Sections(block).Select(s => s.Flags)];

    private static IEnumerable<(string Id, int Flags)> Sections(byte[] block)
    {
        var at = (int)BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(6));
        while (at < block.Length)
        {
            yield return (Encoding.ASCII.GetString(block, at, 4), BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 6)));
            at += 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at + 8));
        }
    }
}
