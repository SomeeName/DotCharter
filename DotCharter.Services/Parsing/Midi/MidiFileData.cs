namespace DotCharter.Services.Parsing.Midi;

/// <summary>
/// Parsed Standard MIDI File (SMF format 0/1/2) — pure data, no I/O.
/// Produced by <see cref="MidiFileParser"/>.
/// </summary>
public sealed class MidiFileData
{
    public MidiFileData(int format, int ticksPerQuarterNote, IReadOnlyList<MidiTrackData> tracks,
        IReadOnlyList<MidiTempoEvent> tempoEvents, IReadOnlyList<MidiTimeSignatureEvent> timeSignatureEvents)
    {
        Format = format;
        TicksPerQuarterNote = ticksPerQuarterNote;
        Tracks = tracks;
        TempoEvents = tempoEvents;
        TimeSignatureEvents = timeSignatureEvents;
    }

    /// <summary>SMF format: 0 (single track), 1 (parallel tracks), 2 (independent sequences).</summary>
    public int Format { get; }

    /// <summary>Pulses per quarter note (PPQ). Always &gt; 0 — SMPTE division is rejected.</summary>
    public int TicksPerQuarterNote { get; }

    public IReadOnlyList<MidiTrackData> Tracks { get; }

    /// <summary>Shared tempo events for formats 0/1, tick-sorted. Format 2 uses each track's events.</summary>
    public IReadOnlyList<MidiTempoEvent> TempoEvents { get; }

    /// <summary>Shared meter events for formats 0/1, tick-sorted. Format 2 uses each track's events.</summary>
    public IReadOnlyList<MidiTimeSignatureEvent> TimeSignatureEvents { get; }
}

public sealed class MidiTrackData
{
    public MidiTrackData(string name, IReadOnlyList<MidiNoteEvent> notes,
        IReadOnlyList<MidiTempoEvent>? tempoEvents = null,
        IReadOnlyList<MidiTimeSignatureEvent>? timeSignatureEvents = null)
    {
        Name = name;
        Notes = notes;
        TempoEvents = tempoEvents ?? [];
        TimeSignatureEvents = timeSignatureEvents ?? [];
    }

    /// <summary>Track name (meta 0x03), empty when absent.</summary>
    public string Name { get; }

    /// <summary>Paired note-on/off events, sorted by start tick.</summary>
    public IReadOnlyList<MidiNoteEvent> Notes { get; }
    public IReadOnlyList<MidiTempoEvent> TempoEvents { get; }
    public IReadOnlyList<MidiTimeSignatureEvent> TimeSignatureEvents { get; }
}

/// <summary>One paired note: on-tick plus length in ticks.</summary>
public readonly record struct MidiNoteEvent(int Channel, int Pitch, int Velocity, long StartTicks, long LengthTicks);

/// <summary>Meta 0x51: tempo in microseconds per quarter note from <see cref="Ticks"/> on.</summary>
public readonly record struct MidiTempoEvent(long Ticks, int MicrosecondsPerQuarterNote)
{
    public double BeatsPerMinute => 60_000_000.0 / MicrosecondsPerQuarterNote;
}

/// <summary>Meta 0x58: time signature (denominator already expanded from its power-of-two form).</summary>
public readonly record struct MidiTimeSignatureEvent(long Ticks, int Numerator, int Denominator);
