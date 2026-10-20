using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Issue #38: the MIDI preview's timing, as the pure <see cref="MidiPreviewScheduler" /> that
///     <c>JuceAudioPlaybackService.UpdateMidiNotes</c> drives. Sample-accurate events carry the
///     absolute frame <c>anchor + (t − now) / speed × rate</c>; a jump, a loop wrap, a pause or a
///     speed change leaves no hanging and no doubled note — checked by replaying every event, in
///     frame order, through a voice model.
/// </summary>
public sealed class MidiPreviewSchedulerTests
{
    private const int Rate = 48_000;
    private const int Block = 256;

    [Fact]
    public void ANote_IsPlacedOnItsOwnFrame_AWholeLookaheadAhead()
    {
        var rig = new Rig();
        rig.Tick(nowMs: 1000, frame: 10_000);
        rig.Play(startMs: 1010, endMs: 1050, note: 40);
        rig.EndTick();

        Assert.Equal(1000 + MidiPreviewScheduler.LookaheadMs, rig.Scheduler.WindowEndMs);
        Assert.Equal(10_000 + 480, rig.FrameOf(0x90, 40)); // 10 ms at 48 kHz

        // Its end comes into the window 20 ms later and lands on ITS frame, on the same clock.
        rig.Tick(nowMs: 1020, frame: 10_000 + 960);
        rig.EndTick();
        Assert.Equal(10_000 + 2_400, rig.FrameOf(0x80, 40));
    }

    [Fact]
    public void ANoteExactlyOnABlockBoundary_LandsOnTheBoundary_AndIsSentOnce()
    {
        var rig = new Rig();
        var startMs = 1000 + (Block * 1000.0 / Rate); // one block after the anchor
        rig.Tick(nowMs: 1000, frame: 10_000);
        Assert.True(rig.DueNow(startMs));
        rig.Play(startMs, startMs + 200, note: 45);
        rig.EndTick();
        Assert.Equal(10_000 + Block, rig.FrameOf(0x90, 45));

        // The window is (start, end]: a note on the window's END is due in this tick, never again.
        var edgeMs = rig.Scheduler.WindowEndMs;
        rig.Tick(nowMs: 1004, frame: 10_192);
        Assert.False(rig.DueNow(edgeMs));
        Assert.True(rig.DueNow(edgeMs + 0.001));
    }

    [Fact]
    public void BelowNormalSpeed_TheWindowShrinks_AndTheFramesStretch()
    {
        var rig = new Rig();
        rig.Tick(nowMs: 1000, frame: 10_000, speed: 0.5);
        Assert.Equal(1000 + (MidiPreviewScheduler.LookaheadMs * 0.5), rig.Scheduler.WindowEndMs);
        rig.Play(startMs: 1010, endMs: 1400, note: 50);
        rig.EndTick();

        Assert.Equal(10_000 + 960, rig.FrameOf(0x90, 50)); // 10 chart ms = 20 wall ms at half speed
    }

    [Fact]
    public void ASpeedDrop_NeverMovesTheWindowBack_SoNothingIsSentTwice()
    {
        var notes = Notes((1005, 1100, 40), (1020, 1100, 45), (1028, 1100, 50), (1040, 1200, 55));
        var rig = new Rig();
        rig.Run(notes, 1000, 1008, stepMs: 4); // the window reaches 1038
        rig.Run(notes, 1012, 1016, stepMs: 4, speedAt: _ => 0.25); // ...and would now end at 1019.5
        // A chart edit rewinds the caller's cursor (UpdateMidiNotes does on any add/remove), so
        // only the window's own start keeps 1020 and 1028 from being sent again.
        rig.ResetCursor();
        rig.Run(notes, 1020, 1300, stepMs: 4, speedAt: _ => 0.25);
        rig.FinishAndSettle();

        foreach (var n in notes) Assert.Equal(1, rig.NoteOns(n.Note));
        rig.AssertNothingHangs();
    }

