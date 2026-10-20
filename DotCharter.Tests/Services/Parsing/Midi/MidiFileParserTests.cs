using DotCharter.Services.Parsing.Midi;
using static DotCharter.Tests.Services.Parsing.Midi.MidiTestBytes;

namespace DotCharter.Tests.Services.Parsing.Midi;

public sealed class MidiFileParserTests
{
    [Fact]
    public void Parse_Format0_PairsNoteOnAndOff()
    {
        var bytes = SmfFile(0, 480, Track(
            SetTempo(0, 500_000),
            NoteOn(0, 0, 64, 100),
            NoteOff(480, 0, 64),
            EndOfTrack()));

        var midi = MidiFileParser.Parse(bytes);

        Assert.Equal(0, midi.Format);
        Assert.Equal(480, midi.TicksPerQuarterNote);
        var track = Assert.Single(midi.Tracks);
        var note = Assert.Single(track.Notes);
        Assert.Equal(new MidiNoteEvent(0, 64, 100, 0, 480), note);
        var tempo = Assert.Single(midi.TempoEvents);
        Assert.Equal(new MidiTempoEvent(0, 500_000), tempo);
        Assert.Equal(120.0, tempo.BeatsPerMinute, 3);
    }

    [Fact]
    public void Parse_Format1_MergesTempoMapAcrossTracks_AndReadsTrackNames()
    {
        var bytes = SmfFile(1, 960,
            Track(
                SetTempo(0, 400_000),
                TimeSignature(0, 3, 2),
                SetTempo(960, 200_000),
                EndOfTrack()),
            Track(
                TrackName(0, "Guitar"),
                NoteOn(0, 2, 52, 90),
                NoteOff(960, 2, 52),
                EndOfTrack()));

        var midi = MidiFileParser.Parse(bytes);

        Assert.Equal(1, midi.Format);
        Assert.Equal(2, midi.Tracks.Count);
        Assert.Equal("Guitar", midi.Tracks[1].Name);
        Assert.Equal(new[] { new MidiTempoEvent(0, 400_000), new MidiTempoEvent(960, 200_000) },
            midi.TempoEvents);
        var signature = Assert.Single(midi.TimeSignatureEvents);
        Assert.Equal(new MidiTimeSignatureEvent(0, 3, 4), signature);
        var note = Assert.Single(midi.Tracks[1].Notes);
        Assert.Equal(new MidiNoteEvent(2, 52, 90, 0, 960), note);
    }

    [Fact]
    public void Parse_RunningStatus_AndNoteOnVelocityZeroAsOff()
    {
        // After the first note-on, subsequent events omit the status byte;
        // velocity-0 note-ons act as note-offs.
        var track = Track(
            NoteOn(0, 0, 60, 100),
            Ev(10, 62, 100), // running-status note-on
            Ev(10, 60, 0), //   running-status off for 60 (tick 20)
            Ev(0, 62, 0), //    running-status off for 62 (tick 20)
            EndOfTrack());

        var midi = MidiFileParser.Parse(SmfFile(0, 480, track));

        var notes = Assert.Single(midi.Tracks).Notes;
        Assert.Equal(2, notes.Count);
        Assert.Equal(new MidiNoteEvent(0, 60, 100, 0, 20), notes[0]);
        Assert.Equal(new MidiNoteEvent(0, 62, 100, 10, 10), notes[1]);
    }

    [Fact]
    public void Parse_MissingEndOfTrack_ClosesOpenNotesAtLastTick()
    {
        var track = Track(
            NoteOn(0, 0, 60, 100),
            NoteOn(480, 0, 64, 100)); // no offs, no EOT

        var midi = MidiFileParser.Parse(SmfFile(0, 480, track));

        var notes = Assert.Single(midi.Tracks).Notes;
        Assert.Equal(2, notes.Count);
        Assert.Equal(new MidiNoteEvent(0, 60, 100, 0, 480), notes[0]);
        Assert.Equal(new MidiNoteEvent(0, 64, 100, 480, 0), notes[1]);
    }

    [Fact]
    public void Parse_SkipsUnknownChunks()
    {
        var known = SmfFile(0, 480, Track(NoteOn(0, 0, 60, 100), NoteOff(10, 0, 60), EndOfTrack()));
        // Splice an unknown chunk between MThd and MTrk.
        var junk = new byte[] { (byte)'J', (byte)'U', (byte)'N', (byte)'K', 0, 0, 0, 3, 1, 2, 3 };
        var bytes = known.Take(14).Concat(junk).Concat(known.Skip(14)).ToArray();

        var midi = MidiFileParser.Parse(bytes);

        Assert.Single(Assert.Single(midi.Tracks).Notes);
    }

    [Fact]
    public void Parse_IgnoresOtherChannelMessages_AndSysEx()
    {
        var track = Track(
            Ev(0, 0xC0, 30), // program change (1 data byte)
            Ev(0, 0xB0, 7, 100), // controller (2 data bytes)
            Ev(0, 0xF0, 3, 1, 2, 0xF7), // sysex, length-prefixed
            NoteOn(0, 0, 60, 100),
            Ev(0, 0xE0, 0, 64), // pitch bend
            NoteOff(240, 0, 60),
            EndOfTrack());

        var midi = MidiFileParser.Parse(SmfFile(0, 480, track));

        var note = Assert.Single(Assert.Single(midi.Tracks).Notes);
        Assert.Equal(new MidiNoteEvent(0, 60, 100, 0, 240), note);
    }

