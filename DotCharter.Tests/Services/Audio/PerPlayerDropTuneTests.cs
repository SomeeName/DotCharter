using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Per-seat drop tune (ABI v31).
///
///     <para>
///         The engine used to hold ONE <c>dcdsp::DropTune</c> and one semitone
///         atomic, applied to the raw device input BEFORE the per-player split.
///         So on a couch with two guitars in different tunings the pedal was
///         unusable: dropping one dropped BOTH, and the seat that did not ask for
///         it was detuned with nothing on screen saying so. v31 gives every seat
///         its own shifter, its own target and its own lag ceiling.
///     </para>
///     <para>
///         The DSP itself is native and cannot be asserted from here. What the
///         managed side owes — and what shipped broken three times for the other
///         per-seat state — is that a seat's setting is RECORDED and REPLAYED
///         across an engine rebuild, and that seat 0 stays exactly the pedal it
///         has always been.
///     </para>
/// </summary>
public sealed class PerPlayerDropTuneTests
{
    private static PlayerInputRoutingState State(int seats = 4) => new(seats);

    // ------------------------------------------------------------- the defaults

    /// <summary>
    ///     A fresh state is the engine's own starting point, and
    ///     <c>IsDefault</c> is what makes an ordinary device change cost NO
    ///     P/Invokes. A drop tune that read as non-default out of the box would
    ///     make every solo device change re-assert four seats.
    /// </summary>
    [Fact]
    public void AFreshState_IsDefault_AndEverySeatIsBypassedAtBalanced()
    {
        var state = State();

        Assert.True(state.IsDefault);
        Assert.All(state.PitchShiftSemitones, s => Assert.Equal(0f, s));
        Assert.All(state.PitchShiftModes,
            m => Assert.Equal(PlayerInputRoutingState.DefaultPitchShiftMode, m));
    }

    /// <summary>The engine's default mode and the mapping's default are ONE
    /// value. They are declared in two places (native, managed) and a
    /// disagreement means a rebuild silently changes the pedal's feel.</summary>
    [Fact]
    public void TheDefaultMode_AgreesWithDropTunerEngineMap()
    {
        Assert.Equal(DropTunerEngineMap.Default, PlayerInputRoutingState.DefaultPitchShiftMode);
        Assert.Equal(DropTunerEngineMap.Balanced, PlayerInputRoutingState.DefaultPitchShiftMode);
    }

    // -------------------------------------------------------------- the recording

    /// <summary>
    ///     THE POINT of recording it at all: a drop tune must survive an engine
    ///     REBUILD. A device change, the NAM 48 kHz request or a watchdog
    ///     recovery otherwise hands the player back a guitar a whole step out of
    ///     tune with the song, mid-run, with nothing on screen changing. The
    ///     count, the channels and the trims each shipped missing from this
    ///     replay; this one is not going to.
    /// </summary>
    [Fact]
    public void ASeatsDropTune_IsRecorded_SoTheRebuildCanReplayIt()
    {
        var state = State();

        state.RecordPitchShiftSemitones(1, -2f);
        state.RecordPitchShiftMode(1, DropTunerEngineMap.Smooth);

        Assert.False(state.IsDefault);
        Assert.Equal(-2f, state.PitchShiftSemitones[1]);
        Assert.Equal(DropTunerEngineMap.Smooth, state.PitchShiftModes[1]);
        // …and nobody else moved. A pedal that detunes the neighbouring seat is
        // the bug this whole feature exists to remove.
        Assert.Equal(0f, state.PitchShiftSemitones[0]);
        Assert.Equal(0f, state.PitchShiftSemitones[2]);
    }

    /// <summary>Seat 0's pedal alone makes the state non-default, so a SOLO drop
    /// tune is replayed too — the pre-v31 pedal is seat 0's and it has to come
    /// back across a device change like everything else.</summary>
    [Fact]
    public void ASoloDropTune_AlsoLeavesTheDefaultState()
    {
        var state = State();

        state.RecordPitchShiftSemitones(0, -1f);

        Assert.False(state.IsDefault);
    }

