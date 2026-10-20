// Partial: MIDI routing/synth — see JuceAudioPlaybackService.cs for the shared state and locking rules.
using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    private void ApplyMidiDevice(int deviceIndex)
    {
        if (!WinMMMidiOutput.IsSupported) return;
        lock (_midiGate)
        {
            _midiDeviceIndex = deviceIndex;
            // While an internal VSTi is attached (in-proc or in the sandbox
            // child), MIDI is routed there; don't tear it down — just
            // remember the desired WinMM index for when it is detached.
            if (_instrumentPlugin != null || _instrumentReloadPending || _sandboxInstrumentLoaded) return;
            if (_midi is WinMMMidiOutput existing && existing.IsOpen && existing.DeviceIndex == deviceIndex)
                return;
            try { _midi.Dispose(); } catch { /* ignored */ }
            _midi = new NullMidiOutput();
            try
            {
                var sink = new WinMMMidiOutput(deviceIndex);
                sink.Open();
                _midi = sink;
                _logger?.Info(AppLogCategory.Audio, $"MIDI output opened (device index={deviceIndex}).");
                if (_midiProgram >= 0)
                {
                    try { _midi.ProgramChange(0, _midiProgram); _midi.ChannelVolume(0, 100); } catch { /* ignored */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"Failed to open MIDI output device {deviceIndex}: {ex.Message}");
            }
        }
    }

    // Send the per-channel init burst that Ample Guitar / general GM
    // instruments expect: program change (clean electric guitar), CC7
    // volume 127, RPN pitch-bend range 12 semitones. Called under _midiGate.
    private void SetupInstrumentChannelsLocked(IMidiOutput sink)
    {
        if (_instrumentChannelsSetup) return;
        _instrumentChannelsSetup = true;
        for (var ch = 0; ch < 16; ch++)
        {
            try
            {
                sink.ProgramChange(ch, 27);   // GM 28 - electric guitar (clean)
                sink.ChannelVolume(ch, 127);
                // RPN: Pitch Bend Range = 12 semitones.
                _engine?.SendMidi((byte)(0xB0 | ch), 101, 0);
                _engine?.SendMidi((byte)(0xB0 | ch), 100, 0);
                _engine?.SendMidi((byte)(0xB0 | ch), 6, 12);
            }
            catch { /* ignored */ }
        }
    }

    // Maps a chart string index to a MIDI channel for the instrument VSTi.
    // Ample Guitar splits articulations per channel and expects strings on
    // channels 0..5 in reverse order (string 0 = high E / channel 5 in
    // some configs; original code used `5 - stringIndex`).
    private int StringChannelForInstrument(int stringIndex)
    {
        var s = ((stringIndex % 6) + 6) % 6;
        return _instrumentIsAmpleGuitar ? 5 - s : s;
    }

    // Send Ample Guitar's articulation keyswitch on its dedicated MIDI
    // channel (16, status 0x9F). No-op for non-Ample instruments. Map taken
    // from the AGSC / AG main-panel manual (Yamaha note naming where
    // middle C = C3, so C0 = MIDI 24):
    //   23 (B-1) = Pinch Harmonic
    //   24 (C0)  = Sustain / Pop
    //   25 (C#0) = Natural Harmonic (vel<127) / Artificial Harmonic (vel=127)
    //   26 (D0)  = Palm Mute   (lower velocity = deeper mute)
    //   27 (D#0) = Slide In / Slide Out
    //   28 (E0)  = Legato Slide
    //   29 (F0)  = Hammer-On / Pull-Off
    //   30 (F#0) = Slide Guitar
    // There is no dedicated "fully muted note" keyswitch � we fall back to
    // Palm Mute at minimum velocity so the chord still chunks rather than
    // ringing out.
    private const byte AmpleKsPinch = 23;
    private const byte AmpleKsSustain = 24;
    private const byte AmpleKsNatural = 25;
    private const byte AmpleKsPalmMute = 26;
    private const byte AmpleKsLast = 30;

    // The keyswitch is handed to the scheduler, which presses it in front of its
    // note — one frame ahead when sample-accurate, just before it otherwise.
    // Every OTHER key in B-1..F#0 (23..30) is released so a latched
    // articulation doesn't bleed into this note; the chosen key is simply
    // pressed again (Ample latches on NoteOn), which keeps a release/press pair
    // of the same key off one frame, where nothing guarantees their order.
    private MidiKeyswitch? AmpleArticulationKeyswitch(MuteType mute, HarmonicType harmonic = HarmonicType.None)
    {
        if (!_instrumentIsAmpleGuitar || _engine == null) return null;

        byte ksNote;
        byte ksVel = 100;
        if (harmonic == HarmonicType.Pinch)
        {
            ksNote = AmpleKsPinch;
        }
        else if (harmonic == HarmonicType.Normal)
        {
            ksNote = AmpleKsNatural;
        }
        else
        {
            switch (mute)
            {
                case MuteType.Palm:
                    ksNote = AmpleKsPalmMute;
                    // Per AGSC: keyswitch velocity controls mute depth (lower =
                    // deeper). Mid velocity -> medium palm-mute depth.
                    ksVel = 64;
                    break;
                case MuteType.Full:
                    // No dedicated full-mute keyswitch; use the deepest possible
                    // palm mute (vel 1). The played NoteOn velocity is also
                    // floored in TriggerNote so no audible pitch sneaks through.
                    ksNote = AmpleKsPalmMute;
                    ksVel = 1;
                    break;
                default:
                    ksNote = AmpleKsSustain;
                    break;
            }
        }

        return new MidiKeyswitch(15, ksNote, ksVel, AmpleKsPinch, AmpleKsLast);
    }

    // Back-compat shim � older call sites pass no articulation.
    // (Removed: SendAmpleSustainKeyswitch — no callers remain.)

    public void SetMidiInstrument(int program)
    {
        _midiProgram = program;
        try
        {
            lock (_midiGate)
            {
                _midi.ProgramChange(0, Math.Clamp(program, 0, 127));
                _midi.ChannelVolume(0, 100);
            }
        }
        catch (Exception ex) { _logger?.Error(AppLogCategory.Audio, "MIDI ProgramChange failed.", ex); }
    }

    // Every voice off + All Sound Off on all 16 channels, and every NoteOn still
    // parked in the engine for a future frame answered with a NoteOff
    // (MidiPreviewScheduler.Flush). Called from Pause/Stop/SeekTo so the
    // instrument VST doesn't ring out after the user hits pause or scrubs the
    // playhead. resumeAtRawMs is where the transport continues (a seek target)
    // on the RAW axis, so a note exactly there still sounds.
    private void FlushMidiActiveNotes(double? resumeAtRawMs = null)
    {
        try
        {
            lock (_midiGate)
            {
                _previewSink.Target = _midi is null or NullMidiOutput ? null : _midi;
                _midiScheduler.Flush(_previewSink, resumeAtRawMs + (_instrumentLatencyMs - MidiOffsetMs));
            }
        }
        catch { /* ignored */ }
    }

    // ---- 250 Hz scan state --------------------------------------------------
    // UpdateMidiNotes runs on the 4 ms position-timer thread at up to 250 Hz
    // whenever MIDI/VST preview is enabled. It must not rebuild the legato
    // index, allocate tuning offsets and walk EVERY chart note/chord per tick
    // (O(total chart) + allocations at 250 Hz). The chart collections are
    // Position-sorted (Core invariant — the same one the snapshot binary
    // search relies on), so monotonic scan cursors cover the trigger scan;
    // the legato index is cached with a 250 ms TTL (same pattern as
    // ChartRenderer.FillGhostBuffers) so position-only edits can't stay stale.
    // WHEN each event sounds is MidiPreviewScheduler's (pure, tested); this
    // method only finds the due notes and prices them.
    private int _midiNoteScanIndex;
    private int _midiChordScanIndex;
    private object? _midiScanNotesRef;
    private object? _midiScanChordsRef;
    private int _midiScanNotesCount;
    private int _midiScanChordsCount;
    private NoteLegatoIndex? _legatoCache;
    private long _legatoBuiltTick;
    private const long LegatoRefreshIntervalMs = 250;
    private readonly MidiStringPitches _midiPitches = new();
    private readonly MidiPreviewScheduler _midiScheduler = new();
    private readonly PreviewMidiSink _previewSink = new();

    public void UpdateMidiNotes(double rawPlayheadMs, IList<GuitarNote> activeChartNotes,
        IList<Chord> activeChartChords, Arrangement currentArrangement, double bpm, IList<BeatMarker> beats,
        IList<HandShape>? handShapes = null)
    {
        IMidiOutput midi;
        lock (_midiGate) { midi = _midi; }
        if (midi is null or NullMidiOutput || !midi.IsOpen) return;

        // The RAW axis (see IAudioPlaybackService.UpdateMidiNotes): the song time
        // the engine is writing INTO the output, so a note leaves in the block
        // that carries its song audio. _instrumentLatencyMs is ADDED, which fires
        // the note that much EARLY — the plugin then delays its own output by
        // exactly that, so the sound still lands on the raw block carrying the
        // song audio for the note.
        var axisShiftMs = _instrumentLatencyMs - MidiOffsetMs;
        var nowMs = rawPlayheadMs + axisShiftMs;

        // SAMPLE-ACCURATE for an instrument inside the engine: each
        // event is placed on the engine's render clock (dca_engine_send_midi_at,
        // ABI v34) a short lookahead ahead, instead of "now" from this ~4 ms
        // timer, which was late by up to a buffer plus a tick and jittered note
        // to note. That holds for the out-of-process SANDBOX too (bridge v3):
        // the engine parks a scheduled event until its block
        // and the shared mapping carries its sample offset to the child.
        // Only WinMM / GS Wavetable stays IMMEDIATE — a device outside the
        // engine, with no render clock to place anything on.
        MidiRenderAnchor? anchor = null;
        if (MidiPreviewScheduler.SchedulesOnRenderClock(_instrumentPlugin != null, _sandboxInstrumentLoaded, midi)
            && _engine is { } engine && TryReadRenderAnchor(engine, out var frame, out var rawAtFrameMs))
        {
            var sr = engine.SampleRate;
            if (sr > 0)
            {
                nowMs = rawAtFrameMs + axisShiftMs;
                anchor = new MidiRenderAnchor(frame, sr);
            }
        }

        // Collection swap (arrangement/level change) or add/remove edit —
        // rescan from the start; the cursor advance below lands right again.
        if (!ReferenceEquals(_midiScanNotesRef, activeChartNotes) || _midiScanNotesCount != activeChartNotes.Count)
        {
            _midiScanNotesRef = activeChartNotes;
            _midiScanNotesCount = activeChartNotes.Count;
            _midiNoteScanIndex = 0;
            _legatoCache = null;
        }

        if (!ReferenceEquals(_midiScanChordsRef, activeChartChords) || _midiScanChordsCount != activeChartChords.Count)
        {
            _midiScanChordsRef = activeChartChords;
            _midiScanChordsCount = activeChartChords.Count;
            _midiChordScanIndex = 0;
            _legatoCache = null;
        }

        _midiPitches.Refresh(currentArrangement);
        var capo = currentArrangement.Capo;

        // Build a per-string sorted index of upcoming note START times so each
        // triggered note can extend its NoteOff up to the next attack on the
        // same string (legato / handshape sustain). Cheap to build per call —
        // typical chart has well under a few thousand notes — but not at
        // 250 Hz, so it is cached and rebuilt at most every 250 ms.
        var nowTick = Environment.TickCount64;
        if (_legatoCache == null || nowTick - _legatoBuiltTick >= LegatoRefreshIntervalMs)
        {
            _legatoCache = NoteLegatoIndex.Build(activeChartNotes, activeChartChords, beats);
            _legatoBuiltTick = nowTick;
        }

        var legato = _legatoCache;
        // Minimum sustain for non-accented notes = one eighth note (= 30_000/bpm ms).
        // Real guitarists let notes ring; staccato is reserved for *accented* notes
        // (Rocksmith's "accent" flag, which we treat as the staccato articulation).
        var eighthMs = bpm > 0 ? 30_000.0 / bpm : 250.0;
        const double legatoEndPadMs = 8.0;   // gap so the next NoteOn isn't on top of an active NoteOff
        const double staccatoMs = 90.0;  // short attack-only length used for accented notes

        try
        {
            lock (_midiGate)
            {
                // The sink was swapped (instrument attach/detach) since it was read:
                // the swap flushed, and the next tick starts on the new sink.
                if (!ReferenceEquals(midi, _midi)) return;
                _previewSink.Target = midi;

                // Seek / loop wrap / first tick after a pause: everything off, rescan.
                if (_midiScheduler.BeginTick(_previewSink, nowMs, _playbackSpeed, anchor))
                {
                    _midiNoteScanIndex = 0;
                    _midiChordScanIndex = 0;
                }

                var fromMs = _midiScheduler.WindowStartMs;
                var toMs = _midiScheduler.WindowEndMs;

                // Single guitar notes — cursor over the Position-sorted list:
                // skip everything already triggered, stop at the first note
                // past the window. Same shape as the coordinator's clap scan.
                {
                    while (_midiNoteScanIndex < activeChartNotes.Count &&
                           activeChartNotes[_midiNoteScanIndex].GetTimeMs(beats) <= fromMs)
                    {
                        _midiNoteScanIndex++;
                    }

                    for (var i = _midiNoteScanIndex; i < activeChartNotes.Count; i++)
                    {
                        var n = activeChartNotes[i];
                        var startMs = n.GetTimeMs(beats);
                        if (startMs > toMs) break;
                        var midiNote = _midiPitches.MidiFor(n.StringIndex, n.Fret, capo);
                        if (midiNote > 0)
                        {
                            var baseEnd = (n.Position + n.Sustain).GetPositionMs(beats);
                            var endMs = ComputeNoteEnd(startMs, baseEnd, n.StringIndex, n.Accent,
                                handShapes, beats, legato, eighthMs, legatoEndPadMs, staccatoMs);
                            var velocity = n.Accent ? 120 : 100;
                            TriggerNote(n.StringIndex, n.Mute, n.Harmonic, midiNote, velocity, startMs, endMs);
                        }
                    }
                }

                // Chords — same cursor pattern.
                {
                    while (_midiChordScanIndex < activeChartChords.Count &&
                           activeChartChords[_midiChordScanIndex].Position.GetPositionMs(beats) <= fromMs)
                    {
                        _midiChordScanIndex++;
                    }

                    for (var i = _midiChordScanIndex; i < activeChartChords.Count; i++)
                    {
                        var c = activeChartChords[i];
                        var startMs = c.Position.GetPositionMs(beats);
                        if (startMs > toMs) break;
                        var cns = c.ChordNotes;
                        for (var j = 0; j < cns.Count; j++)
                        {
                            var cn = cns[j];
                            var midiNote = _midiPitches.MidiFor(cn.StringIndex, cn.Fret, capo);
                            if (midiNote > 0)
                            {
                                var baseEnd = (c.Position + cn.Sustain).GetPositionMs(beats);
                                var endMs = ComputeNoteEnd(startMs, baseEnd, cn.StringIndex, c.Accent,
                                    handShapes, beats, legato, eighthMs, legatoEndPadMs, staccatoMs);
                                var velocity = c.Accent ? 120 : 100;
                                TriggerNote(cn.StringIndex, cn.Mute, cn.Harmonic, midiNote, velocity, startMs, endMs);
                            }
                        }
                    }
                }

                // Release every note whose tail is due (allocation-free).
                _midiScheduler.EndTick(_previewSink);
            }
        }
        catch (Exception ex) { _logger?.Error(AppLogCategory.Audio, "MIDI update failed.", ex); }
    }

    // Compute the effective NoteOff time.
    //
    // Accented notes (Rocksmith "accent" flag) are treated as staccato: a short,
    // punchy attack with no extension. All other notes ring like a real guitar:
    //   * Minimum sustain of one eighth note (so notes never sound cut off).
    //   * If a next attack on the same string lands before that minimum, hold
    //     until just before it (legato).
    //   * If the note sits inside a handshape, hold until the next note in the
    //     same handshape or the handshape's end, whichever comes first.
    // Returns the larger of the authored sustain end and the computed hold.
    internal static double ComputeNoteEnd(double startMs, double baseEndMs, int stringIndex, bool accent,
        IList<HandShape>? handShapes, IList<BeatMarker> beats, NoteLegatoIndex legato,
        double eighthMs, double padMs, double staccatoMs)
    {
        // Accented = staccato. Use the authored sustain if any, otherwise a
        // short staccato length. Never extend an accented note past its release.
        if (accent)
        {
            var staccatoEnd = startMs + staccatoMs;
            return Math.Max(baseEndMs, staccatoEnd);
        }

        // Start with at least the authored sustain or an eighth note, whichever is longer.
        var extended = Math.Max(baseEndMs, startMs + eighthMs);

        // Handshape containment � extend up to the next note in the same handshape
        // (or the handshape's end).
        if (handShapes != null && handShapes.Count > 0 && beats.Count > 0)
        {
            for (var i = 0; i < handShapes.Count; i++)
            {
                var hs = handShapes[i];
                var hsStartMs = hs.Position.GetPositionMs(beats);
                var hsEndMs = hs.EndPosition.GetPositionMs(beats);
                if (startMs + 1e-3 < hsStartMs || startMs > hsEndMs + 1e-3) continue;

                var nextInHs = legato.NextStartOnString(stringIndex, startMs);
                var hold = (nextInHs is { } nx && nx <= hsEndMs + 1e-3)
                    ? Math.Min(hsEndMs, nx) - padMs
                    : hsEndMs - padMs;
                if (hold > extended) extended = hold;
                break;
            }
        }

        // Clamp to just before the next attack on this string so the next NoteOn
        // is never on top of an active NoteOff.
        var next = legato.NextStartOnString(stringIndex, startMs);
        if (next is { } nextStart)
        {
            var clamp = nextStart - padMs;
            // Always cut by the next attack (don't ring through it). Keep a tiny
            // floor so we still get an audible attack at minimum.
            extended = Math.Min(extended, clamp);
            if (extended < startMs + 30.0) extended = startMs + 30.0;
        }

        return extended;
    }

    private void TriggerNote(int stringIndex, MuteType mute, HarmonicType harmonic, int midiNote, int velocity,
        double startMs, double endMs)
    {
        // Determine the MIDI channel. When routing through the internal VSTi
        // we split notes across per-string channels so a re-trigger on the
        // same string can NoteOff the previous pitch cleanly and Ample
        // Guitar's per-channel articulation engine works. When sending to a
        // generic WinMM device (e.g. MS GS Wavetable) channel 0 is fine.
        var throughInstrument = (_instrumentPlugin != null || _sandboxInstrumentLoaded)
                                  && _previewSink.Target is JuceInstrumentMidiOutput;
        var channel = throughInstrument ? StringChannelForInstrument(stringIndex) : 0;

        MidiKeyswitch? keyswitch = null;
        if (throughInstrument)
        {
            // The articulation keyswitch goes fresh before every NoteOn so
            // Ample's sticky per-channel state matches the authored mute /
            // harmonic flag of *this* note (including open-string notes which
            // can absolutely be palm-muted).
            keyswitch = AmpleArticulationKeyswitch(mute, harmonic);

            // Ample exposes mute depth via the *played* note velocity when the
            // palm-mute keyswitch is active (lower velocity = deeper / more
            // muted). Pinch/Normal harmonics also need a higher attack to
            // articulate clearly. We only apply this scaling when routing
            // through the Ample instrument so generic synths still hear the
            // authored velocity.
            if (_instrumentIsAmpleGuitar && harmonic == HarmonicType.None)
            {
                velocity = mute switch
                {
                    // Full mute = thunky percussive hit with no audible pitch.
                    MuteType.Full => 12,
                    // Palm mute = noticeably muted but still pitched.
                    MuteType.Palm => 55,
                    _ => velocity,
                };
            }
        }

        // The scheduler honours the (possibly legato-extended) endMs with a
        // MinHoldMs floor, so a zero-sustain note still fires its envelope.
        _midiScheduler.NoteOn(_previewSink, channel, midiNote, velocity, startMs, endMs,
            monoPerChannel: throughInstrument, keyswitch);
    }

    // The scheduler's output: the current sink, set under _midiGate before each
    // use. One instance for the service's life — nothing allocates per tick.
    private sealed class PreviewMidiSink : IMidiEventSink
    {
        public IMidiOutput? Target;

        public void Send(byte status, byte data1, byte data2, long renderFrame)
        {
            var midi = Target;
            if (midi == null) return;
            try
            {
                if (midi is JuceInstrumentMidiOutput instrument)
                {
                    instrument.Send(status, data1, data2, renderFrame);
                    return;
                }

                // WinMM / GS Wavetable: outside the engine, so there is no frame
                // to honour — every event goes now (the scheduler only ever sends
                // this sink immediate events anyway).
                var channel = status & 0x0F;
                switch (status & 0xF0)
                {
                    case 0x90 when data2 > 0: midi.NoteOn(channel, data1, data2); break;
                    case 0x90 or 0x80: midi.NoteOff(channel, data1); break;
                    case 0xB0 when data1 == 120: midi.AllSoundOff(channel); break;
                }
            }
            catch (Exception) { /* a failing sink drops this event, never the tick */ }
        }
    }
}
