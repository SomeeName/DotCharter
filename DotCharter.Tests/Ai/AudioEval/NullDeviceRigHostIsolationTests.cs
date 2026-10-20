using System.Runtime.InteropServices;
using DotCharter.Services.Ai.Match;
using JuceSharp;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     #85: the offline rig host must not put the headless "Null" device into a LIVE
///     engine. It used to set the process-wide <c>DCA_ENABLE_NULL_DEVICE</c> around its
///     own engine's construction, and since #67 it is built during ordinary play — so a
///     live-engine rebuild on another thread inside that window (device change, NAM
///     48 kHz request, watchdog) registered the Null type in the player's device list.
///     The host now asks for the device through its engine CONFIG
///     (<see cref="JuceEngineConfig.EnableNullDevice" />, ABI v41), which no other
///     engine can see.
///     <para>
///         The "live engine" here asks for the <c>"Null"</c> output type WITHOUT the flag:
///         it constructs only if the type leaked into its device manager, and it opens no
///         real device either way, so the test is deterministic and needs no sound card.
///     </para>
/// </summary>
[Collection("NativeEngine")]
public sealed class NullDeviceRigHostIsolationTests : IDisposable
{
    private const string EnableVar = "DCA_ENABLE_NULL_DEVICE";
    private const string ManualVar = "DCA_NULL_DEVICE_MANUAL";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly string? _outerEnable = Environment.GetEnvironmentVariable(EnableVar);
    private readonly string? _outerManual = Environment.GetEnvironmentVariable(ManualVar);

    public NullDeviceRigHostIsolationTests()
    {
        // The env path must be OFF, or the "live" engine gets the type from it and the
        // test proves nothing about the host.
        Environment.SetEnvironmentVariable(EnableVar, null);
        Environment.SetEnvironmentVariable(ManualVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnableVar, _outerEnable);
        Environment.SetEnvironmentVariable(ManualVar, _outerManual);
    }

    private static bool NativeLoadable()
        => NativeLibrary.TryLoad("juce_sharp", typeof(JuceAudioEngine).Assembly, null, out _);

    /// <summary>An engine that asks for the Null type without the flag — what a live
    /// engine's device manager would list. Null when the type does not exist for it.</summary>
    private static JuceAudioEngine? TryBuildLiveEngineOnTheNullType()
    {
        try
        {
            return new JuceAudioEngine(new JuceEngineConfig { OutputTypeName = "Null", EnableConsoleLog = false });
        }
        catch (JuceAudioException)
        {
            return null;
        }
    }

    [Fact]
    public async Task ALiveEngineBuilt_WhileTheOfflineHostIsBeingConstructed_DoesNotGetTheNullType()
    {
        Assert.SkipUnless(NativeLoadable(), "juce_sharp.dll is not loadable here");
        var ct = TestContext.Current.CancellationToken;

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JuceEngineConfig? asked = null;
        using var host = new NullDeviceRigHost(null, config =>
        {
            asked = config;
            try
            {
                // The offline engine is built — and the host's construction is then HELD
                // OPEN here, which is exactly the window the env var used to be set in.
                var engine = new JuceAudioEngine(config);
                entered.Set();
                release.Wait(Bound);
                return engine;
            }
            finally
            {
                entered.Set();
            }
        });

        var offline = Task.Run(() => host.IsAvailable, ct);
        JuceAudioEngine? live = null;
        string? leakedTypes = null;
        try
        {
            Assert.True(entered.Wait(Bound, ct), "the offline host never reached its engine construction");
            Assert.False(offline.IsCompleted, "the seam did not hold the offline construction open");

            live = TryBuildLiveEngineOnTheNullType();
            if (live is not null)
            {
                leakedTypes = string.Join(", ",
                    live.EnumerateOutputDevicesLive().Select(d => $"{d.TypeName}/{d.Name}").Distinct());
            }
        }
        finally
        {
            release.Set();
            live?.Dispose();
        }

        var available = await offline.WaitAsync(Bound, ct);
        Assert.True(available, "the offline host is unavailable: " + host.Describe());
        Assert.NotNull(asked);
        Assert.True(asked!.EnableNullDevice, "the offline engine no longer asks for the Null device per engine");
        Assert.True(leakedTypes is null,
            "a live engine built while the offline rig host was constructing could open the \"Null\" " +
            "output type — the host leaked it through process state (#85). Live engine types: " + leakedTypes);
        Assert.Null(Environment.GetEnvironmentVariable(EnableVar));
    }

    /// <summary>The host is available with the env var cleared — it needs nothing from
    /// the environment — and leaves the environment exactly as it found it.</summary>
    [Fact]
    public void TheHost_IsAvailableWithoutTheEnvVar_AndNeverWritesIt()
    {
        Assert.SkipUnless(NativeLoadable(), "juce_sharp.dll is not loadable here");

        using var host = new NullDeviceRigHost();
        Assert.True(host.IsAvailable, "the offline host is unavailable without the env var: " + host.Describe());
        Assert.Null(Environment.GetEnvironmentVariable(EnableVar));
        Assert.Null(Environment.GetEnvironmentVariable(ManualVar));
    }
}
