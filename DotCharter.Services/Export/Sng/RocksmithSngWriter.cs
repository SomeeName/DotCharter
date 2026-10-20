using System.Security.Cryptography;
using System.Text;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Infrastructure.Psarc;

namespace DotCharter.Services.Export.Sng;

/// <summary>
/// Serializes a <see cref="RocksmithSng"/> back into the Rocksmith 2014 SNG
/// binary format (PC platform) — the exact inverse of the package reader's
/// SNG parser. Section order, padding bytes and unknown
/// fields follow Rocksmith2014.NET's Rocksmith2014.SNG type writers.
/// </summary>
internal static class RocksmithSngWriter
{
    /// <summary>
    /// Writes the packed (zlib + AES-CTR encrypted) PC SNG:
    /// magic 0x4A, header 3, 16-byte IV, encrypted (plain length prefix +
    /// zlib payload), and 56 trailing zero bytes the game ignores.
    /// The container is <see cref="SngPacker.Pack" />. Needs the user's PC SNG key
    /// (<see cref="RocksmithKeys" />).
    /// </summary>
    /// <param name="sng">The parsed SNG structure to serialize.</param>
    /// <param name="output">Destination stream the packed SNG bytes are written to.</param>
    /// <param name="iv">Counter IV; random when null. Tests pass a fixed IV
    /// for deterministic output.</param>
    public static void WritePackedPc(RocksmithSng sng, Stream output, byte[]? iv = null) =>
        WritePacked(sng, output, RocksmithKeys.Current?.SngPcKey
                                 ?? throw new InvalidOperationException("No SNG key: write a plain SNG (WritePlainPc)."), iv);

    /// <summary>
    /// The Mac SNG: the same body and container as <see cref="WritePackedPc" />, under
    /// the Mac SNG key. Both platforms are little-endian (Rocksmith2014.NET BinaryWriters.fs
    /// <c>getWriter</c>: <c>PC | Mac</c>); a Mac package has no plain form.
    /// </summary>
    public static void WritePackedMac(RocksmithSng sng, Stream output, byte[]? iv = null) =>
        WritePacked(sng, output, RocksmithKeys.Current?.SngMacKey
                                 ?? throw new InvalidOperationException("No Mac SNG key: a Mac package cannot be written."), iv);

    private static void WritePacked(RocksmithSng sng, Stream output, byte[] key, byte[]? iv)
    {
        iv ??= RandomNumberGenerator.GetBytes(16);
        output.Write(SngPacker.Pack(WriteUnpacked(sng), key, iv));
        output.Flush();
    }

    /// <summary>
    /// Writes the PLAIN PC SNG that the CDLC enabler's plain-PSARC patch
    /// loads (<see cref="SngPacker.PackPlain" />): no key is involved. Byte for byte what
    /// <c>rs7chart</c> and the enabler's <c>PlainPsarc.cpp</c> describe.
    /// </summary>
    public static void WritePlainPc(RocksmithSng sng, Stream output)
    {
        output.Write(SngPacker.PackPlain(WriteUnpacked(sng)));
        output.Flush();
    }

    /// <summary>Serializes the plain (unencrypted, uncompressed) SNG body.</summary>
    internal static byte[] WriteUnpacked(RocksmithSng sng)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
        {
            WriteArray(writer, sng.Beats, WriteBeat);
            WriteArray(writer, sng.Phrases, WritePhrase);
            WriteArray(writer, sng.Chords, WriteChordTemplate);
            WriteArray(writer, sng.ChordNotes, WriteChordNotes);

            WriteArray(writer, sng.Vocals, WriteVocal);
            if (sng.Vocals.Count > 0)
            {
                WriteArray(writer, sng.SymbolsHeaders, WriteSymbolsHeader);
                WriteArray(writer, sng.SymbolsTextures, WriteSymbolsTexture);
                WriteArray(writer, sng.SymbolDefinitions, WriteSymbolDefinition);
            }

            WriteArray(writer, sng.PhraseIterations, WritePhraseIteration);
            WriteArray(writer, sng.PhraseExtraInfos, WritePhraseExtraInfo);
            WriteArray(writer, sng.NewLinkedDifficulties, WriteNewLinkedDifficulty);
            WriteArray(writer, sng.Actions, WriteAction);

            WriteArray(writer, sng.Events, WriteEvent);
            WriteArray(writer, sng.Tones, WriteTone);

            WriteArray(writer, sng.Dnas, WriteDna);

            WriteArray(writer, sng.Sections, WriteSection);
            WriteArray(writer, sng.Levels, WriteLevel);
            WriteMetaData(writer, sng.MetaData);
            // RS7X (7+ strings): inside the body, after MetaData, where the game stops reading.
            if (sng.Extension != null) writer.Write(RsExtensionBlockWriter.Serialize(sng.Extension));
        }

