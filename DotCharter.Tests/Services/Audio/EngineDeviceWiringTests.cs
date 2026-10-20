using System.Diagnostics;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Engine;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Four device-facing wiring faults in <c>JuceAudioPlaybackService</c>, each
///     of which the app carried silently because the failure is "a capability
///     quietly stopped existing" rather than an exception:
///     the streaming mirror handed a UI LABEL instead of a device name; a driver
///     fallback that dropped the CHANNEL COUNTS and so opened no capture channels
///     at all; a <c>DeviceBufferFramesChanged</c> event nothing ever raised; and
///     the "Process priority" switch, which no line of the app read.
///     <para>
///         The pure halves are driven directly. The two wiring invariants are
///         asserted on the SOURCE, in the style of
///         <see cref="EngineConfigPolicyTests" />, because reaching them at
///         runtime needs a real audio device — and this suite must never create
///         one (an ASIO open is exclusive and a stranded test host holds the
///         machine's sound card).
///     </para>
/// </summary>
public sealed class EngineDeviceWiringTests
{
    // ── The streaming mirror's device name ────────────────────────────────────

    /// <summary>
    ///     The mirror dropdown is filled from <c>GetWasapiOutputDevices()</c>,
    ///     i.e. from <c>LabelWasapiModes</c>, so a device JUCE registers under
    ///     more than one WASAPI type reaches the config with a MODE SUFFIX. The
    ///     native <c>MirrorOutput::open</c> assigns the string straight to
    ///     <c>AudioDeviceSetup.outputDeviceName</c>, so the open failed with "No
    ///     such device" and the mirror stayed off — for exactly the users whose
    ///     interface exposes several modes, and with only a warning in the log.
    /// </summary>
    [Theory]
    [InlineData("Speakers (Realtek) (Exclusive)", "Speakers (Realtek)")]
    [InlineData("Speakers (Realtek) (Low Latency)", "Speakers (Realtek)")]
    [InlineData("Speakers (Realtek) (Shared)", "Speakers (Realtek)")]
    [InlineData("VoiceMeeter Input", "VoiceMeeter Input")]
    public void ResolveMirrorDeviceName_HandsTheEngineABareDeviceName(string saved, string expected)
    {
        var cfg = new AppConfig { EnableStreamingMirror = true, StreamingMirrorDeviceName = saved };
        Assert.Equal(expected, JuceAudioPlaybackService.ResolveMirrorDeviceName(cfg));
    }

    /// <summary>"Off" and "nothing picked" are both a CLOSED mirror, and the
    /// engine's own contract is that an empty name closes it — so neither may
    /// come back as a name.</summary>
    [Fact]
    public void ResolveMirrorDeviceName_IsNullWhenTheMirrorIsOffOrUnset()
    {
        Assert.Null(JuceAudioPlaybackService.ResolveMirrorDeviceName(
            new AppConfig { EnableStreamingMirror = false, StreamingMirrorDeviceName = "VB-Cable" }));
        Assert.Null(JuceAudioPlaybackService.ResolveMirrorDeviceName(
            new AppConfig { EnableStreamingMirror = true, StreamingMirrorDeviceName = "  " }));
    }

    // ── The driver-fallback ladder ────────────────────────────────────────────

    /// <summary>
    ///     Every fallback rung must still ask for the same number of INPUT and
    ///     OUTPUT channels. All of them clear both device ids, which sends the
    ///     native side down
    ///     <c>initialiseWithDefaultDevices(inputChannels, outputChannels)</c> —
    ///     and it reads <c>input_channels &lt;= 0</c> as "open no capture channels
    ///     at all". The WASAPI and Default rungs left the counts at 0, so an ASIO
    ///     interface that was offline at launch cost the user their whole live
    ///     input: no tuner, no meter, no judged run, and nothing in the log but
    ///     "started on fallback driver".
    /// </summary>
    [Fact]
    public void EveryFallbackAttempt_KeepsTheChannelCounts()
    {
        var cfg = new JuceEngineConfig
        {
            PreferredDriver = AudioDriver.Asio,
            OutputDeviceId = "Focusrite USB ASIO",
            InputDeviceId = "Focusrite USB ASIO",
            SampleRate = 48_000,
            BufferFrames = 256,
            OutputChannels = 2,
            InputChannels = 2,
            OutputChannelMask = 0b0011,
            InputChannelMask = 0b0110,
        };

        var attempts = JuceAudioPlaybackService.BuildEngineAttempts(cfg);

        Assert.Equal(4, attempts.Count); // requested, default-ASIO, WASAPI, Default
        foreach (var a in attempts)
        {
            Assert.Equal(cfg.OutputChannels, a.OutputChannels);
            Assert.Equal(cfg.InputChannels, a.InputChannels);
        }
    }

