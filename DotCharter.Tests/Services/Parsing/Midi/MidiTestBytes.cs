namespace DotCharter.Tests.Services.Parsing.Midi;

/// <summary>Hand-builds Standard MIDI File byte arrays for parser tests.</summary>
internal static class MidiTestBytes
{
    public static byte[] SmfFile(int format, int division, params byte[][] trackChunks)
    {
        var bytes = new List<byte>();
        bytes.AddRange("MThd"u8.ToArray());
        AddU32(bytes, 6);
        AddU16(bytes, format);
        AddU16(bytes, trackChunks.Length);
        AddU16(bytes, division);
        foreach (var track in trackChunks)
        {
            bytes.AddRange("MTrk"u8.ToArray());
            AddU32(bytes, (uint)track.Length);
            bytes.AddRange(track);
        }

        return bytes.ToArray();
    }

    public static byte[] Track(params byte[][] events) => events.SelectMany(e => e).ToArray();

    public static byte[] Vlq(long value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        value >>= 7;
        while (value > 0)
        {
            bytes.Insert(0, (byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        return bytes.ToArray();
    }

    public static byte[] Ev(long delta, params byte[] payload) => Vlq(delta).Concat(payload).ToArray();

    public static byte[] NoteOn(long delta, int channel, int pitch, int velocity) => Ev(delta, (byte)(0x90 | channel), (byte)pitch, (byte)velocity);

    public static byte[] NoteOff(long delta, int channel, int pitch) => Ev(delta, (byte)(0x80 | channel), (byte)pitch, 0);

    public static byte[] SetTempo(long delta, int microsecondsPerQuarterNote)
    {
        return Ev(delta, 0xFF, 0x51, 0x03,
            (byte)(microsecondsPerQuarterNote >> 16),
            (byte)(microsecondsPerQuarterNote >> 8),
            (byte)microsecondsPerQuarterNote);
    }

    public static byte[] TimeSignature(long delta, int numerator, int denominatorPower) => Ev(delta, 0xFF, 0x58, 0x04, (byte)numerator, (byte)denominatorPower, 24, 8);

    public static byte[] TrackName(long delta, string name)
    {
        var ascii = System.Text.Encoding.ASCII.GetBytes(name);
        return Vlq(delta).Concat(new byte[] { 0xFF, 0x03 }).Concat(Vlq(ascii.Length)).Concat(ascii).ToArray();
    }

    /// <summary>A System Realtime status byte (0xF8-0xFF): NO data bytes at
    /// all, and legal anywhere in a track's event stream.</summary>
    public static byte[] RealtimeStatus(long delta, byte status) => Ev(delta, status);

    /// <summary>MIDI Time Code quarter frame (0xF1): exactly ONE data byte.</summary>
    public static byte[] TimeCodeQuarterFrame(long delta, byte value) => Ev(delta, 0xF1, value);

    /// <summary>Song position pointer (0xF2): exactly TWO data bytes.</summary>
    public static byte[] SongPosition(long delta, byte lsb, byte msb) => Ev(delta, 0xF2, lsb, msb);

    public static byte[] EndOfTrack(long delta = 0) => Ev(delta, 0xFF, 0x2F, 0x00);

    private static void AddU16(List<byte> bytes, int value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void AddU32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}