        return memory.ToArray();
    }

    private static void WriteBeat(BinaryWriter writer, RsBeat beat)
    {
        writer.Write(beat.Time);
        writer.Write(beat.Measure);
        writer.Write(beat.Beat);
        writer.Write(beat.PhraseIteration);
        writer.Write(beat.Mask);
    }

    private static void WritePhrase(BinaryWriter writer, RsPhrase phrase)
    {
        writer.Write(phrase.Solo);
        writer.Write(phrase.Disparity);
        writer.Write(phrase.Ignore);
        writer.Write((sbyte)0); // padding
        writer.Write(phrase.MaxDifficulty);
        writer.Write(phrase.IterationCount);
        WriteFixedString(writer, phrase.Name, 32);
    }

    private static void WriteChordTemplate(BinaryWriter writer, RsChordTemplate chord)
    {
        writer.Write(chord.Mask);
        WriteSBytes(writer, chord.Frets, 6, -1);
        WriteSBytes(writer, chord.Fingers, 6, -1);
        for (var i = 0; i < 6; i++)
            writer.Write(i < chord.Notes.Length ? chord.Notes[i] : 0);
        WriteFixedString(writer, chord.Name, 32);
    }

    private static void WriteChordNotes(BinaryWriter writer, RsChordNotes chordNotes)
    {
        for (var i = 0; i < 6; i++) writer.Write(chordNotes.Mask[i]);
        for (var i = 0; i < 6; i++) WriteBendData32(writer, chordNotes.BendData[i]);
        for (var i = 0; i < 6; i++) writer.Write(chordNotes.SlideTo[i]);
        for (var i = 0; i < 6; i++) writer.Write(chordNotes.SlideUnpitchTo[i]);
        for (var i = 0; i < 6; i++) writer.Write(chordNotes.Vibrato[i]);
    }

    private static void WriteBendData32(BinaryWriter writer, List<RsBendValue> bendValues)
    {
        var count = Math.Min(bendValues.Count, 32);
        for (var i = 0; i < 32; i++)
            WriteBendValue(writer, i < count ? bendValues[i] : new RsBendValue(0f, 0f));
        writer.Write(count);
    }

    private static void WriteBendValue(BinaryWriter writer, RsBendValue bend)
    {
        writer.Write(bend.Time);
        writer.Write(bend.Step);
        writer.Write(0); // unknown, always zero
    }

    private static void WriteVocal(BinaryWriter writer, RsVocal vocal)
    {
        writer.Write(vocal.Time);
        writer.Write(vocal.Note);
        writer.Write(vocal.Length);
        WriteFixedString(writer, vocal.Lyric, 48);
    }

    private static void WriteSymbolsHeader(BinaryWriter writer, RsSymbolsHeader header)
    {
        writer.Write(header.Id);
        writer.Write(header.Unk2);
        writer.Write(header.Unk3);
        writer.Write(header.Unk4);
        writer.Write(header.Unk5);
        writer.Write(header.Unk6);
        writer.Write(header.Unk7);
        writer.Write(header.Unk8);
    }

    private static void WriteSymbolsTexture(BinaryWriter writer, RsSymbolsTexture texture)
    {
        WriteFixedString(writer, texture.Font, 128);
        writer.Write(texture.FontPathLength);
        writer.Write(0); // unknown, always zero
        writer.Write(texture.Width);
        writer.Write(texture.Height);
    }

    private static void WriteSymbolDefinition(BinaryWriter writer, RsSymbolDefinition definition)
    {
        WriteFixedString(writer, definition.Symbol, 12);
        for (var i = 0; i < 4; i++) writer.Write(definition.Outer[i]);
        for (var i = 0; i < 4; i++) writer.Write(definition.Inner[i]);
    }

    private static void WritePhraseIteration(BinaryWriter writer, RsPhraseIteration iteration)
    {
        writer.Write(iteration.PhraseId);
        writer.Write(iteration.StartTime);
        writer.Write(iteration.EndTime);
        for (var i = 0; i < 3; i++)
            writer.Write(i < iteration.Difficulty.Length ? iteration.Difficulty[i] : 0);
    }

    private static void WritePhraseExtraInfo(BinaryWriter writer, RsPhraseExtraInfo info)
    {
        writer.Write(info.PhraseId);
        writer.Write(info.Difficulty);
        writer.Write(info.Empty);
        writer.Write(info.LevelJump);
        writer.Write(info.Redundant);
        writer.Write((sbyte)0); // padding
    }

    private static void WriteNewLinkedDifficulty(BinaryWriter writer, RsNewLinkedDifficulty nld)
    {
        writer.Write(nld.LevelBreak);
        writer.Write(nld.Phrases.Length);
        foreach (var phrase in nld.Phrases) writer.Write(phrase);
    }

    private static void WriteAction(BinaryWriter writer, RsAction action)
    {
        writer.Write(action.Time);
        WriteFixedString(writer, action.Name, 256);
    }

    private static void WriteEvent(BinaryWriter writer, RsEvent rsEvent)
    {
        writer.Write(rsEvent.Time);
        WriteFixedString(writer, rsEvent.Name, 256);
    }

    private static void WriteTone(BinaryWriter writer, RsTone tone)
    {
        writer.Write(tone.Time);
        writer.Write(tone.ToneId);
    }

    private static void WriteDna(BinaryWriter writer, RsDna dna)
    {
        writer.Write(dna.Time);
        writer.Write(dna.DnaId);
    }

    private static void WriteSection(BinaryWriter writer, RsSection section)
    {
        WriteFixedString(writer, section.Name, 32);
        writer.Write(section.Number);
        writer.Write(section.StartTime);
        writer.Write(section.EndTime);
        writer.Write(section.StartPhraseIterationId);
        writer.Write(section.EndPhraseIterationId);
        WriteSBytes(writer, section.StringMask, 36, 0);
    }

    private static void WriteLevel(BinaryWriter writer, RsLevel level)
    {
        writer.Write(level.Difficulty);
        WriteArray(writer, level.Anchors, WriteAnchor);
        WriteArray(writer, level.AnchorExtensions, WriteAnchorExtension);
        WriteArray(writer, level.HandShapes, WriteFingerPrint);
        WriteArray(writer, level.Arpeggios, WriteFingerPrint);
        WriteArray(writer, level.Notes, WriteNote);
        writer.Write(level.AverageNotesPerIteration.Length);
        foreach (var value in level.AverageNotesPerIteration) writer.Write(value);
        writer.Write(level.NotesInPhraseIterationsExclIgnored.Length);
        foreach (var value in level.NotesInPhraseIterationsExclIgnored) writer.Write(value);
        writer.Write(level.NotesInPhraseIterationsAll.Length);
        foreach (var value in level.NotesInPhraseIterationsAll) writer.Write(value);
    }

    private static void WriteAnchor(BinaryWriter writer, RsAnchor anchor)
    {
        writer.Write(anchor.StartTime);
        writer.Write(anchor.EndTime);
        writer.Write(anchor.FirstNoteTime);
        writer.Write(anchor.LastNoteTime);
        writer.Write(anchor.Fret);
        writer.Write((byte)0); // 3 bytes padding
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(anchor.Width);
        writer.Write(anchor.PhraseIterationId);
    }

    private static void WriteAnchorExtension(BinaryWriter writer, RsAnchorExtension extension)
    {
        writer.Write(extension.BeatTime);
        writer.Write(extension.Fret);
        writer.Write(0); // unknown, always zero
        writer.Write((short)0); // unknown, always zero
        writer.Write((sbyte)0); // unknown, always zero
    }

    private static void WriteFingerPrint(BinaryWriter writer, RsFingerPrint fingerPrint)
    {
        writer.Write(fingerPrint.ChordId);
        writer.Write(fingerPrint.StartTime);
        writer.Write(fingerPrint.EndTime);
        writer.Write(fingerPrint.FirstNoteTime);
        writer.Write(fingerPrint.LastNoteTime);
    }

    private static void WriteNote(BinaryWriter writer, RsNote note)
    {
        writer.Write(note.Mask);
        writer.Write(note.Flags);
        writer.Write(note.Hash);
        writer.Write(note.Time);
        writer.Write(note.StringIndex);
        writer.Write(note.Fret);
        writer.Write(note.AnchorFret);
        writer.Write(note.AnchorWidth);
        writer.Write(note.ChordId);
        writer.Write(note.ChordNotesId);
        writer.Write(note.PhraseId);
        writer.Write(note.PhraseIterationId);
        writer.Write(note.FingerPrintId0);
        writer.Write(note.FingerPrintId1);
        writer.Write(note.NextIterNote);
        writer.Write(note.PrevIterNote);
        writer.Write(note.ParentPrevNote);
        writer.Write(note.SlideTo);
        writer.Write(note.SlideUnpitchTo);
        writer.Write(note.LeftHand);
        writer.Write(note.Tap);
        writer.Write(note.PickDirection);
        writer.Write(note.Slap);
        writer.Write(note.Pluck);
        writer.Write(note.Vibrato);
        writer.Write(note.Sustain);
        writer.Write(note.MaxBend);
        writer.Write(note.BendData.Count);
        foreach (var bend in note.BendData) WriteBendValue(writer, bend);
    }

    private static void WriteMetaData(BinaryWriter writer, RsMetaData metaData)
    {
        writer.Write(metaData.MaxScore);
        writer.Write(metaData.MaxNotesAndChords);
        writer.Write(metaData.MaxNotesAndChordsReal);
        writer.Write(metaData.PointsPerNote);
        writer.Write(metaData.FirstBeatLength);
        writer.Write(metaData.StartTime);
        writer.Write(metaData.CapoFretId);
        WriteFixedString(writer, metaData.LastConversionDateTime, 32);
        writer.Write(metaData.Part);
        writer.Write(metaData.SongLength);
        writer.Write(metaData.Tuning.Length);
        foreach (var tuning in metaData.Tuning) writer.Write(tuning);
        // FirstNoteTime appears twice, always the same value.
        writer.Write(metaData.FirstNoteTime);
        writer.Write(metaData.FirstNoteTime);
        writer.Write(metaData.MaxDifficulty);
    }

    private static void WriteArray<T>(BinaryWriter writer, List<T> items, Action<BinaryWriter, T> write)
    {
        writer.Write(items.Count);
        foreach (var item in items) write(writer, item);
    }

    private static void WriteSBytes(BinaryWriter writer, IReadOnlyList<sbyte> values, int count, sbyte fill)
    {
        for (var i = 0; i < count; i++)
            writer.Write(i < values.Count ? values[i] : fill);
    }

    /// <summary>Writes a zero-terminated UTF-8 string into a fixed-size field,
    /// truncating so at least one zero terminator always remains.
    /// Reference: Rocksmith2014.NET BinaryHelpers.fs `writeZeroTerminatedUTF8String`.</summary>
    private static void WriteFixedString(BinaryWriter writer, string value, int length)
    {
        var buffer = new byte[length];
        var utf8 = Encoding.UTF8.GetBytes(value);
        var copyLength = Math.Min(utf8.Length, length - 1);
        Array.Copy(utf8, buffer, copyLength);
        writer.Write(buffer);
    }
}
