using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig tuner reads the seat the rig is pointed at.
///
///     <para>
///         Every seat has had its own capture ring since ABI v28, and the in-rig
///         tuner read the GLOBAL tap for all of them — so on a two-guitar couch,
///         switching the board to player 2 left the needle on player 1's guitar
///         and "tune each guitar" tuned one guitar twice. Nothing native changed
///         to fix it: <c>dca_engine_read_input_capture_for_player</c> and the
///         wrapper's <c>ReadInputCapture(player, …)</c> already existed; what was
///         missing was a managed member and a caller.
///     </para>
///     <para>
///         These assert against WHICH RING was drained and WHAT NOTE came out —
///         never against the seat property alone. A picker that moves a field
///         nobody reads is the shape this repo has shipped four times.
///     </para>
/// </summary>
public sealed class RigTunerSeatTests
{
    private const int SampleRate = 48000;
    private const int WindowSize = 4096;

    private const double E2Hz = 82.4069;  // low E — what seat 0 is holding
    private const double A2Hz = 110.0;    // A — what seat 1 is holding

    private static float[] Sine(double hz)
    {
        var buf = new float[WindowSize];
        for (var i = 0; i < WindowSize; i++)
            buf[i] = 0.35f * MathF.Sin((float)(2.0 * Math.PI * hz * i / SampleRate));
        return buf;
    }

    /// <summary>
    ///     An engine with a different guitar on every seat, which RECORDS which
    ///     ring each read came out of. "Seat 1 is tuned" and "seat 1 is tuned off
    ///     seat 0's guitar" produce identical needles otherwise.
    /// </summary>
    private sealed class SeatAudio : FakeAudioPlaybackService,
        global::DotCharter.Tone.Audio.Abstractions.IPlayerInputRouting
    {
        public readonly List<int> WindowReads = [];
        public readonly List<int> StreamReads = [];
        public readonly Dictionary<int, float[]> Rings = [];

        public int LiveInputPlayerCount { get; set; } = 1;

        public void SetLiveInputOverride(global::DotCharter.Tone.Audio.Abstractions.LiveInputSeatLayout layout) => LiveInputPlayerCount = layout.PlayerCount;
        public void RestoreLiveInputBaseline() => LiveInputPlayerCount = 1;
        public float GetAndResetPlayerInputPeak(int player) => 0f;

        /// <summary>Per-seat drop tune (ABI v31) — recorded, so a test can assert
        /// WHICH seat was detuned.</summary>
        public readonly Dictionary<int, double> PitchShifts = [];
        public readonly Dictionary<int, int> PitchShiftModes = [];

        public void SetPlayerInputPitchShift(int player, double semitones) => PitchShifts[player] = semitones;
        public void SetPlayerInputPitchShiftMode(int player, int mode) => PitchShiftModes[player] = mode;
        public bool SupportsPerPlayerDropTune => true;

        public int ReadPlayerInputCaptureStream(int player, Span<float> destination, out ulong streamPosition)
        {
            StreamReads.Add(player);
            streamPosition = 0;
            return 0;
        }

        public bool TryGetPlayerInputCaptureSync(int player, out ulong capturedSamples, out long hostTicks)
        {
            capturedSamples = 0;
            hostTicks = 0;
            return false;
        }

        public int ReadPlayerInputCaptureWindow(int player, float[] destination)
        {
            WindowReads.Add(player);
            if (!Rings.TryGetValue(player, out var ring)) return 0;
            var n = Math.Min(destination.Length, ring.Length);
            Array.Copy(ring, destination, n);
            return n;
        }
    }

    /// <summary>Seat 0 on the global tap, seat 1 on its own ring, both live.</summary>
    private static SeatAudio TwoLiveGuitars()
    {
        var audio = new SeatAudio
        {
            InputSampleRate = SampleRate,
            InputCaptureWindow = Sine(E2Hz),
            LiveInputPlayerCount = 2
        };
        audio.Rings[0] = Sine(E2Hz);
        audio.Rings[1] = Sine(A2Hz);
        return audio;
    }

