using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The per-player live-input routing (ABI v28) is engine state that is NOT
///     part of <c>JuceEngineConfig</c>, so nothing restored it when the engine
///     was rebuilt: a device change, the NAM 48 kHz request or a watchdog
///     recovery mid-song put <c>playerCount_</c> back to 1 and every seat on its
///     default channel, and couch co-op's routing silently ended while the split
///     highway, the HUD and the setup screen all carried on saying it was live.
/// </summary>
public sealed class PlayerInputRoutingStateTests
{
    [Fact]
    public void AFreshStateIsTheEnginesOwnDefault_SoNothingIsEverReAsserted()
    {
        var state = new PlayerInputRoutingState(DspPlayers.MaxPlayers);

        Assert.Equal(1, state.PlayerCount);
        // Seat p defaults to channel p, which is what the native engine
        // initialises inputCaptureChannel_ to. That equality is what makes the
        // solo path cost zero P/Invokes across a device change — and what keeps
        // a pre-v28 DLL from being asked for exports it does not have.
        for (var p = 0; p < DspPlayers.MaxPlayers; p++)
            Assert.Equal(p, state.Channels[p]);
        Assert.True(state.IsDefault);
    }

    [Fact]
    public void ARoutedCoopRunIsRememberedSoARebuildCanReplayIt()
    {
        var state = new PlayerInputRoutingState(DspPlayers.MaxPlayers);

        state.RecordPlayerCount(2);
        state.RecordChannel(0, 0);
        state.RecordChannel(1, 1);

        Assert.False(state.IsDefault);
        Assert.Equal(2, state.PlayerCount);
        Assert.Equal(0, state.Channels[0]);
        Assert.Equal(1, state.Channels[1]);
    }

    [Fact]
    public void ASeatMovedOffItsDefaultChannelIsNotDefault_EvenAtOnePlayer()
    {
        // The solo half of the same bug: a run that put player 1 on input 2
        // leaves every later solo run listening to the wrong jack, so "one
        // player" alone is not enough to call the state clean.
        var state = new PlayerInputRoutingState(DspPlayers.MaxPlayers);
        state.RecordChannel(0, 1);

        Assert.Equal(1, state.PlayerCount);
        Assert.False(state.IsDefault);

        state.ResetToDefaults();
        Assert.True(state.IsDefault);
    }

    /// <summary>
    ///     The monitor trim is the THIRD mutating member of
    ///     <see cref="IPlayerInputRouting" /> and is engine state exactly like the
    ///     other two — written straight at the engine, carried by no config. It was
    ///     left out of this type when it was written, which left the invariant this
    ///     type exists for covering two thirds of its own surface: a device change
    ///     mid-run would put a balanced two-guitar monitor mix back to unity with
    ///     nothing on screen changing.
    /// </summary>
    [Fact]
    public void ABalancedMonitorMixIsRememberedSoARebuildCanReplayIt()
    {
        var state = new PlayerInputRoutingState(DspPlayers.MaxPlayers);

        // Unity everywhere is the engine's own start, so nothing is re-asserted.
        Assert.All(state.MonitorGains, g => Assert.Equal(PlayerInputRoutingState.DefaultMonitorGain, g));
        Assert.True(state.IsDefault);

        state.RecordMonitorGain(1, 0.5f);

        Assert.Equal(0.5f, state.MonitorGains[1]);
        // …and a trimmed seat alone is enough to make the state dirty, even with
        // one player on the default channel: otherwise the replay would skip it.
        Assert.Equal(1, state.PlayerCount);
        Assert.False(state.IsDefault);

        state.ResetToDefaults();
        Assert.Equal(PlayerInputRoutingState.DefaultMonitorGain, state.MonitorGains[1]);
        Assert.True(state.IsDefault);
    }

