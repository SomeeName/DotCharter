using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The seat count has to have EXACTLY ONE owner, and the resting value has to
///     come from the input configuration.
///     <para>
///         Both halves were broken and each caused the other's symptom: the
///         seat count once had a single run-scoped writer, so two guitars on
///         inputs 0 and 1 came out one per ear outside that run, and its exit
///         hard-coded ONE seat, so an override could silently undo the user's
///         input setting.
///     </para>
/// </summary>
public sealed class LiveInputRoutingWiringTests
{
    // ---------------------------------------------------------- the two layers

    private static AppConfig TwoGuitars() => new()
    {
        EnableInput = true,
        AudioDriver = "ASIO",
        AsioInputMono = false,
        AsioInputChannelLeft = 0,
        AsioInputChannelRight = 1,
        InputChannelsAreSeparateGuitars = true
    };

    private static PlayerInputRoutingState FreshState() => new(DspPlayers.MaxPlayers);

    /// <summary>THE POINT: outside any override, the effective seat count is
    /// whatever the INPUT CONFIGURATION asks for.</summary>
    [Fact]
    public void OutsideARun_TheSeatCountComesFromTheInputConfiguration()
    {
        var state = FreshState();

        state.AdoptBaseline(LiveInputSeatLayout.FromInputConfig(TwoGuitars()));

        Assert.Equal(2, state.PlayerCount);
        Assert.Equal(0, state.Channels[0]);
        Assert.Equal(1, state.Channels[1]);
        // …and it is no longer the engine's own default, which is what makes the
        // rebuild replay re-assert it after a device change.
        Assert.False(state.IsDefault);
    }

    /// <summary>A shipped-default configuration asks for nothing, so the ordinary
    /// solo path still costs no engine calls and a pre-v28 DLL is never poked.</summary>
    [Fact]
    public void TheShippedMonoConfiguration_LeavesTheEngineOnItsOwnDefaults()
    {
        var state = FreshState();

        state.AdoptBaseline(LiveInputSeatLayout.FromInputConfig(new AppConfig()));

        Assert.Equal(1, state.PlayerCount);
        Assert.True(state.IsDefault);
    }

    /// <summary>An override wins over the configuration while it is in force —
    /// it is the more specific statement about which input to listen to.</summary>
    [Fact]
    public void ARunsRoster_OverridesTheConfiguredBaseline()
    {
        var state = FreshState();
        state.AdoptBaseline(LiveInputSeatLayout.FromInputConfig(TwoGuitars()));

        state.AdoptOverride(new LiveInputSeatLayout(2, [1, 0], [0.5f, 1f]));

        Assert.True(state.HasOverride);
        Assert.Equal(1, state.Channels[0]);
        Assert.Equal(0.5f, state.MonitorGains[0]);
    }

    /// <summary>A device change, the NAM 48 kHz request or a watchdog recovery
    /// re-runs the whole config apply — MID-RUN. It records the new baseline and
    /// leaves the run's routing exactly where it was; ending an override's routing
    /// halfway through a song is the bug <c>PlayerInputRoutingState</c> exists to
    /// prevent.</summary>
    [Fact]
    public void AConfigApplyDuringARun_DoesNotDisturbTheRun()
    {
        var state = FreshState();
        state.AdoptOverride(new LiveInputSeatLayout(2, [1, 0]));

        state.AdoptBaseline(LiveInputSeatLayout.FromInputConfig(new AppConfig()));

        Assert.Equal(2, state.PlayerCount);
        Assert.Equal(1, state.Channels[0]);
        // …but the new baseline IS what the run hands back to.
        state.ClearOverride();
        Assert.Equal(1, state.PlayerCount);
    }

    /// <summary>…and when the run ends it goes back to the CONFIGURATION, not to
    /// one seat. A hard-coded 1 would have silently undone a two-guitar setup.</summary>
    [Fact]
    public void WhenTheRunEnds_ItGoesBackToTheConfiguration_NotToOneSeat()
    {
        var state = FreshState();
        state.AdoptBaseline(LiveInputSeatLayout.FromInputConfig(TwoGuitars()));
        state.AdoptOverride(new LiveInputSeatLayout(2, [1, 0], [0.4f, 1f]));

        state.ClearOverride();

        Assert.False(state.HasOverride);
        Assert.Equal(2, state.PlayerCount);
        Assert.Equal(0, state.Channels[0]);
        Assert.Equal(PlayerInputRoutingState.DefaultMonitorGain, state.MonitorGains[0]);
    }

    // ----------------------------------------------------- override composition

    /// <summary>Records what an override does to the routing, and what the
    /// input configuration would hand it back to.</summary>
    private sealed class RoutingSpy : IPlayerInputRouting
    {
        private readonly PlayerInputRoutingState _state = new(DspPlayers.MaxPlayers);

        public LiveInputSeatLayout Baseline { get; init; } = LiveInputSeatLayout.Single;

