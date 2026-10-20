namespace DotCharter.Tone.Audio.Midi;

/// <summary>Where the preview's MIDI shorts go. A negative <c>renderFrame</c> means "as soon as possible".</summary>
internal interface IMidiEventSink
{
    void Send(byte status, byte data1, byte data2, long renderFrame);
}

/// <summary>
///     The engine's output render clock at the tick: <see cref="Frame" /> is the next sample the
///     device will render (<c>JuceAudioEngine.TryGetRenderClock</c>), and the tick's position is
///     the song time of that very sample (the RAW playhead), on the preview's axis.
/// </summary>
internal readonly record struct MidiRenderAnchor(long Frame, int SampleRate);

/// <summary>
///     An Ample-style articulation keyswitch to press before a note: every key in
///     <see cref="ClearLow" />..<see cref="ClearHigh" /> except <see cref="Note" /> is released,
///     then <see cref="Note" /> is pressed, all on <see cref="Channel" />.
/// </summary>
internal readonly record struct MidiKeyswitch(byte Channel, byte Note, byte Velocity, byte ClearLow, byte ClearHigh);

/// <summary>
/// Every timing decision of the MIDI chart preview, as a pure type: which chart
///     span is due this tick, the output frame each NoteOn/NoteOff lands on, and the note
///     bookkeeping that keeps seeks, loops, pauses and speed changes from leaving a hanging or a
///     doubled note. <c>JuceAudioPlaybackService.UpdateMidiNotes</c> drives it from the ~4 ms
///     position tick: <see cref="BeginTick" />, then <see cref="NoteOn" /> for every note whose
///     start is in (<see cref="WindowStartMs" />, <see cref="WindowEndMs" />], then
///     <see cref="EndTick" />. All times are chart milliseconds on the preview's axis (raw
///     playhead − MIDI offset + instrument latency).
///     <para>
///         <b>Two modes, chosen per tick by whether the caller has a render anchor.</b>
///         SAMPLE-ACCURATE (a VSTi in the engine, in-process or sandboxed — the sandbox bridge
///         carries each event's sample offset since bridge v3): the window reaches <see cref="LookaheadMs" />
///         of WALL time ahead of the playhead, and each event carries the absolute frame
///         <c>anchor + (t − now) / speed × rate</c> for <c>dca_engine_send_midi_at</c>, which parks
///         it until its block — so neither the timer's jitter nor the block size moves a note.
///         IMMEDIATE (the WinMM / GS Wavetable device, which has no render clock): the window
///         ends at the playhead and every event is "now" (frame −1).
///     </para>
///     <para>
///         <b>What makes a jump safe.</b> A jump (a seek, a loop wrap, the first tick after a
///         pause or stop: the playhead moved back more than <see cref="JumpBackMs" /> or forward
///         more than <see cref="JumpForwardMs" />) or an explicit <see cref="Flush" /> turns every
///         sounding note off and sends All Sound Off now. That cannot reach a NoteOn already
///         PARKED in the engine for a future frame, so every scheduled NoteOn is remembered
///         until its frame has safely passed and a jump answers each with a NoteOff one frame
///         after it. A later NoteOn on the same key is never placed at or before the last NoteOff
///         sent for that key, so a guard can never cut the note that follows it. The window
///         never moves backwards (a speed drop shrinks the lookahead), so no note is sent twice.
///     </para>
/// </summary>
internal sealed class MidiPreviewScheduler
{
    /// <summary>Wall-clock lead of a scheduled event over its block — several ~4 ms ticks plus timer jitter.</summary>
    public const double LookaheadMs = 30.0;

    public const double JumpBackMs = 50.0;
    public const double JumpForwardMs = 500.0;

    /// <summary>The shortest note: a zero-sustain note still fires its envelope.</summary>
    public const double MinHoldMs = 30.0;

    public const int Channels = 16;

    /// <summary>A scheduled NoteOn is remembered this long past its frame: a LATE event (the engine
    /// places it at the head of the block that drains it) can still be guarded.</summary>
    private const double TrackSlackMs = 100.0;

    private const int MaxTrackedOns = 256;

    private readonly Dictionary<(int Channel, int Note), Active> _active = new(64);
    private readonly List<(int Channel, int Note)> _expired = new(32);
    private readonly int[] _channelNote = new int[Channels];
    private readonly long[] _channelOnFrame = new long[Channels];
    private readonly long[] _lastOffFrame = new long[Channels * 128];
    private readonly TrackedOn[] _tracked = new TrackedOn[MaxTrackedOns];
    private int _trackedCount;