    [Fact]
    public void ASeekInsideASustain_ReleasesIt_DoesNotRetriggerIt_AndPlaysANoteExactlyOnTheTarget()
    {
        var notes = Notes((1000, 3000, 40), (1020, 1500, 45), (2000, 2200, 50), (2100, 2300, 55));
        var rig = new Rig();
        rig.Run(notes, 995, 1005, stepMs: 4); // 40 sounds, 45 is parked for a future frame

        Assert.Equal(1, rig.NoteOns(40));
        Assert.Equal(1, rig.NoteOns(45));
        var parked45 = rig.FrameOf(0x90, 45);
        Assert.True(parked45 > rig.LastFrame, "the fixture proves nothing unless 45 is still parked");

        rig.Flush(resumeAtMs: 2000);
        // The parked NoteOn would sound after the All Sound Off: it is answered one frame later.
        Assert.Contains(rig.Events, e => e.Status == 0x80 && e.Data1 == 45 && e.Frame == parked45 + 1);

        rig.Run(notes, 2004, 2400, stepMs: 4); // the transport lands a tick after the target
        rig.FinishAndSettle();

        Assert.Equal(1, rig.NoteOns(40)); // inside its sustain: not retriggered
        Assert.Equal(1, rig.NoteOns(50)); // exactly on the target: sounds
        Assert.Equal(1, rig.NoteOns(55));
        rig.AssertNothingHangs();
    }

    [Fact]
    public void ALoopWrap_PlaysEveryNoteOncePerPass_AndLeavesNothingHanging()
    {
        // Notes right at the loop start and just past the loop end (inside the lookahead).
        var notes = Notes((1000, 1300, 40), (1100, 1150, 45), (1190, 1400, 50), (1215, 1300, 55));
        var rig = new Rig();
        rig.Flush(resumeAtMs: 1000); // play from the loop start
        for (var pass = 0; pass < 3; pass++)
        {
            rig.Run(notes, 1000 + 3, 1200, stepMs: 4);
            rig.Flush(resumeAtMs: 1000); // what SeekTo does at the loop end
        }

        rig.FinishAndSettle();
        Assert.Equal(3, rig.NoteOns(40)); // the loop START is not skipped by the wrap
        Assert.Equal(3, rig.NoteOns(45));
        Assert.Equal(3, rig.NoteOns(50));
        rig.AssertNothingHangs();
        Assert.Equal(0, rig.Scheduler.ActiveCount);
    }

    [Fact]
    public void AJumpWithoutAFlush_IsDetected_AndReleasesEverything()
    {
        var rig = new Rig();
        rig.Tick(nowMs: 1000, frame: 10_000);
        rig.Play(1005, 1500, note: 40);
        rig.EndTick();
        rig.Events.Clear();

        Assert.True(rig.Tick(nowMs: 500, frame: 10_200)); // backwards by more than JumpBackMs
        Assert.Equal(0, rig.Scheduler.ActiveCount);
        Assert.Equal(16, rig.Events.Count(e => e.Status >= 0xB0 && e.Status <= 0xBF && e.Data1 == 120));
        Assert.False(rig.Tick(nowMs: 504, frame: 10_392));
    }

    [Fact]
    public void APause_LeavesNoParkedNoteHanging()
    {
        var notes = Notes((1010, 1400, 40), (1020, 1400, 45));
        var rig = new Rig();
        rig.Run(notes, 1000, 1004, stepMs: 4); // 40 and 45 are parked for future frames
        Assert.True(rig.FrameOf(0x90, 45) > rig.LastFrame, "the fixture proves nothing unless 45 is still parked");
        rig.Flush(); // Pause
        rig.Advance(frames: Rate); // the render clock keeps running while paused: they come due
        rig.AssertNothingHangs(upToFrame: rig.LastFrame); // ...and must not ring through the pause
        rig.Run(notes, 1005, 1500, stepMs: 4); // Play again from the same spot
        rig.FinishAndSettle();
        rig.AssertNothingHangs();
    }