    [Fact]
    public void Parse_TruncatedTrackChunk_ThrowsControlledException()
    {
        var bytes = new List<byte>();
        bytes.AddRange(SmfFile(0, 480)); // header declaring 0 tracks…
        bytes[11] = 1; // …patched to declare 1 track
        bytes.AddRange("MTrk"u8.ToArray());
        bytes.AddRange(new byte[] { 0, 0, 0, 100 }); // declares 100 bytes
        bytes.AddRange(new byte[] { 0, 0x90, 60, 100 }); // provides 4

        Assert.Throws<MidiParseException>(() => MidiFileParser.Parse(bytes.ToArray()));
    }

    [Fact]
    public void Parse_AbsurdChunkLength_ThrowsInsteadOfAllocating()
    {
        var bytes = new List<byte>();
        bytes.AddRange(SmfFile(0, 480));
        bytes[11] = 1;
        bytes.AddRange("MTrk"u8.ToArray());
        bytes.AddRange(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }); // ~2 GB declared
        bytes.AddRange(new byte[] { 0, 0x90, 60, 100 });

        Assert.Throws<MidiParseException>(() => MidiFileParser.Parse(bytes.ToArray()));
    }

    [Fact]
    public void Parse_AbsurdMetaLength_ThrowsControlledException()
    {
        var track = Track(
            Ev(0, 0xFF, 0x51), // tempo meta with a lying VLQ length…
            Vlq(0x0FFFFFFF), //  …of 268 MB
            [1, 2, 3],
            EndOfTrack());

        Assert.Throws<MidiParseException>(() => MidiFileParser.Parse(SmfFile(0, 480, track)));
    }

    [Fact]
    public void Parse_HeaderDeclaresMoreTracksThanPresent_Throws()
    {
        var bytes = SmfFile(1, 480, Track(EndOfTrack()));
        bytes[11] = 3; // declare 3 tracks, provide 1

        Assert.Throws<MidiParseException>(() => MidiFileParser.Parse(bytes));
    }

    [Fact]
    public void Parse_SmpteDivision_Throws()
    {
        var bytes = SmfFile(0, 0, Track(EndOfTrack()));
        bytes[12] = 0xE2; // division = SMPTE -30/80
        bytes[13] = 0x50;

        Assert.Throws<MidiParseException>(() => MidiFileParser.Parse(bytes));
    }

    [Fact]
    public void Parse_ZeroDivision_Throws() => Assert.Throws<MidiParseException>(() => MidiFileParser.Parse(SmfFile(0, 0, Track(EndOfTrack()))));

    [Fact]
    public void Parse_NotAMidiFile_Throws()
    {
        Assert.Throws<MidiParseException>(() =>
            MidiFileParser.Parse("RIFF----WAVEfmt not midi at all"u8.ToArray()));
    }

    [Fact]
    public void Parse_EmptyInput_Throws() => Assert.Throws<MidiParseException>(() => MidiFileParser.Parse([]));

    [Fact]
    public void Parse_DataByteWithoutRunningStatus_Throws()
    {
        var track = Track(
            Ev(0, 0x40, 0x40), // data byte first — no status to run with
            EndOfTrack());

        Assert.Throws<MidiParseException>(() => MidiFileParser.Parse(SmfFile(0, 480, track)));
    }
}

public sealed class MidiTempoMapTests
{
    [Fact]
    public void TicksToMs_UsesSmfDefaultTempo_WhenNoTempoEvents()
    {
        var map = new MidiTempoMap(480, Array.Empty<MidiTempoEvent>());

        Assert.Equal(0.0, map.TicksToMs(0), 6);
        Assert.Equal(500.0, map.TicksToMs(480), 6); // 120 BPM default
        Assert.Equal(1250.0, map.TicksToMs(1200), 6);
    }

    [Fact]
    public void TicksToMs_IsPiecewiseAcrossTempoChanges()
    {
        var map = new MidiTempoMap(480, new[]
        {
            new MidiTempoEvent(0, 500_000), // 120 BPM
            new MidiTempoEvent(480, 250_000) // 240 BPM from beat 1
        });

        Assert.Equal(500.0, map.TicksToMs(480), 6);
        Assert.Equal(750.0, map.TicksToMs(960), 6);
        Assert.Equal(875.0, map.TicksToMs(1200), 6);
    }

