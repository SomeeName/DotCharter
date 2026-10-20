using System.Text;

namespace DotCharter.Services.Parsing.Midi;

/// <summary>Controlled failure for malformed MIDI input — the parser never
/// lets a bad length field crash the process or drive an allocation.</summary>
public sealed class MidiParseException : Exception
{
    public MidiParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// Pure Standard MIDI File parser (formats 0 and 1; format 2 tracks are
/// parsed independently like format 1). No I/O — bytes in, data out.
///
/// Defensive by construction: every length field is validated against the
/// remaining input before it is honored, all reads are bounds-checked, and
/// nothing is ever allocated proportionally to an unvalidated length field —
/// a lying chunk size raises <see cref="MidiParseException"/> instead of an
/// OutOfMemory or a runaway loop. Running status, note-on-velocity-0-as-off
/// and missing end-of-track markers are all handled; notes left open at the
/// end of a track are closed at the track's final tick. System Common and
/// System Realtime status bytes are sized by their OWN data-byte counts (one
/// for 0xF1/0xF3, two for 0xF2, none for the rest) rather than as channel
/// messages, and only System Common cancels running status.
/// </summary>
public static class MidiFileParser
{
    private const int HeaderChunkMinLength = 6;

    public static MidiFileData Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reader = new Reader(data);

        // ---- MThd ----
        if (reader.ReadFourCc() != "MThd") throw new MidiParseException("Not a Standard MIDI File (missing MThd).");
        var headerLength = reader.ReadU32Be();
        if (headerLength < HeaderChunkMinLength || headerLength > reader.Remaining)
            throw new MidiParseException($"Bad MThd length {headerLength}.");
        var headerEnd = reader.Position + (int)headerLength;

        var format = reader.ReadU16Be();
        var declaredTracks = reader.ReadU16Be();
        var division = reader.ReadU16Be();
        if ((division & 0x8000) != 0)
            throw new MidiParseException("SMPTE time division is not supported.");
        if (division == 0) throw new MidiParseException("Ticks-per-quarter-note must be > 0.");
        reader.SeekTo(headerEnd); // tolerate an oversized header

        // ---- chunks ----
        var tracks = new List<MidiTrackData>();
        var tempoEvents = new List<MidiTempoEvent>();
        var timeSignatures = new List<MidiTimeSignatureEvent>();

        while (tracks.Count < declaredTracks && reader.Remaining >= 8)
        {
            var chunkType = reader.ReadFourCc();
            var chunkLength = reader.ReadU32Be();
            if (chunkLength > (uint)reader.Remaining)
            {
                throw new MidiParseException(
                    $"Chunk '{chunkType}' declares {chunkLength} bytes but only {reader.Remaining} remain (truncated or corrupt).");
            }

            var chunkEnd = reader.Position + (int)chunkLength;
            if (chunkType == "MTrk")
                tracks.Add(ParseTrack(reader, chunkEnd, tempoEvents, timeSignatures));
            // Unknown chunk types are skipped per the SMF spec.
            reader.SeekTo(chunkEnd);
        }

        if (tracks.Count < declaredTracks)
            throw new MidiParseException($"Header declares {declaredTracks} track(s) but only {tracks.Count} present.");

        tempoEvents.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
        timeSignatures.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
        return new MidiFileData(format, division, tracks, tempoEvents, timeSignatures);
    }

    private static MidiTrackData ParseTrack(Reader reader, int chunkEnd,
        List<MidiTempoEvent> tempoEvents, List<MidiTimeSignatureEvent> timeSignatures)
    {
        var track = new TrackBuilder();
        long tick = 0;
        var runningStatus = 0;

        while (reader.Position < chunkEnd)
        {
            tick += reader.ReadVariableLength(chunkEnd);

            var (status, data1) = ReadStatus(reader.ReadU8(chunkEnd), ref runningStatus);

            if (status == 0xFF)
            {
                if (ReadMetaEvent(reader, chunkEnd, tick, track, tempoEvents, timeSignatures))
                    return track.Finish(tick);
            }
            else if (status is 0xF0 or 0xF7)
            {
                SkipSysEx(reader, chunkEnd);
            }
            else if (status >= 0xF1)
            {
                SkipSystemMessage(reader, chunkEnd, status);
            }
            else
            {
                ReadChannelMessage(reader, chunkEnd, status, data1, tick, track);
            }
        }

        // No end-of-track meta: close whatever is still sounding at the last tick.
        return track.Finish(tick);
    }