    private double _lastNowMs = double.NaN;
    private double _scannedToMs;
    private double? _resumeAtMs;
    private double _nowMs;
    private double _speed = 1.0;
    private MidiRenderAnchor _anchor;

    /// <summary>
    ///     Whether the preview may schedule on the engine's render clock: the sink is the engine's
    ///     instrument bus AND an instrument is there to play it — in-process, or in the sandbox,
    ///     whose bridge has carried each event's sample offset (bridge v3).
    ///     A device outside the engine (WinMM / GS Wavetable) has no render clock.
    /// </summary>
    public static bool SchedulesOnRenderClock(bool inProcessInstrument, bool sandboxInstrument, IMidiOutput sink)
        => (inProcessInstrument || sandboxInstrument) && sink is JuceInstrumentMidiOutput;

    public MidiPreviewScheduler()
    {
        Array.Fill(_channelNote, -1);
        Array.Fill(_lastOffFrame, long.MinValue);
    }

    /// <summary>Exclusive lower bound of this tick's due span.</summary>
    public double WindowStartMs { get; private set; }

    /// <summary>Inclusive upper bound of this tick's due span.</summary>
    public double WindowEndMs { get; private set; }

    public bool IsSampleAccurate { get; private set; }

    /// <summary>Notes sounding (or scheduled to) whose NoteOff has not been sent.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>The note held on <paramref name="channel" /> by a per-channel-mono instrument, or −1.</summary>
    public int ChannelNote(int channel) => _channelNote[channel];

    /// <summary>
    ///     Opens a tick at <paramref name="nowMs" />. Returns true on a JUMP: everything sounding
    ///     has been turned off and the caller must rewind its scan cursors.
    /// </summary>
    public bool BeginTick(IMidiEventSink sink, double nowMs, double speed, MidiRenderAnchor? anchor)
    {
        _speed = speed > 0.0 && double.IsFinite(speed) ? speed : 1.0;
        _nowMs = nowMs;
        IsSampleAccurate = anchor is { SampleRate: > 0 } && anchor.Value.Frame >= 0;
        if (IsSampleAccurate) _anchor = anchor!.Value;

        var jump = double.IsNaN(_lastNowMs) || nowMs < _lastNowMs - JumpBackMs || nowMs > _lastNowMs + JumpForwardMs;
        if (jump)
        {
            FlushCore(sink);
            // A known resume point (the seek target) makes a note exactly ON it due, as long as
            // the transport really is there; otherwise resume at the playhead.
            _scannedToMs = _resumeAtMs is { } resume && nowMs >= resume - JumpBackMs && nowMs <= resume + JumpForwardMs
                ? Math.Min(resume, nowMs) - 1e-3
                : nowMs;
        }

        _resumeAtMs = null;
        _lastNowMs = nowMs;
        if (IsSampleAccurate) PruneTracked();

        var horizon = IsSampleAccurate ? nowMs + LookaheadMs * _speed : nowMs;
        WindowStartMs = _scannedToMs;
        WindowEndMs = Math.Max(horizon, _scannedToMs);
        return jump;
    }

    /// <summary>
    ///     Starts a note. <paramref name="monoPerChannel" />: the instrument plays one note per
    ///     channel (one channel per string), so a different note on the channel is released
    ///     first.
    /// </summary>
    public void NoteOn(IMidiEventSink sink, int channel, int note, int velocity, double startMs, double endMs,
        bool monoPerChannel, MidiKeyswitch? keyswitch = null)
    {
        channel &= 0x0F;
        note &= 0x7F;
        var accurate = IsSampleAccurate;
        // One frame is kept free in front of the NoteOn for its releases and its keyswitch.
        var on = accurate ? Math.Max(FrameFor(startMs), _anchor.Frame + 1) : -1L;
        if (accurate) on = Math.Max(on, _lastOffFrame[Key(channel, note)] + 1);

        if (monoPerChannel)
        {
            var prev = _channelNote[channel];
            if (prev >= 0 && prev != note)
            {
                on = Release(sink, channel, prev, on, _channelOnFrame[channel]);
                _active.Remove((channel, prev));
            }
        }

        if (_active.TryGetValue((channel, note), out var held))
            on = Release(sink, channel, note, on, held.OnFrame);

        if (keyswitch is { } ks)
        {
            var ksFrame = accurate ? on - 1 : -1L;
            var ksStatus = (byte)(0x90 | (ks.Channel & 0x0F));
            for (int k = ks.ClearLow; k <= ks.ClearHigh; k++)
            {
                if (k != ks.Note) sink.Send(ksStatus, (byte)k, 0, ksFrame);
            }

            sink.Send(ksStatus, ks.Note, ks.Velocity, ksFrame);
        }

        sink.Send((byte)(0x90 | channel), (byte)note, (byte)Math.Clamp(velocity, 1, 127), on);
        _active[(channel, note)] = new Active(Math.Max(endMs, startMs + MinHoldMs), on);
        if (monoPerChannel)
        {
            _channelNote[channel] = note;
            _channelOnFrame[channel] = on;
        }

        if (accurate && _trackedCount < MaxTrackedOns) _tracked[_trackedCount++] = new TrackedOn(channel, note, on);
    }