    private static RigTunerViewModel Tuner(SeatAudio audio) => new(audio, FakeLocalization.English(), audio);

    // ----------------------------------------------------------------- the read

    /// <summary>The solo path, unchanged: seat 0 reads the global window and no
    /// per-seat call is made at all — a pre-v28 DLL is never asked for an export
    /// it lacks.</summary>
    [Fact]
    public void Seat0_ReadsTheGlobalTap_AndAsksForNoSeatRing()
    {
        var audio = TwoLiveGuitars();
        var tuner = Tuner(audio);

        tuner.Tick();

        Assert.Equal(0, tuner.Player);
        Assert.Empty(audio.WindowReads);
        Assert.True(tuner.HasReading);
        Assert.Equal("E2", tuner.NoteText);
    }

    /// <summary>
    ///     THE POINT. Pointed at seat 1, the needle reads seat 1's ring — a
    ///     different guitar, a different note. With the bug this replaces it read
    ///     the global tap and answered "E" for both players.
    /// </summary>
    [Fact]
    public void Seat1_ReadsSeat1sOwnRing_NotTheGlobalTap()
    {
        var audio = TwoLiveGuitars();
        var tuner = Tuner(audio);

        tuner.Player = 1;
        tuner.Tick();

        Assert.Equal([1], audio.WindowReads);
        Assert.True(tuner.HasReading);
        Assert.Equal("A2", tuner.NoteText);
    }

    /// <summary>Two seats holding two different notes must never converge on one
    /// reading — the whole failure, stated as one assertion.</summary>
    [Fact]
    public void TwoSeats_ReadTheirOwnGuitars_NotOneSharedOne()
    {
        var audio = TwoLiveGuitars();
        var tuner = Tuner(audio);

        tuner.Tick();
        var seat0 = tuner.NoteText;

        tuner.Player = 1;
        tuner.Tick();
        var seat1 = tuner.NoteText;

        Assert.Equal("E2", seat0);
        Assert.Equal("A2", seat1);
        Assert.NotEqual(seat0, seat1);
    }

    /// <summary>
    ///     The tuner takes the WINDOWED read, never the streaming one. Seat N's
    ///     stream cursor belongs to a streaming reader; a needle consuming it would
    ///     steal its samples, and both cursors
    ///     exist separately in the engine for exactly this reason.
    /// </summary>
    [Fact]
    public void TheTuner_NeverDrainsTheSeatsJudgingStream()
    {
        var audio = TwoLiveGuitars();
        var tuner = Tuner(audio);

        tuner.Player = 1;
        tuner.Tick();
        tuner.Tick();
        tuner.Tick();

        Assert.Empty(audio.StreamReads);
        Assert.Equal([1, 1, 1], audio.WindowReads);
    }

    // ------------------------------------------------------------- the honesty

    /// <summary>
    ///     A seat the engine is not RUNNING has no ring to read: the callback
    ///     only walks the live seats, so its capture ring is never written. The
    ///     panel says so rather than standing on "Pluck a string…" over a guitar
    ///     that can never appear — the capability rule, applied before the fact.
    /// </summary>
    [Fact]
    public void ASeatTheEngineIsNotRunning_SaysSo_AndReadsNothing()
    {
        var audio = TwoLiveGuitars();
        audio.LiveInputPlayerCount = 1;   // solo engine, co-op picker still up
        var tuner = Tuner(audio);

        tuner.Player = 1;
        tuner.Tick();

        Assert.False(tuner.IsSeatLive);
        Assert.False(tuner.HasInput);
        Assert.False(tuner.HasReading);
        Assert.Empty(audio.WindowReads);
        Assert.Equal(FakeLocalization.English()["Tuner_SeatNotLive"], tuner.StatusHint);
    }