    /// <summary>
    ///     The event's status byte and, under running status, its first data byte
    ///     (<c>-1</c> when it is still to be read).
    /// </summary>
    private static (int Status, int Data1) ReadStatus(int first, ref int runningStatus)
    {
        if (first >= 0x80)
        {
            // Channel status arms running status; System COMMON (0xF1-0xF7)
            // CANCELS it (SMF spec); System REALTIME (0xF8-0xFF) leaves it
            // alone — it may be interleaved inside a running-status run.
            if (first < 0xF0) runningStatus = first;
            else if (first < 0xF8) runningStatus = 0;
            return (first, -1);
        }

        // Running status: data byte with the previous channel status.
        if (runningStatus == 0)
            throw new MidiParseException("Data byte encountered with no running status.");
        return (runningStatus, first);
    }

    /// <summary>Reads one meta event; true at the end-of-track meta.</summary>
    private static bool ReadMetaEvent(Reader reader, int chunkEnd, long tick, TrackBuilder track,
        List<MidiTempoEvent> tempoEvents, List<MidiTimeSignatureEvent> timeSignatures)
    {
        var metaType = reader.ReadU8(chunkEnd);
        var length = reader.ReadVariableLength(chunkEnd);
        if (length > chunkEnd - reader.Position)
        {
            throw new MidiParseException(
                $"Meta event 0x{metaType:X2} declares {length} bytes past the end of its track.");
        }

        var dataStart = reader.Position;

        switch (metaType)
        {
            case 0x03 when track.Name.Length == 0:
                track.Name = reader.ReadAscii(dataStart, (int)length);
                break;
            case 0x51 when length == 3:
                var microseconds = (reader.Peek(dataStart) << 16) |
                                   (reader.Peek(dataStart + 1) << 8) |
                                   reader.Peek(dataStart + 2);
                if (microseconds > 0) tempoEvents.Add(new MidiTempoEvent(tick, microseconds));
                break;
            case 0x58 when length >= 2:
                var numerator = reader.Peek(dataStart);
                var denominatorPower = reader.Peek(dataStart + 1);
                if (numerator > 0 && denominatorPower <= 6)
                    timeSignatures.Add(new MidiTimeSignatureEvent(tick, numerator, 1 << denominatorPower));
                break;
            case 0x2F:
                return true;
        }

        reader.SeekTo(dataStart + (int)length);
        return false;
    }

    // SysEx: length-prefixed payload, skipped.
    private static void SkipSysEx(Reader reader, int chunkEnd)
    {
        var length = reader.ReadVariableLength(chunkEnd);
        if (length > chunkEnd - reader.Position)
            throw new MidiParseException($"SysEx declares {length} bytes past the end of its track.");
        reader.SeekTo(reader.Position + (int)length);
    }

    /// <summary>
    ///     System Common / System Realtime. These are NOT channel messages and they
    ///     do NOT all carry two data bytes: 0xF1 (MTC quarter frame) and 0xF3 (song
    ///     select) carry one, 0xF2 (song position) two, and everything else —
    ///     0xF4-0xF6 and every realtime byte 0xF8-0xFF — carries none. Reading two for
    ///     all of them swallowed the following event's bytes and desynced the rest of
    ///     the track.
    /// </summary>
    private static void SkipSystemMessage(Reader reader, int chunkEnd, int status)
    {
        var systemDataBytes = status switch
        {
            0xF2 => 2,
            0xF1 or 0xF3 => 1,
            _ => 0
        };
        for (var i = 0; i < systemDataBytes; i++) reader.ReadU8(chunkEnd);
    }

    private static void ReadChannelMessage(Reader reader, int chunkEnd, int status, int data1, long tick, TrackBuilder track)
    {
        var kind = status & 0xF0;
        var channel = status & 0x0F;
        if (data1 < 0) data1 = reader.ReadU8(chunkEnd);
        var dataBytes = kind is 0xC0 or 0xD0 ? 1 : 2;
        var data2 = dataBytes == 2 ? reader.ReadU8(chunkEnd) : 0;

        // 0xA0/0xB0/0xE0 (2 data bytes) and 0xC0/0xD0 (1) carry nothing we chart — consumed above.
        if (kind == 0x90 && data2 > 0)
            track.NoteOn(channel, data1, data2, tick);
        else if (kind is 0x80 or 0x90) // velocity 0 = note off
            track.NoteOff(channel, data1, tick);
    }