    /// <summary>
    ///     The non-vacuous inverse, and the reason the masks are NOT carried: a
    ///     mask names PHYSICAL channels of the device the user picked, and every
    ///     rung below the first is a different device that may not have them.
    ///     Only the first attempt — the user's own configuration — keeps them.
    /// </summary>
    [Fact]
    public void OnlyTheRequestedAttempt_CarriesTheChannelMasksAndTheDeviceIds()
    {
        var cfg = new JuceEngineConfig
        {
            PreferredDriver = AudioDriver.Asio,
            OutputDeviceId = "Focusrite USB ASIO",
            InputDeviceId = "Focusrite USB ASIO",
            OutputChannels = 2,
            InputChannels = 1,
            OutputChannelMask = 0b1100,
            InputChannelMask = 0b0010,
        };

        var attempts = JuceAudioPlaybackService.BuildEngineAttempts(cfg);

        Assert.Same(cfg, attempts[0]);
        foreach (var a in attempts.Skip(1))
        {
            Assert.Equal(0u, a.OutputChannelMask);
            Assert.Equal(0u, a.InputChannelMask);
            Assert.Equal(string.Empty, a.OutputDeviceId);
            Assert.Equal(string.Empty, a.InputDeviceId);
        }
    }

    /// <summary>A WASAPI configuration has exactly one rung below it, and it too
    /// keeps the counts.</summary>
    [Fact]
    public void AWasapiConfiguration_FallsBackOnceAndKeepsItsChannelCounts()
    {
        var cfg = new JuceEngineConfig
        {
            PreferredDriver = AudioDriver.Wasapi,
            OutputDeviceId = "Speakers",
            OutputChannels = 2,
            InputChannels = 1,
        };

        var attempts = JuceAudioPlaybackService.BuildEngineAttempts(cfg);

        Assert.Equal(2, attempts.Count);
        Assert.Equal(AudioDriver.Default, attempts[1].PreferredDriver);
        Assert.Equal(2, attempts[1].OutputChannels);
        Assert.Equal(1, attempts[1].InputChannels);
    }

    // ── DeviceBufferFramesChanged ─────────────────────────────────────────────

    /// <summary>A first real size is news; repeating it is not; a change is.</summary>
    [Fact]
    public void TheBufferReporter_ReportsEachRealChangeOnce()
    {
        var reporter = new DeviceBufferReporter();

        Assert.True(reporter.TryObserve(256));
        Assert.False(reporter.TryObserve(256));
        Assert.True(reporter.TryObserve(512));
        Assert.Equal(512, reporter.LastReported);
    }

    /// <summary>
    ///     A non-positive size is "no device open" — between engines, after a
    ///     failed bring-up, on a dead ASIO handle. It must never be published:
    ///     both subscribers snap the value through <c>NormalizeBufferSize</c>,
    ///     which answers the CONFIG DEFAULT for a non-positive input, so one
    ///     transient 0 would silently rewrite the user's buffer row.
    /// </summary>
    [Fact]
    public void TheBufferReporter_NeverReportsAClosedDevice()
    {
        var reporter = new DeviceBufferReporter();
        reporter.TryObserve(256);

        Assert.False(reporter.TryObserve(0));
        Assert.False(reporter.TryObserve(-1));
        Assert.Equal(256, reporter.LastReported);
    }

    /// <summary>A NEW device's block size is news even when it equals the old
    /// one's — the subscribers have been shown nothing since the rebuild.</summary>
    [Fact]
    public void TheBufferReporter_ForgetsAcrossAnEngineRebuild()
    {
        var reporter = new DeviceBufferReporter();
        reporter.TryObserve(256);

        reporter.Forget();

        Assert.True(reporter.TryObserve(256));
    }

    /// <summary>
    ///     The wiring half. <c>DeviceBufferFramesChanged</c> was declared with a
    ///     <c>#pragma warning disable CS0067</c> — the compiler had already
    ///     reported that nothing raised it — while Settings ▸ Audio's buffer
    ///     dropdown and the status bar both subscribed. All three moments a
    ///     device's block size can change must publish it, and the pragma must
    ///     stay gone: with warnings-as-errors, its absence is what makes deleting
    ///     the invoke a build failure rather than a silent regression.
    /// </summary>
    [Fact]
    public void TheBufferEvent_IsRaisedFromEveryPlaceTheDeviceCanChangeIt()
    {
        var src = ServiceSource();

        Assert.Contains("DeviceBufferFramesChanged?.Invoke(", src, StringComparison.Ordinal);
        // A real directive, not the several comments that now EXPLAIN why it is
        // gone — the same reason every source lint here strips comments first.
        Assert.DoesNotContain(
            src.Split('\n').Select(l => l.Trim()),
            l => l.StartsWith("#pragma warning disable CS0067", StringComparison.Ordinal));

        Assert.Contains("ReportDeviceBufferFrames(engine);",
            MethodBody("private void ApplyEngineConfigGated(JuceEngineConfig cfg, bool allowReplaceFallback)"),
            StringComparison.Ordinal);
        Assert.Contains("ReportDeviceBufferFrames(engine);",
            MethodBody("private void ReplaceEngineGated(JuceEngineConfig cfg)"),
            StringComparison.Ordinal);
        Assert.Contains("ReportDeviceBufferFrames(e);",
            MethodBody("public bool TryOpenAsioControlPanel(IntPtr parentWindowHandle, out string? error)"),
            StringComparison.Ordinal);
    }

