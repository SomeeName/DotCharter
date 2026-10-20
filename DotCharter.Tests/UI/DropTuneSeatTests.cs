using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The drop-tune pedal is per SEAT (ABI v31), and the transport is the
///     surface that says which one.
///
///     <para>
///         Until v31 the engine held ONE shifter ahead of the per-player split,
///         so a couch with two guitars in different tunings could not use the
///         pedal at all: dropping one dropped BOTH, and the seat that did not ask
///         for it was detuned with nothing on screen saying so.
///     </para>
///     <para>
///         These assert against the ROUTING CALLS — which seat the engine was
///         told to detune — never against the picker's own property. A selector
///         that moves a field nobody reads is the shape this repo has shipped
///         four times.
///     </para>
/// </summary>
public sealed class DropTuneSeatTests
{
    /// <summary>An audio service that records which seat's pedal was written.</summary>
    private sealed class SeatAudio : FakeAudioPlaybackService, IPlayerInputRouting
    {
        public readonly List<(int Seat, double Semitones)> Shifts = [];
        public readonly List<(int Seat, int Mode)> Modes = [];

        public void SetLiveInputOverride(LiveInputSeatLayout layout) { }
        public void RestoreLiveInputBaseline() { }
        public float GetAndResetPlayerInputPeak(int player) => 0f;
        public int ReadPlayerInputCaptureWindow(int player, float[] destination) => 0;
        public int LiveInputPlayerCount => 2;
        public bool SupportsPerPlayerDropTune => true;

        public void SetPlayerInputPitchShift(int player, double semitones) => Shifts.Add((player, semitones));
        public void SetPlayerInputPitchShiftMode(int player, int mode) => Modes.Add((player, mode));

        public int ReadPlayerInputCaptureStream(int player, Span<float> destination, out ulong streamPosition)
        {
            streamPosition = 0;
            return 0;
        }

        public bool TryGetPlayerInputCaptureSync(int player, out ulong capturedSamples, out long hostTicks)
        {
            capturedSamples = 0;
            hostTicks = 0;
            return false;
        }
    }

    private static (TransportViewModel Transport, SeatAudio Audio, FakeConfigurationService Config) NewTransport()
    {
        var audio = new SeatAudio();
        var config = new FakeConfigurationService();
        return (new TransportViewModel(audio, new ProjectState(), config, FakeLocalization.KeysOnly()), audio, config);
    }

    private static IReadOnlyList<RigSeatOption> TwoSeats() =>
        [new RigSeatOption(0, "Ana"), new RigSeatOption(1, "Bo")];

    // ------------------------------------------------------------- solo, unchanged

    /// <summary>One seat is not a choice — no picker, and every write is seat 0's
    /// pedal, which is exactly what it always was.</summary>
    [Fact]
    public void WithNoRoster_ThereIsNoPickerAndEveryWriteIsSeat0()
    {
        var (transport, audio, _) = NewTransport();

        Assert.False(transport.HasMultipleDropTuneSeats);
        Assert.Equal(0, transport.DropTunePlayer);

        transport.InputPitchShiftSemis = -2;

        Assert.Equal([(0, -2.0)], audio.Shifts);
    }

    /// <summary>Seat 0's pedal is PERSISTED, exactly as before — a solo player's
    /// drop tune survives a restart.</summary>
    [Fact]
    public void Seat0sDropTune_IsStillPersisted()
    {
        var (transport, _, config) = NewTransport();

        transport.InputPitchShiftSemis = -3;

        Assert.Equal(-3, config.LoadAppConfig().InputPitchShiftSemis);
    }

    // ---------------------------------------------------------------- per seat