        public int PlayerCount => _state.PlayerCount;
        public IReadOnlyList<int> Channels => _state.Channels;

        public void SetLiveInputOverride(LiveInputSeatLayout layout) => _state.AdoptOverride(layout);

        public void RestoreLiveInputBaseline()
        {
            _state.AdoptBaseline(Baseline);
            _state.ClearOverride();
        }

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

        public int ReadPlayerInputCaptureWindow(int player, float[] destination) => 0;

        public int LiveInputPlayerCount => 1;

        public void SetPlayerInputPitchShift(int player, double semitones) { }
        public void SetPlayerInputPitchShiftMode(int player, int mode) { }
        public bool SupportsPerPlayerDropTune => true;

        public float GetAndResetPlayerInputPeak(int player) => 0f;
    }

    /// <summary>
    ///     An override ended on a two-guitar rig leaves TWO seats, because that
    ///     is what the owner's inputs are set to. An override (the latency
    ///     wizard's channel probe) is layered over the configured layout and
    ///     hands the engine back rather than resetting it.
    /// </summary>
    [Fact]
    public void AnOverride_HandsBackTheConfiguredLayout()
    {
        var routing = new RoutingSpy
        {
            Baseline = LiveInputSeatLayout.FromInputConfig(TwoGuitars())
        };

        routing.SetLiveInputOverride(new LiveInputSeatLayout(2, [1, 0]));
        Assert.Equal(2, routing.PlayerCount);
        Assert.Equal(1, routing.Channels[0]);

        routing.RestoreLiveInputBaseline();

        Assert.Equal(2, routing.PlayerCount);
        Assert.Equal(0, routing.Channels[0]);
    }

    // ---------------------------------------------------------- one mechanism

    /// <summary>
    ///     The engine's seat count is written from exactly ONE place. A second
    ///     writer is how the two halves above come apart: the config apply and a
    ///     run-scoped override would each believe they own the value, and last-writer-wins
    ///     would decide what the player hears.
    /// </summary>
    [Fact]
    public void OnlyOneProductFileWritesTheEnginesSeatCount()
    {
        var writers = ProductSourceFiles()
            .Where(f => Regex.IsMatch(SourceText.StripComments(UiSourceTree.ReadAllText(f)), @"\.SetPlayerCount\s*\("))
            .Select(f => Path.GetFileName(f)!)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["JuceAudioPlaybackService.PlayerRouting.cs"], writers);
    }

    /// <summary>
    ///     …and the layout it writes comes from the input CONFIGURATION, at the
    ///     one point every producer of an engine config passes through: startup,
    ///     a settings apply and the watchdog's recovery rebuild all reach
    ///     <c>ApplyAudioConfigurationCore</c>. A UI call site instead would be one
    ///     a fourth entry point could forget, which is the shape
    ///     the removed couch co-op applier shipped in.
    ///     <para>
    ///         A source lint rather than a behavioural one on purpose: the
    ///         behavioural seam is <c>ApplyAudioConfiguration</c>, which opens a
    ///         real audio device — and a test host that opens one holds it
    ///         exclusively. The layout it passes IS covered behaviourally, by
    ///         <see cref="LiveInputSeatLayoutTests" /> and the layer tests above.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheConfigApplyIsWhatSetsTheBaseline()
    {
        var source = SourceText.StripComments(UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Tone", "Audio", "JuceAudioPlaybackService.EngineConfig.cs")));

        Assert.Contains("SetLiveInputBaseline(LiveInputSeatLayout.FromInputConfig(config))", source);
    }

    /// <summary>The settings apply has to REACH that path. A seat-layout change
    /// opens no new channel, so the routing-changed guard would have let the user
    /// tick "two guitars" and hear nothing change until the next launch.</summary>
    [Fact]
    public void TheSettingsApply_TreatsASeatLayoutChangeAsARoutingChange()
    {
        var folder = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Presentation", "Screens", "Settings");
        var source = SourceText.StripComments(
            UiSourceTree.ReadAllText(Path.Combine(folder, "AudioSettingsRules.cs")) +
            UiSourceTree.ReadAllText(Path.Combine(folder, "AudioSettingsViewModel.cs")));

        Assert.Contains(
            "oldCfg.InputChannelsAreSeparateGuitars != newCfg.InputChannelsAreSeparateGuitars",
            source);
        Assert.Contains("config.InputChannelsAreSeparateGuitars =", source);
    }

    // -------------------------------------------------------------- the plumbing

    private static IEnumerable<string> ProductSourceFiles()
    {
        foreach (var project in new[] { "DotCharter.Services", "DotCharter.Tone", "DotCharter.UI", "DotCharter.Core" })
        {
            var root = Path.Combine(TestPaths.RepoRoot(), project);
            if (!Directory.Exists(root)) continue;
            foreach (var file in UiSourceTree.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }
}