    /// <summary>Seat 0 is live on every engine — a solo player must never be told
    /// their own input is only live in co-op.</summary>
    [Fact]
    public void Seat0_IsAlwaysLive()
    {
        var audio = TwoLiveGuitars();
        audio.LiveInputPlayerCount = 1;
        var tuner = Tuner(audio);

        Assert.True(tuner.IsSeatLive);
    }

    /// <summary>Without a per-seat surface behind it (headless, the null service)
    /// the tuner stays on seat 0 rather than reading a ring that does not exist —
    /// an unreachable seat must not present as a silent guitar.</summary>
    [Fact]
    public void WithNoPerSeatRouting_TheTunerStaysOnSeat0()
    {
        var audio = new FakeAudioPlaybackService
        {
            InputSampleRate = SampleRate,
            InputCaptureWindow = Sine(E2Hz)
        };
        var tuner = new RigTunerViewModel(audio, FakeLocalization.English(), routing: null)
        {
            Player = 3
        };
        tuner.Tick();

        Assert.Equal(0, tuner.Player);
        Assert.True(tuner.IsSeatLive);
        Assert.Equal("E2", tuner.NoteText);
    }

    /// <summary>Moving seats drops the previous guitar's reading immediately —
    /// a stale "in tune" needle over the guitar you just picked up is worse than
    /// no needle.</summary>
    [Fact]
    public void MovingSeats_ClearsThePreviousGuitarsReading()
    {
        var audio = TwoLiveGuitars();
        var tuner = Tuner(audio);

        tuner.Tick();
        Assert.True(tuner.HasReading);

        tuner.Player = 1;

        Assert.False(tuner.HasReading);
        Assert.Equal("–", tuner.NoteText);
    }

    // --------------------------------------------------------------- the wiring

    /// <summary>
    ///     The rig's seat picker is what moves the tuner. One seat answer for the
    ///     whole surface: a board on player 2 with a needle on player 1 is two
    ///     answers to "which guitar", and the player cannot see which one they are
    ///     looking at.
    /// </summary>
    [Fact]
    public void PickingASeatOnTheBoard_MovesTheTunerToThatSeat()
    {
        var chains = new FakeChains(2);
        var rig = TestRigBuilders.Pedalboard(chains.For(0), new FakeDspTonePresetService(), FakeLocalization.English(),
            audio: TwoLiveGuitars(), chains: chains);

        rig.SetSeats([new RigSeatOption(0, "Ada"), new RigSeatOption(1, "Bo")]);
        rig.SelectedSeat = rig.Seats[1];

        Assert.Equal(1, rig.SelectedPlayer);
        Assert.Equal(1, rig.RigTuner.Player);

        rig.SelectedSeat = rig.Seats[0];
        Assert.Equal(0, rig.RigTuner.Player);
    }

    /// <summary>…and it still moves on a build with no per-seat DSP chains: the
    /// tuner's ring and the rig's chain are different native surfaces, and a
    /// missing one must not strand the other.</summary>
    [Fact]
    public void WithNoPerSeatChains_ThePickerStillMovesTheTuner()
    {
        var audio = TwoLiveGuitars();
        var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English(),
            audio: audio);

        rig.RigTuner.Player = 0;
        rig.SelectedSeat = new RigSeatOption(1, "Bo");

        Assert.Equal(1, rig.RigTuner.Player);
    }

    private sealed class FakeChains : IPlayerDspChains
    {
        private readonly List<IDspChainService> _all;

        public FakeChains(int seats) =>
            _all = Enumerable.Range(0, seats)
                .Select(_ => (IDspChainService)new FakeDspChainService())
                .ToList();

        public int MaxPlayers => _all.Count;
        public IReadOnlyList<IDspChainService> All => _all;
        public IDspChainService For(int player) => _all[Math.Clamp(player, 0, _all.Count - 1)];
        public void AttachEngineToAll(nint engine) { }
    }
}