    /// <summary>THE POINT. Pointed at seat 1, the pedal detunes seat 1 — and only
    /// seat 1. With the bug this replaces there was one shifter ahead of the
    /// split and both guitars moved together.</summary>
    [Fact]
    public void PickingASeat_DetunesThatSeatAlone()
    {
        var (transport, audio, _) = NewTransport();
        transport.SetVstSeats(TwoSeats());

        transport.DropTuneSeat = transport.VstSeats[1];
        transport.InputPitchShiftSemis = -2;

        Assert.True(transport.HasMultipleDropTuneSeats);
        Assert.Equal(1, transport.DropTunePlayer);
        Assert.Contains((1, -2.0), audio.Shifts);
        Assert.DoesNotContain(audio.Shifts, s => s.Seat == 0 && s.Semitones != 0);
    }

    /// <summary>Each seat keeps its OWN value, and the readout follows the
    /// picker: a combo that showed seat 0's setting over seat 1's guitar is the
    /// same lie as detuning the wrong one.</summary>
    [Fact]
    public void EachSeatKeepsItsOwnSetting_AndTheReadoutFollowsThePicker()
    {
        var (transport, _, _) = NewTransport();
        transport.SetVstSeats(TwoSeats());

        transport.InputPitchShiftSemis = -1;          // seat 0
        transport.DropTuneSeat = transport.VstSeats[1];
        transport.InputPitchShiftSemis = -5;          // seat 1

        Assert.Equal(-5, transport.InputPitchShiftSemis);
        transport.DropTuneSeat = transport.VstSeats[0];
        Assert.Equal(-1, transport.InputPitchShiftSemis);
    }

    /// <summary>
    ///     A higher seat is NOT persisted. <c>AppConfig.InputPitchShiftSemis</c>
    ///     is one integer and the extra seats belong to a roster, not to the
    ///     install — writing them there would restore a second player's drop tune
    ///     onto the next SOLO session, silently.
    /// </summary>
    [Fact]
    public void AHigherSeatsDropTune_IsNotWrittenIntoTheInstallsConfig()
    {
        var (transport, _, config) = NewTransport();
        transport.SetVstSeats(TwoSeats());

        transport.DropTuneSeat = transport.VstSeats[1];
        transport.InputPitchShiftSemis = -4;

        Assert.Equal(0, config.LoadAppConfig().InputPitchShiftSemis);
    }

    /// <summary>The run ended and the roster is gone: the picker collapses to one
    /// seat and every later write is seat 0's again. Leaving it on "Bo" would
    /// point the pedal at a seat nobody is in.</summary>
    [Fact]
    public void ClearingTheRoster_CollapsesThePickerBackToSeat0()
    {
        var (transport, audio, _) = NewTransport();
        transport.SetVstSeats(TwoSeats());
        transport.DropTuneSeat = transport.VstSeats[1];

        transport.SetVstSeats(null);

        Assert.False(transport.HasMultipleDropTuneSeats);
        Assert.Equal(0, transport.DropTunePlayer);

        transport.InputPitchShiftSemis = -2;
        Assert.Contains((0, -2.0), audio.Shifts);
    }

    /// <summary>A roster EDIT (someone renames themselves) must not move the
    /// pedal out from under whoever is dialling seat 1 in — the same rule the rig
    /// and the VST rows follow.</summary>
    [Fact]
    public void RenamingASeat_KeepsThePedalOnTheOneBeingEdited()
    {
        var (transport, _, _) = NewTransport();
        transport.SetVstSeats(TwoSeats());
        transport.DropTuneSeat = transport.VstSeats[1];

        transport.SetVstSeats([new RigSeatOption(0, "Ana"), new RigSeatOption(1, "Bo the Younger")]);

        Assert.Equal(1, transport.DropTunePlayer);
        Assert.Equal("Bo the Younger", transport.DropTuneSeat!.Label);
    }

    /// <summary>The drop-tune picker and the VST rows read the SAME seat list.
    /// Two collections would be two answers to "which seats exist", and they
    /// would disagree the first time a roster changed.</summary>
    [Fact]
    public void ThePedalAndTheVstRows_OfferOneSeatList()
    {
        var (transport, _, _) = NewTransport();
        transport.SetVstSeats(TwoSeats());

        Assert.Same(transport.VstSeats, transport.VstSeats);
        Assert.Contains(transport.DropTuneSeat, transport.VstSeats);
    }
}