    [Fact]
    public void AGuard_NeverCutsTheNoteThatFollowsItOnTheSameKey()
    {
        var rig = new Rig();
        rig.Tick(nowMs: 1000, frame: 10_000);
        rig.Play(1020, 1100, note: 40);
        rig.EndTick();
        var parked = rig.FrameOf(0x90, 40);

        // A seek 10 ms on, while that NoteOn is still parked: the same note now maps to an
        // EARLIER frame than the guard that answers the parked one.
        rig.Flush(resumeAtMs: 1010);
        rig.Tick(nowMs: 1010, frame: 10_010);
        rig.Play(1020, 1100, note: 40);
        rig.EndTick();

        var ons = rig.Events.Where(e => e.Status == 0x90 && e.Data1 == 40).Select(e => e.Frame).ToList();
        Assert.Equal(2, ons.Count);
        Assert.True(ons[1] > parked + 1, $"the new NoteOn at {ons[1]} must follow the guard at {parked + 1}");
    }

    [Fact]
    public void AMonoChannel_ReleasesItsPreviousNote_InFrontOfTheNext()
    {
        var rig = new Rig { Mono = true };
        rig.Tick(nowMs: 1000, frame: 10_000);
        rig.Play(1002, 1500, note: 40, channel: 2);
        rig.Play(1010, 1500, note: 42, channel: 2);
        rig.EndTick();

        var on42 = rig.FrameOf(0x92, 42);
        Assert.Equal(on42 - 1, rig.FrameOf(0x82, 40));
        Assert.Equal(42, rig.Scheduler.ChannelNote(2));
    }

    [Fact]
    public void AKeyswitch_IsPressedOneFrameAhead_AndTheChosenKeyIsNotReleased()
    {
        var rig = new Rig { Keyswitch = new MidiKeyswitch(15, 26, 64, 23, 30) };
        rig.Tick(nowMs: 1000, frame: 10_000);
        rig.Play(1010, 1500, note: 40);
        rig.EndTick();

        var on = rig.FrameOf(0x90, 40);
        var ks = rig.Events.Where(e => e.Status == 0x9F).ToList();
        Assert.All(ks, e => Assert.Equal(on - 1, e.Frame));
        Assert.Equal([23, 24, 25, 27, 28, 29, 30, 26], ks.Select(e => (int)e.Data1));
        Assert.Equal(64, ks[^1].Data2);
    }

    [Fact]
    public void WithoutARenderClock_EveryEventIsImmediate_AndNothingIsSentEarly()
    {
        var rig = new Rig { Immediate = true };
        rig.Tick(nowMs: 1000, frame: 0);
        Assert.Equal(1000, rig.Scheduler.WindowEndMs);
        Assert.False(rig.DueNow(1010));

        var notes = Notes((1010, 1100, 40));
        rig.Run(notes, 1004, 1200, stepMs: 4);
        rig.FinishAndSettle();
        Assert.All(rig.Events, e => Assert.Equal(-1, e.Frame));
        Assert.Equal(1, rig.NoteOns(40));
    }

    [Fact]
    public void ATick_AllocatesNothing()
    {
        var sink = new CountingSink();
        var scheduler = new MidiPreviewScheduler();
        var now = 1000.0;
        var frame = 10_000L;
        AllocationProbe.AssertAllocationFree("MidiPreviewScheduler tick", () =>
        {
            for (var i = 0; i < 200; i++)
            {
                scheduler.BeginTick(sink, now, 1.0, new MidiRenderAnchor(frame, Rate));
                scheduler.NoteOn(sink, i % 6, 40 + (i % 12), 100, now + 10, now + 60, monoPerChannel: true,
                    new MidiKeyswitch(15, 24, 100, 23, 30));
                scheduler.EndTick(sink);
                now += 4;
                frame += 192;
            }
        });
        Assert.True(sink.Count > 0);
    }