    [Fact]
    public void SuggestBeatGrid_EmitsQuarterNoteBeats_WithTempoAndMeasures()
    {
        var map = new MidiTempoMap(480, new[]
        {
            new MidiTempoEvent(0, 500_000),
            new MidiTempoEvent(480, 250_000)
        });

        var beats = map.SuggestBeatGrid(960);

        Assert.Equal(4, beats.Count); // through the end plus one spare beat
        Assert.Equal(0.0, beats[0].Time, 6);
        Assert.Equal(500.0, beats[1].Time, 6);
        Assert.Equal(750.0, beats[2].Time, 6);
        Assert.Equal(1000.0, beats[3].Time, 6);
        Assert.Equal(120.0, beats[0].Bpm, 3);
        Assert.Equal(240.0, beats[1].Bpm, 3);
        Assert.Equal(new[] { true, false, false, false }, beats.Select(b => b.IsMeasure)); // default 4/4
    }

    [Fact]
    public void SuggestBeatGrid_HonorsTimeSignature()
    {
        var map = new MidiTempoMap(480, Array.Empty<MidiTempoEvent>());
        var signatures = new[] { new MidiTimeSignatureEvent(0, 3, 4) }; // 3/4

        var beats = map.SuggestBeatGrid(480 * 7, signatures);

        Assert.True(beats.Count >= 7);
        Assert.True(beats[0].IsMeasure);
        Assert.False(beats[1].IsMeasure);
        Assert.False(beats[2].IsMeasure);
        Assert.True(beats[3].IsMeasure);
        Assert.True(beats[6].IsMeasure);
        Assert.Equal(3, beats[0].BeatsInMeasure);
    }
}

/// <summary>
///     System Common (0xF1-0xF7) and System Realtime (0xF8-0xFF) status bytes
///     are legal inside an <c>MTrk</c> event stream and do NOT have two data
///     bytes. Mis-sizing one desyncs everything after it in the track.
/// </summary>
public sealed class MidiSystemMessageParseTests
{
    /// <summary>
    ///     System Realtime bytes (0xF8-0xFF) carry NO data and may appear
    ///     anywhere in a track's event stream. Treating one as a two-data-byte
    ///     channel message swallows the next two bytes and DESYNCS the rest of
    ///     the track — every note after it lands at the wrong tick, or the
    ///     parse throws outright.
    /// </summary>
    [Theory]
    [InlineData(0xF8)] // timing clock
    [InlineData(0xFA)] // start
    [InlineData(0xFC)] // stop
    [InlineData(0xFE)] // active sensing
    public void Parse_SystemRealtimeByte_DoesNotConsumeTheFollowingEvent(int status)
    {
        var bytes = SmfFile(0, 480, Track(
            NoteOn(0, 0, 64, 100),
            NoteOff(480, 0, 64),
            RealtimeStatus(0, (byte)status),
            NoteOn(0, 0, 67, 90),
            NoteOff(480, 0, 67),
            EndOfTrack()));

        var midi = MidiFileParser.Parse(bytes);

        var track = Assert.Single(midi.Tracks);
        Assert.Equal(2, track.Notes.Count);
        Assert.Equal(new MidiNoteEvent(0, 64, 100, 0, 480), track.Notes[0]);
        Assert.Equal(new MidiNoteEvent(0, 67, 90, 480, 480), track.Notes[1]);
    }

    /// <summary>System Common with ONE data byte (0xF1 MTC quarter frame).
    /// Reading two desyncs the stream by a byte.</summary>
    [Fact]
    public void Parse_SystemCommonOneDataByte_DoesNotOverRead()
    {
        var bytes = SmfFile(0, 480, Track(
            NoteOn(0, 0, 64, 100),
            NoteOff(480, 0, 64),
            TimeCodeQuarterFrame(0, 0x21),
            NoteOn(0, 0, 67, 90),
            NoteOff(480, 0, 67),
            EndOfTrack()));

        var midi = MidiFileParser.Parse(bytes);

        var track = Assert.Single(midi.Tracks);
        Assert.Equal(2, track.Notes.Count);
        Assert.Equal(new MidiNoteEvent(0, 67, 90, 480, 480), track.Notes[1]);
    }

    /// <summary>System Common CANCELS running status (SMF spec). A data byte
    /// after one is malformed input, not a silent re-use of the last channel
    /// status — which would fabricate a note.</summary>
    [Fact]
    public void Parse_SystemCommon_CancelsRunningStatus()
    {
        var bytes = SmfFile(0, 480, Track(
            NoteOn(0, 0, 64, 100),
            SongPosition(0, 0x00, 0x01),
            Ev(480, 64, 0), // running-status data bytes with no live status
            EndOfTrack()));

        Assert.Throws<MidiParseException>(() => MidiFileParser.Parse(bytes));
    }

    /// <summary>System REALTIME does NOT cancel running status — it may be
    /// interleaved inside a running-status run.</summary>
    [Fact]
    public void Parse_SystemRealtime_KeepsRunningStatus()
    {
        var bytes = SmfFile(0, 480, Track(
            NoteOn(0, 0, 64, 100),
            RealtimeStatus(0, 0xF8),
            Ev(480, 64, 0), // running status: note-on velocity 0 = note off
            EndOfTrack()));

        var midi = MidiFileParser.Parse(bytes);

        var note = Assert.Single(Assert.Single(midi.Tracks).Notes);
        Assert.Equal(new MidiNoteEvent(0, 64, 100, 0, 480), note);
    }
}