    // ── The "Process priority" switch ─────────────────────────────────────────

    /// <summary>The switch has two positions and both are defined values —
    /// "leave it wherever it happens to be" would make it one-way.</summary>
    [Fact]
    public void ProcessPriority_HasBothPositions()
    {
        Assert.Equal(ProcessPriorityPolicy.Raised, ProcessPriorityPolicy.DesiredFor(true));
        Assert.Equal(ProcessPriorityPolicy.Resting, ProcessPriorityPolicy.DesiredFor(false));
        Assert.NotEqual(ProcessPriorityPolicy.Raised, ProcessPriorityPolicy.Resting);
    }

    /// <summary>
    ///     Only a class this switch itself produces may be overwritten. Startup,
    ///     every settings apply and every watchdog recovery rebuild run through
    ///     the caller, so writing unconditionally would be a kernel call each
    ///     time — and would stamp on a process a launcher deliberately parked at
    ///     <c>RealTime</c> or <c>Idle</c>, neither of which this switch can mean.
    /// </summary>
    [Theory]
    [InlineData(ProcessPriorityClass.Normal, true, true)]
    [InlineData(ProcessPriorityClass.AboveNormal, false, true)]
    [InlineData(ProcessPriorityClass.Normal, false, false)]
    [InlineData(ProcessPriorityClass.AboveNormal, true, false)]
    [InlineData(ProcessPriorityClass.RealTime, true, false)]
    [InlineData(ProcessPriorityClass.Idle, false, false)]
    public void ProcessPriority_IsWrittenOnlyWhenItWouldChangeAValueThisSwitchOwns(
        ProcessPriorityClass current, bool enableHigh, bool expected)
    {
        Assert.Equal(expected,
            ProcessPriorityPolicy.ShouldApply(current, ProcessPriorityPolicy.DesiredFor(enableHigh)));
    }

    /// <summary>
    ///     …and the wiring, which is the whole bug:
    ///     <c>AppConfig.EnableHighProcessPriority</c> shipped ON by default, was
    ///     loaded and saved by <c>AudioSettingsViewModel</c>, and was READ by
    ///     nothing at all. Its row promises "Helps avoid glitches / dropouts when
    ///     running a VST effect", which is a capability the app could not honour
    ///     while showing it as live. It belongs in
    ///     <c>ApplyAudioConfigurationCore</c> for the reason the live-seat
    ///     baseline does: startup, every settings apply and the watchdog's
    ///     recovery rebuild all reach that one method.
    /// </summary>
    [Fact]
    public void TheProcessPriorityRow_IsAppliedByTheConfigApply()
    {
        var body = MethodBody("private void ApplyAudioConfigurationCore(RigConfig config, bool forceEngineReplace)");
        Assert.Contains("ApplyProcessPriority(config.EnableHighProcessPriority);", body, StringComparison.Ordinal);
        Assert.Contains("ProcessPriorityPolicy.DesiredFor(", ServiceSource(), StringComparison.Ordinal);
    }

    // ── source helpers (mirrors EngineConfigPolicyTests) ──────────────────────

    private static string MethodBody(string signature)
    {
        var src = ServiceSource();
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}' — has it been renamed?");
        var open = src.IndexOf('{', start + signature.Length);
        var depth = 0;
        var i = open;
        for (; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) break;
        }

        return src[open..Math.Min(i + 1, src.Length)];
    }

    /// <summary>Every `JuceAudioPlaybackService*.cs` partial and its extracted
    /// collaborators (<see cref="PlaybackServiceSourceFiles" />), concatenated in a
    /// stable order — derived from the directory so a new partial is covered the
    /// day it appears, and so moving a method between partials cannot make this
    /// lint vacuous.</summary>
    private static string ServiceSource() =>
        string.Concat(PlaybackServiceSourceFiles.All().Select(File.ReadAllText));
}