    /// <summary>One track's name and notes while its events are read.</summary>
    private sealed class TrackBuilder
    {
        // FIFO per (channel, pitch): a note-off closes the earliest open note-on.
        private readonly Dictionary<int, Queue<(long StartTicks, int Velocity)>> _openNotes = [];
        private readonly List<MidiNoteEvent> _notes = [];

        public string Name { get; set; } = "";

        public void NoteOn(int channel, int pitch, int velocity, long tick)
        {
            var key = (channel << 8) | pitch;
            if (!_openNotes.TryGetValue(key, out var queue))
                _openNotes[key] = queue = new Queue<(long, int)>();
            queue.Enqueue((tick, velocity));
        }

        public void NoteOff(int channel, int pitch, long tick)
        {
            var key = (channel << 8) | pitch;
            if (!_openNotes.TryGetValue(key, out var queue) || queue.Count == 0) return; // orphan note-off: ignored

            var (startTicks, velocity) = queue.Dequeue();
            _notes.Add(new MidiNoteEvent(channel, pitch, velocity, startTicks, Math.Max(0, tick - startTicks)));
        }

        public MidiTrackData Finish(long tick)
        {
            CloseOpenNotes(_openNotes, _notes, tick);
            _notes.Sort((a, b) => a.StartTicks != b.StartTicks
                ? a.StartTicks.CompareTo(b.StartTicks)
                : a.Pitch.CompareTo(b.Pitch));
            return new MidiTrackData(Name, _notes);
        }
    }

    private static void CloseOpenNotes(Dictionary<int, Queue<(long StartTicks, int Velocity)>> openNotes,
        List<MidiNoteEvent> notes, long endTick)
    {
        foreach (var (key, queue) in openNotes)
        {
            var channel = key >> 8;
            var pitch = key & 0xFF;
            while (queue.Count > 0)
            {
                var (startTicks, velocity) = queue.Dequeue();
                notes.Add(new MidiNoteEvent(channel, pitch, velocity, startTicks, Math.Max(0, endTick - startTicks)));
            }
        }

        openNotes.Clear();
    }

    /// <summary>Forward-only big-endian reader; every read is bounds-checked
    /// against the input (and optionally a chunk end) and throws
    /// <see cref="MidiParseException"/> instead of running past the data.</summary>
    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }
        public int Remaining => data.Length - Position;

        public byte ReadU8(int limit = int.MaxValue)
        {
            if (Position >= data.Length || Position >= limit)
                throw new MidiParseException("Unexpected end of MIDI data.");
            return data[Position++];
        }

        public int ReadU16Be() => (ReadU8() << 8) | ReadU8();

        public uint ReadU32Be() => ((uint)ReadU8() << 24) | ((uint)ReadU8() << 16) | ((uint)ReadU8() << 8) | ReadU8();

        public string ReadFourCc()
        {
            Span<char> chars = stackalloc char[4];
            for (var i = 0; i < 4; i++) chars[i] = (char)ReadU8();
            return new string(chars);
        }

        /// <summary>SMF variable-length quantity: at most 4 bytes (28 bits).</summary>
        public long ReadVariableLength(int limit)
        {
            long value = 0;
            for (var i = 0; i < 4; i++)
            {
                var b = ReadU8(limit);
                value = (value << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) return value;
            }

            throw new MidiParseException("Variable-length quantity longer than 4 bytes.");
        }

        public byte Peek(int position)
        {
            if (position < 0 || position >= data.Length)
                throw new MidiParseException("Unexpected end of MIDI data.");
            return data[position];
        }

        public string ReadAscii(int position, int length)
        {
            if (position < 0 || length < 0 || position + length > data.Length)
                throw new MidiParseException("Unexpected end of MIDI data.");
            return Encoding.ASCII.GetString(data, position, length);
        }

        public void SeekTo(int position)
        {
            if (position < Position || position > data.Length)
                throw new MidiParseException("Malformed MIDI data (seek out of bounds).");
            Position = position;
        }
    }
}