    /// <summary>Closes the tick: every note whose end is due is released (at its own frame when sample-accurate).</summary>
    public void EndTick(IMidiEventSink sink)
    {
        if (_active.Count > 0)
        {
            _expired.Clear();
            foreach (var kv in _active)
            {
                if (kv.Value.EndMs <= WindowEndMs) _expired.Add(kv.Key);
            }

            for (var i = 0; i < _expired.Count; i++)
            {
                var (channel, note) = _expired[i];
                var a = _active[(channel, note)];
                var off = IsSampleAccurate ? Math.Max(Math.Max(FrameFor(a.EndMs), a.OnFrame + 1), _anchor.Frame) : -1L;
                SendOff(sink, channel, note, off);
                _active.Remove((channel, note));
                if (_channelNote[channel] == note) _channelNote[channel] = -1;
            }
        }

        _scannedToMs = WindowEndMs;
    }

    /// <summary>
    ///     Pause, stop, seek or a sink swap: everything off now, every parked NoteOn guarded, and
    ///     the next tick is a jump. <paramref name="resumeAtMs" /> is where the transport will
    ///     continue (the seek target), so a note exactly there still sounds.
    /// </summary>
    public void Flush(IMidiEventSink sink, double? resumeAtMs = null)
    {
        FlushCore(sink);
        _lastNowMs = double.NaN;
        _resumeAtMs = resumeAtMs;
    }

    /// <summary>An instrument was attached: its channels hold nothing yet.</summary>
    public void ForgetChannelNotes() => Array.Fill(_channelNote, -1);

    private void FlushCore(IMidiEventSink sink)
    {
        foreach (var key in _active.Keys) SendOff(sink, key.Channel, key.Note, -1);
        _active.Clear();
        for (var ch = 0; ch < Channels; ch++) sink.Send((byte)(0xB0 | ch), 120, 0, -1);

        // A NoteOn still parked for a future frame would sound AFTER the All Sound Off above and
        // hang: answer each with a NoteOff one frame after it.
        for (var i = 0; i < _trackedCount; i++)
        {
            var t = _tracked[i];
            SendOff(sink, t.Channel, t.Note, t.Frame + 1);
        }

        _trackedCount = 0;
        Array.Fill(_channelNote, -1);
    }

    /// <summary>Releases a held key in front of a NoteOn at <paramref name="on" />; returns the (possibly later) NoteOn frame.</summary>
    private long Release(IMidiEventSink sink, int channel, int note, long on, long heldOnFrame)
    {
        if (!IsSampleAccurate)
        {
            SendOff(sink, channel, note, -1);
            return on;
        }

        var off = Math.Max(on - 1, heldOnFrame + 1);
        SendOff(sink, channel, note, off);
        return Math.Max(on, off + 1);
    }

    private void SendOff(IMidiEventSink sink, int channel, int note, long frame)
    {
        sink.Send((byte)(0x80 | channel), (byte)note, 0, frame);
        if (frame >= 0)
        {
            ref var last = ref _lastOffFrame[Key(channel, note)];
            if (frame > last) last = frame;
        }
    }

    private long FrameFor(double ms)
    {
        var frames = (ms - _nowMs) / _speed * _anchor.SampleRate / 1000.0;
        return Math.Max(_anchor.Frame + (long)Math.Round(frames), _anchor.Frame);
    }

    private void PruneTracked()
    {
        var keepFrom = _anchor.Frame - (long)(TrackSlackMs * _anchor.SampleRate / 1000.0);
        for (var i = _trackedCount - 1; i >= 0; i--)
        {
            if (_tracked[i].Frame < keepFrom) _tracked[i] = _tracked[--_trackedCount];
        }
    }

    private static int Key(int channel, int note) => (channel * 128) + note;

    private readonly record struct Active(double EndMs, long OnFrame);

    private readonly record struct TrackedOn(int Channel, int Note, long Frame);
}
