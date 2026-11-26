namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>Reads one GP3-5 chord diagram from the beat stream and returns its normalised name;
/// the diagram's frets and fingering are skipped (the binary reader takes neither).</summary>
internal static class GpLegacyChordDiagram
{
    /// <summary>
    ///     Consumes the beat's chord diagram block and returns the arranger's
    ///     chord NAME (null when the diagram carries none).
    ///     <para>
    ///     It is not a <c>SkipChordDiagram</c> — a skip would only keep the
    ///     beat stream in sync, so the name would be read and dropped on
    ///     the floor and <see cref="ChordHandShapeBuilder" /> would rename every
    ///     imported chord from <c>ChordNameSuggester</c>'s pitch-class guess. The
    ///     byte layout is the one whose 5-byte
    ///     name→base-fret gap, read as 4, desyncs the whole beat stream.
    ///     </para>
    /// </summary>
    public static string? Read(GpLegacyBinaryReader reader, int stringCount)
    {
        string? name;
        var format = reader.ReadByte();
        if (format == 0)
        {
            // Old (GP3-style) diagram: name + base fret, then — only when the
            // base fret is non-zero — one fret int per TRACK string (EOF's
            // gp_import.c reads string-count ints, not a fixed 6).
            name = reader.ReadStringInt();
            var firstFret = reader.ReadInt32();
            if (firstFret != 0)
                for (var s = 0; s < Math.Clamp(stringCount, 1, 7); s++) reader.ReadInt32();
        }
        else
        {
            // GP4/GP5 "new format": 106 bytes total following the format byte
            // (107 including it) — the layout EOF's gp_import.c:3071-3181 and
            // Charter's GP5FileReader.readVersion500Chord both consume. The
            // 5-byte gap after the name (2 unknown + tonality of the fifth/
            // ninth/eleventh) must be skipped as 5: skipping it as 4 desyncs the beat
            // stream by one byte at every chord diagram.
            reader.Skip(16);                  // sharp flag, root, type, extension, bass, tonality
            name = reader.ReadStringByte(21); // chord name (1 length byte + 20 chars)
            reader.Skip(5);                   // 2 unknown + fifth/ninth/eleventh tonality
            reader.ReadInt32();               // base fret
            reader.Skip(7 * 4);               // fret per string
            reader.Skip(16);                  // barre count + 5 barre frets/starts/stops
            reader.Skip(8);                   // 7 interval/omission bytes + 1 unknown
            reader.Skip(7);                   // fingering per string
            reader.ReadByte();                // display-fingering flag
        }

        return GpAuthoredChordNames.Normalize(name);
    }
}
