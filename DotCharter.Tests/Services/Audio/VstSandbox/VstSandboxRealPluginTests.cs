using System.Diagnostics;
using System.Runtime.Versioning;
using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// Opt-in stress validation against REAL third-party plugins installed on
/// the developer machine (BIAS FX 2, Ample Guitar). Skipped unless
/// <c>DOTCHARTER_SANDBOX_REALPLUGIN_TESTS=1</c> — loading multi-gigabyte
/// amp sims has no place in a default test run, but this is the evidence
/// basis for the out-of-process hosting feature: load a real chain, stream
/// audio + MIDI through the bridge, hard-kill the child mid-stream, and
/// prove the parent side never blocks or corrupts.
/// </summary>
[SupportedOSPlatform("windows")]
public class VstSandboxRealPluginTests
{
    private const string BiasPath = @"C:\Program Files\Common Files\VST3\BIAS FX 2_x64.vst3";
    private const string AgpfPath = @"C:\Program Files\Common Files\VST3\AGPF.vst3";

    private static void RequireOptIn()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows only.");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_SANDBOX_REALPLUGIN_TESTS") != "1",
            "Set DOTCHARTER_SANDBOX_REALPLUGIN_TESTS=1 to run real-plugin sandbox stress tests.");
        Assert.SkipWhen(VstSandboxHost.FindExecutable() == null, "juce_vst_host.exe not deployed.");
    }

    [Fact]
    public async Task RealChainLoadsProcessesAndSurvivesHardKill()
    {
        RequireOptIn();
        Assert.SkipWhen(!File.Exists(BiasPath) && !Directory.Exists(BiasPath), "BIAS FX 2 not installed.");
        Assert.SkipWhen(!File.Exists(AgpfPath) && !Directory.Exists(AgpfPath), "Ample Guitar (AGPF) not installed.");

        using var host = new VstSandboxHost();
        using var shm = new VstSandboxSharedMemory(
            host.ShmName, host.RequestEventName, host.ResponseEventName, sampleRate: 48000);
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Exited += requested => exited.TrySetResult(requested);

        await host.StartAsync(48000, 480);

        // --- load the guitar chain (BIAS FX 2) ------------------------------
        var slot = Guid.NewGuid().ToString();
        var load = await host.SendAsync(new VstSandboxRequest { Op = "load", Slot = slot, Path = BiasPath },
                                        TimeSpan.FromSeconds(240));
        Assert.True(load.Ok, $"BIAS FX 2 load failed: {load.Error}");

        // --- load the instrument (Ample Guitar) -----------------------------
        var instr = await host.SendAsync(new VstSandboxRequest { Op = "loadInstrument", Path = AgpfPath },
                                         TimeSpan.FromSeconds(240));
        Assert.True(instr.Ok, $"AGPF load failed: {instr.Error}");

        // Ample Guitar streams its sample library in asynchronously after
        // prepareToPlay — give it a moment before expecting audible output.
        await Task.Delay(5000, TestContext.Current.CancellationToken);

        // --- stream audio + MIDI through the bridge --------------------------
        const int frames = 480;
        var left = new float[frames];
        var right = new float[frames];
        // NoteOn E3 re-triggered every second (mirrors the app's per-string
        // channel scheme loosely; channel 0 suffices for audibility).
        var noteOn = new[] { VstSandboxLayout.PackMidi(0x90, 52, 110) };
        bool sawChain = false, sawInstrument = false;
        var instrEnergy = 0f;
        var iL = new float[frames];
        var iR = new float[frames];

        for (var block = 0; block < 1200 && instrEnergy <= 0f; block++)
        {
            for (var i = 0; i < frames; i++)
            {
                var t = (block * frames + i) / 48000.0;
                left[i] = right[i] = 0.25f * (float)Math.Sin(2 * Math.PI * 110.0 * t);
            }
            var sendNote = block % 100 == 10;
            var ok = shm.ProcessBlock(left, right, frames,
                                      sendNote ? noteOn : null, sendNote ? 1 : 0,
                                      out var chainActive, out var instrumentActive,
                                      timeoutMs: 250);
            // Pace roughly at real time (10 ms blocks) so the sampler's disk
            // streaming can keep up like it would against a device callback.
            await Task.Delay(5, TestContext.Current.CancellationToken);
            if (!ok) continue; // startup blocks may time out while buffers spin up
            sawChain |= chainActive;
            if (instrumentActive)
            {
                sawInstrument = true;
                shm.ReadInstrumentOutput(iL, iR, frames);
                for (var i = 0; i < frames; i++) instrEnergy += Math.Abs(iL[i]);
            }
            foreach (var s in left) Assert.False(float.IsNaN(s) || float.IsInfinity(s), "chain produced NaN/Inf");
        }

        Assert.True(sawChain, "BIAS FX 2 never processed a block through the bridge");
        Assert.True(sawInstrument, "AGPF never rendered a block through the bridge");
        Assert.True(instrEnergy > 0.0f, "AGPF output stayed digitally silent after a NoteOn");

        // --- hard-kill mid-stream (the BIAS-crash simulation) ----------------
        var childPid = host.ChildProcessId!.Value;
        using (var child = Process.GetProcessById(childPid))
        {
            child.Kill(); // == taskkill /f
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        var requested = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(requested, "hard kill must surface as a crash");

        // Parent side must degrade gracefully: bounded dry timeout, no throw.
        var sw = Stopwatch.StartNew();
        var alive = shm.ProcessBlock(left, right, frames, null, 0, out _, out _, timeoutMs: 100);
        sw.Stop();
        Assert.False(alive);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }
}