    /// <summary>
    ///     JuceSharp #28: the SANDBOXED instrument is scheduled on the render clock too. Its bridge
    ///     used to drop the frame, so the preview kept it immediate (a scheduled-ahead note would
    ///     have sounded early by the lookahead); bridge v3 carries each event's sample offset to
    ///     the child, so both engine instruments get an anchor and only a device outside the
    ///     engine stays immediate.
    /// </summary>
    [Fact]
    public void BothEngineInstruments_ScheduleOnTheRenderClock_AndOnlyAnOutsideDeviceStaysImmediate()
    {
        var engineSink = new JuceInstrumentMidiOutput(null!);   // the ctor only stores the engine
        var outsideDevice = new NullMidiOutput();

        Assert.True(MidiPreviewScheduler.SchedulesOnRenderClock(true, false, engineSink));
        Assert.True(MidiPreviewScheduler.SchedulesOnRenderClock(false, true, engineSink)); // the sandbox
        Assert.False(MidiPreviewScheduler.SchedulesOnRenderClock(false, false, engineSink));
        Assert.False(MidiPreviewScheduler.SchedulesOnRenderClock(true, false, outsideDevice));
        Assert.False(MidiPreviewScheduler.SchedulesOnRenderClock(false, true, outsideDevice));
    }

    /// <summary>
    ///     The wiring half: the render anchor is gated by
    ///     <see cref="MidiPreviewScheduler.SchedulesOnRenderClock" /> with BOTH instrument flags
    ///     (in-process and sandbox), every send goes through the scheduler, and a frame reaches
    ///     the engine's timed entry point.
    /// </summary>
    [Fact]
    public void TheService_AnchorsBothEngineInstruments_AndSendsTimedEventsThroughSendMidiAt()
    {
        var audio = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Audio");
        var service = string.Concat(PlaybackServiceSourceFiles.All().Select(File.ReadAllText));
        Assert.Contains("MidiPreviewScheduler.SchedulesOnRenderClock(_instrumentPlugin != null, _sandboxInstrumentLoaded, midi)", service);
        Assert.DoesNotContain("!_sandboxInstrumentLoaded && midi is JuceInstrumentMidiOutput", service);
        Assert.Contains("anchor = new MidiRenderAnchor(frame, sr);", service);
        Assert.Contains("_midiScheduler.BeginTick(_previewSink, nowMs, _playbackSpeed, anchor)", service);
        Assert.DoesNotContain("_activeMidiNotes", service); // one bookkeeping, the scheduler's

        var sink = File.ReadAllText(Path.Combine(audio, "Midi", "JuceInstrumentMidiOutput.cs"));
        Assert.Contains("_engine.SendMidi(status, (byte)(data1 & 0x7F), (byte)(data2 & 0x7F), renderFrame)", sink);
    }

    // ---- harness -------------------------------------------------------------

    private static List<(double Start, double End, int Note)> Notes(params (double Start, double End, int Note)[] notes) => [.. notes];

    private sealed class CountingSink : IMidiEventSink
    {
        public long Count;

        public void Send(byte status, byte data1, byte data2, long renderFrame) => Count++;
    }

    private sealed record Event(byte Status, byte Data1, byte Data2, long Frame, long Seq, long SentAt);

    /// <summary>
    ///     Plays the role of <c>UpdateMidiNotes</c>: a render clock that advances with the playhead,
    ///     and the same window scan (start in (WindowStart, WindowEnd]) with the cursor rewound on a
    ///     jump. Immediate events are stamped with the frame they were sent at, which is where the
    ///     engine places them (the head of the next block).
    /// </summary>
    private sealed class Rig : IMidiEventSink
    {
        private long _seq;
        private long _frame = 100_000;
        private double _lastMs = double.NaN;
        private int _cursor;

        public MidiPreviewScheduler Scheduler { get; } = new();
        public List<Event> Events { get; } = [];
        public bool Mono { get; init; }
        public bool Immediate { get; init; }
        public MidiKeyswitch? Keyswitch { get; init; }
        public long LastFrame => _frame;

        public void Send(byte status, byte data1, byte data2, long renderFrame) =>
            Events.Add(new Event(status, data1, data2, renderFrame, _seq++, _frame));

        public bool Tick(double nowMs, long frame, double speed = 1.0)
        {
            _frame = frame;
            _lastMs = nowMs;
            var jump = Scheduler.BeginTick(this, nowMs, speed, Immediate ? null : new MidiRenderAnchor(frame, Rate));
            if (jump) _cursor = 0;
            return jump;
        }

        public bool DueNow(double startMs) => startMs > Scheduler.WindowStartMs && startMs <= Scheduler.WindowEndMs;