    [Fact]
    public void AnUnusableMonitorGainIsIgnoredRatherThanStored()
    {
        var state = new PlayerInputRoutingState(2);

        // Same rule as RecordChannel: this is reached from the co-op start path,
        // so an out-of-range seat must not throw. And a NaN/∞/negative gain would
        // be replayed onto every future engine, so it is refused rather than kept.
        state.RecordMonitorGain(-1, 0.5f);
        state.RecordMonitorGain(9, 0.5f);
        state.RecordMonitorGain(0, float.NaN);
        state.RecordMonitorGain(1, float.PositiveInfinity);
        state.RecordMonitorGain(1, -1f);

        Assert.Equal([1f, 1f], state.MonitorGains);
        Assert.True(state.IsDefault);
    }

    [Fact]
    public void TheCountIsClampedToTheSeatsThatExist()
    {
        var state = new PlayerInputRoutingState(2);

        state.RecordPlayerCount(99);
        Assert.Equal(2, state.PlayerCount);

        state.RecordPlayerCount(0);
        Assert.Equal(1, state.PlayerCount);

        // An out-of-range seat is ignored rather than throwing: this is reached
        // from the co-op start path, and arming a run must never be able to take
        // the audio engine down.
        state.RecordChannel(-1, 7);
        state.RecordChannel(5, 7);
        Assert.Equal([0, 1], state.Channels);
    }

    /// <summary>
    ///     The service half. The engine is native, so this is source-level: the
    ///     two places that point the DSP rigs at a LIVE engine must go through
    ///     the one helper that also replays the routing, and the two setters must
    ///     record before they touch the engine (co-op can arm before the engine
    ///     exists, or across a failed rebuild).
    /// </summary>
    [Fact]
    public void EveryLiveEngineAttach_AlsoReAssertsThePerPlayerRouting()
    {
        // EVERY partial of the service, concatenated: the class is split across a
        // dozen files, so pinning one filename makes the occurrence counts below
        // go vacuous the moment one of these methods moves to a neighbouring
        // partial. Ordinal order keeps the concatenation stable.
        var service = ServiceSource();

        // A bare attach to a LIVE engine is the bug: it re-points the tone rigs
        // and leaves the routing on the fresh engine's defaults. There is
        // exactly ONE such call left and it is inside the helper that also
        // replays the routing; the reconfigure and the rebuild both go through
        // that helper.
        Assert.Equal(1, Occurrences(service, "AttachDspChainsToEngine(engine);"));
        var helper = Between(service,
            "private void AttachPerPlayerStateToEngine(JuceAudioEngine engine)", "/// <summary>");
        Assert.Contains("AttachDspChainsToEngine(engine);", helper);
        Assert.Contains("WriteRoutingTo(engine,", helper);
        Assert.Equal(2, Occurrences(service, "AttachPerPlayerStateToEngine(engine);"));

        // The engine write itself is ONE method, shared by the rebuild replay
        // above and the live push, so the two can never drift into writing
        // different surfaces — and it is the only place in the app that reaches
        // the seat count at all (LiveInputRoutingWiringTests lints that).
        var writer = Between(service,
            "private void WriteRoutingTo(JuceAudioEngine engine, string reason)",
            "public int ReadPlayerInputCaptureStream(int player,");
        Assert.Contains("engine.SetPlayerCount(", writer);
        Assert.Contains("engine.SetPlayerInputChannel(player, _playerRouting.Channels[player])", writer);
        Assert.Contains("engine.SetPlayerMonitorGain(player, _playerRouting.MonitorGains[player])", writer);

        // …and the detach path is untouched: detaching is about the DSP rigs
        // only, and the ordering rule around it is load-bearing.
        Assert.Equal(3, Occurrences(service, "AttachDspChainsToEngine(null)"));

        // Both layers RECORD before they push, unconditionally: co-op can arm
        // before an engine exists (or across a failed rebuild), and the state is
        // what carries the layout onto the next engine either way.
        Assert.Contains("_playerRouting.AdoptBaseline(layout);", service);
        Assert.Contains("_playerRouting.AdoptOverride(layout);", service);
        Assert.Contains("_playerRouting.ClearOverride();", service);
    }

    private static string Between(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"end marker not found after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string ServiceSource() =>
        string.Concat(PlaybackServiceSourceFiles.All().Select(File.ReadAllText));
}