    /// <summary>
    ///     A mode outside 0..2 is CLAMPED here, because the native export
    ///     REJECTS it — recording an out-of-range value would mean the rebuild
    ///     replays a write the engine refuses, leaving the seat on whatever a
    ///     fresh engine starts with while the app believes otherwise.
    /// </summary>
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(9, 2)]
    public void AnOutOfRangeMode_IsClampedIntoWhatTheEngineAccepts(int given, int expected)
    {
        var state = State();

        state.RecordPitchShiftMode(0, given);

        Assert.Equal(expected, state.PitchShiftModes[0]);
    }

    /// <summary>Out-of-range seats and non-finite values are ignored rather than
    /// throwing: this is reached from the co-op start path, and arming a run must
    /// never be able to take the audio engine down.</summary>
    [Fact]
    public void AnImpossibleSeatOrValue_IsIgnoredRatherThanThrown()
    {
        var state = State(2);

        state.RecordPitchShiftSemitones(-1, -3f);
        state.RecordPitchShiftSemitones(7, -3f);
        state.RecordPitchShiftSemitones(0, float.NaN);
        state.RecordPitchShiftMode(9, 2);

        Assert.True(state.IsDefault);
    }

    /// <summary>Returning to defaults clears the drop tune with everything else —
    /// otherwise a stale record would re-detune the next engine.</summary>
    [Fact]
    public void ResetToDefaults_ClearsTheDropTuneToo()
    {
        var state = State();
        state.RecordPitchShiftSemitones(1, -2f);
        state.RecordPitchShiftMode(1, 2);

        state.ResetToDefaults();

        Assert.True(state.IsDefault);
        Assert.All(state.PitchShiftSemitones, s => Assert.Equal(0f, s));
    }

    // ----------------------------------------------------- the replay is WRITTEN

    /// <summary>
    ///     A source lint that the rebuild really replays it.
    ///     <para>
    ///         <c>EngineRebuildStateReplayTests</c> enumerates the ENGINE's
    ///         setters and asks whether each is replayed at all; it cannot see
    ///         that the per-SEAT loop covers every seat, because
    ///         <c>SetInputPitchShift</c> has a seat-0 overload that the master-bus
    ///         replay already satisfies. That is precisely the hole this fills:
    ///         seat 0's pedal would come back and seats 1..N would not, which is a
    ///         run where one guitar silently returns to concert pitch.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheEngineRebuild_ReplaysEverySeatsDropTune_NotJustSeat0()
    {
        var replay = SourceOf("DotCharter.Tone", "Audio",
            "JuceAudioPlaybackService.PlayerRouting.cs");
        var body = replay[replay.IndexOf("AttachPerPlayerStateToEngine", StringComparison.Ordinal)..];

        Assert.Contains("_playerRouting.PitchShiftSemitones.Count", body, StringComparison.Ordinal);
        Assert.Contains("engine.SetInputPitchShift(player,", body, StringComparison.Ordinal);
        Assert.Contains("engine.SetInputPitchShiftMode(player,", body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Seat 0's public property IS the per-seat write, not a second path to
    ///     the same engine state. Two writers meant the recorded state and the
    ///     property could disagree, and the rebuild replays the RECORD.
    /// </summary>
    [Fact]
    public void Seat0sProperty_GoesThroughThePerSeatWrite()
    {
        var mixer = SourceOf("DotCharter.Tone", "Audio", "JuceAudioPlaybackService.Mixer.cs");

        Assert.Contains("set => SetPlayerInputPitchShift(0, value);", mixer, StringComparison.Ordinal);
        Assert.Contains("set => SetPlayerInputPitchShiftMode(0, value);", mixer, StringComparison.Ordinal);
        // The old two-engine clamp is GONE. It stored `value == 1 ? 1 : 0`, so
        // the LIVE push of Smooth (2) sent Tight (0) — the one mode you would
        // restart the app to audition was the one you could not hear.
        //
        // Comments are stripped first, and that is not incidental: the property's
        // own doc comment NAMES the old expression, so a raw substring search
        // fails against the very file that fixed it. A `see cref` is exactly as
        // inert as a comment, which is why every source lint in this repo strips.
        var code = string.Join('\n', mixer.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
        Assert.DoesNotContain("value == 1 ? 1 : 0", code, StringComparison.Ordinal);
    }

    private static string SourceOf(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine([dir!.FullName, .. relative]);
        Assert.True(File.Exists(path), $"Source not found: {path}");
        return File.ReadAllText(path);
    }
}