        public void Play(double startMs, double endMs, int note, int channel = 0) =>
            Scheduler.NoteOn(this, channel, note, 100, startMs, endMs, Mono, Keyswitch);

        public void EndTick() => Scheduler.EndTick(this);

        public void Flush(double? resumeAtMs = null)
        {
            Scheduler.Flush(this, resumeAtMs);
            _cursor = 0;
        }

        public void Advance(long frames) => _frame += frames;

        public void ResetCursor() => _cursor = 0;

        /// <summary>Ticks from <paramref name="fromMs" /> to <paramref name="toMs" />, the clock following at <paramref name="speedAt" />.</summary>
        public void Run(List<(double Start, double End, int Note)> notes, double fromMs, double toMs, double stepMs,
            Func<double, double>? speedAt = null)
        {
            for (var ms = fromMs; ms <= toMs; ms += stepMs)
            {
                var speed = speedAt?.Invoke(ms) ?? 1.0;
                // The wall clock advances by the chart step over the speed — plus a fresh clock on a jump.
                var wallFrames = double.IsNaN(_lastMs) || Math.Abs(ms - _lastMs) > 2 * stepMs
                    ? Block
                    : (long)Math.Round((ms - _lastMs) / speed * Rate / 1000.0);
                Tick(ms, _frame + wallFrames, speed);
                while (_cursor < notes.Count && notes[_cursor].Start <= Scheduler.WindowStartMs) _cursor++;
                for (var i = _cursor; i < notes.Count && notes[i].Start <= Scheduler.WindowEndMs; i++)
                {
                    Play(notes[i].Start, notes[i].End, notes[i].Note);
                }

                EndTick();
            }
        }

        /// <summary>
        ///     Let the clock run past every parked event — WITHOUT a flush, whose All Sound Off
        ///     would hide exactly the hanging note the replay is looking for. Every run ends past
        ///     its last note's end, so nothing legitimate is still sounding.
        /// </summary>
        public void FinishAndSettle() => _frame += Rate * 10L;

        public long FrameOf(int status, int note) =>
            Events.First(e => e.Status == status && e.Data1 == note).Frame;

        public int NoteOns(int note) => Events.Count(e => (e.Status & 0xF0) == 0x90 && e.Status != 0x9F && e.Data1 == note && e.Data2 > 0);

        /// <summary>
        ///     Replays every event in the order the engine renders them — a timed event at its
        ///     frame, an immediate one at the head of the block that was next when it was sent —
        ///     through a voice model (a NoteOn adds a voice, a NoteOff releases one, All Sound Off
        ///     clears the channel) and asserts no voice is left sounding. Counting voices is what
        ///     catches a parked NoteOn that a later, legitimate replay of the same note would
        ///     otherwise hide.
        /// </summary>
        public void AssertNothingHangs(long upToFrame = long.MaxValue)
        {
            var voices = new Dictionary<(int, int), int>();
            foreach (var e in Events.Where(e => (e.Frame >= 0 ? e.Frame : e.SentAt) <= upToFrame)
                         .OrderBy(e => e.Frame >= 0 ? e.Frame : e.SentAt).ThenBy(e => e.Seq))
            {
                Apply(voices, e);
            }

            var hanging = voices.Where(v => v.Value > 0).Select(v => $"ch{v.Key.Item1} note {v.Key.Item2} x{v.Value}").ToList();
            Assert.True(hanging.Count == 0, "left sounding: " + string.Join(", ", hanging));
        }

        private static void Apply(Dictionary<(int, int), int> voices, Event e)
        {
            var key = (e.Status & 0x0F, (int)e.Data1);
            switch (e.Status & 0xF0)
            {
                case 0x90 when e.Data2 > 0 && e.Status != 0x9F:
                    voices[key] = voices.GetValueOrDefault(key) + 1;
                    break;
                case 0x90 or 0x80 when e.Status != 0x9F:
                    voices[key] = Math.Max(0, voices.GetValueOrDefault(key) - 1);
                    break;
                case 0xB0 when e.Data1 == 120:
                    foreach (var k in voices.Keys.Where(k => k.Item1 == key.Item1).ToList()) voices[k] = 0;
                    break;
            }
        }
    }
}
