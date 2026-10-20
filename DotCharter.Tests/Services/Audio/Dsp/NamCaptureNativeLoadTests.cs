using DotCharter.Tone.Audio.Dsp;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// REAL-engine inference-load verification for the five bundled <c>.nam</c> captures
/// (<c>DotCharter.UI/NamCaptures</c>). Mirrors <see cref="DspStringParamNativeRoundTripTests"/>:
/// a live <see cref="JuceAudioEngine"/> at 48 kHz with the real <c>JuceDspChainService</c>
/// over the native DSP ABI. Everything skips cleanly (<see cref="Assert.SkipWhen"/>) when the
/// native DLL / audio engine can't load OR the DLL was built without the <c>namamp</c> module
/// (<c>DOTCHARTER_DSP_NEURAL</c> off) — so it NEVER depends on native being present.
///
/// On a machine with the ABI-v23 DLL (namamp compiled in) this is the authoritative proof
/// that each bundled capture's DATA actually constructs a neural model on the real engine:
/// NeuralAudio only flips the poll-only <c>"loaded"</c> flag to 1 after it has successfully
/// parsed the modern single-file <c>.nam</c> JSON (version/architecture/config/weights) and
/// built the WaveNet/LSTM — a bad repack (weight count inconsistent with the config geometry,
/// missing keys) throws in the loader and leaves <c>loaded</c> at 0. We request a 48 kHz engine
/// but do NOT assert <c>"rateMismatch"</c>: whether the model rate matches the ENGINE rate
/// depends on what the audio DEVICE actually opened (a 48 kHz request is best-effort — many
/// default devices stay at 44.1 kHz), which is a device-config concern surfaced by the
/// pedal-card warning, not a property of the capture data.
///
/// NOTE on "wet ≠ dry": the DSP chain only runs on the live audio-device callback over the
/// captured guitar input — the ABI exposes no offline "process this block" entry, so there is
/// no deterministic way to inject a known block and read the wet tail from managed code. The
/// <c>loaded</c> flag IS the real gate here: it can only be 1 when the model was fully
/// constructed from the file's weights, which is exactly the "the data is valid / will make
/// sound" property under test.
/// </summary>
[Collection("NativeEngine")]
public sealed class NamCaptureNativeLoadTests
{
    private const string Namamp = "namamp";
    // A deadlock detector, never a budget: the poll returns the moment the model
    // reports loaded, and this lane shares the box with the gate's parallel phase.
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void EveryBundledCapture_LoadsOnTheRealEngine_At48k()
    {
        var bundleDir = FindBundleDir();
        Assert.SkipWhen(bundleDir is null, "Bundle dir not found (running outside a source tree).");

        var files = UiSourceTree.EnumerateFiles(bundleDir!, "*.nam")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(files.Count == 0, "No bundled .nam captures found.");

        using var env = NativeChainEnv.TryCreate48k();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");

        var svc = env!.Chain;
        Assert.SkipWhen(!svc.Catalog.Any(m => m.Id == Namamp),
            "native catalogue has no 'namamp' module (DLL built without DOTCHARTER_DSP_NEURAL)");

        var failures = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);

            // Fresh node per file so "loaded" starts at 0 and a 1 unambiguously means
            // THIS capture constructed a model (not a stale model from a prior file).
            var node = svc.AddNode(Namamp);
            if (node is null) { failures.Add($"{name}: AddNode(namamp) failed"); continue; }

            try
            {
                if (!svc.SetNodeString(node.NodeId, "modelPath", file))
                {
                    failures.Add($"{name}: SetNodeString(modelPath) rejected");
                    continue;
                }

                var loaded = PollFlag(svc, node.NodeId, "loaded", LoadTimeout);
                if (loaded != 1f)
                    failures.Add($"{name}: 'loaded' never reached 1 (last={loaded?.ToString() ?? "null"}) — model failed to construct");

                // NOTE: we deliberately do NOT assert rateMismatch==0 here. Whether the model
                // rate (48 kHz) matches the ENGINE rate depends on what the audio DEVICE
                // actually opened — a 48 kHz request is best-effort (see the 48 kHz policy in
                // JuceAudioPlaybackService); many default devices stay at 44.1 kHz and report
                // rateMismatch=1. That is a device-config concern surfaced by the pedal-card
                // warning, NOT a property of the capture DATA. 'loaded'==1 is the capture gate.
            }
            finally
            {
                svc.RemoveNode(node.NodeId);
            }
        }

        Assert.True(failures.Count == 0,
            "Bundled NAM capture(s) failed to load on the real engine:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>Polls a poll-only float param (e.g. namamp <c>"loaded"</c>) until it reaches 1
    /// or the timeout elapses. The model loads on the module's own worker thread, so this
    /// spins with a short sleep rather than assuming a synchronous load.</summary>
    private static float? PollFlag(IDspChainService svc, int nodeId, string paramId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        float? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = svc.GetParam(nodeId, paramId);
            if (last is 1f) return 1f;
            Thread.Sleep(50);
        }
        return last;
    }

    private static string? FindBundleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;
        if (dir == null) return null;
        var bundle = UiSourceTree.Resolve(Path.Combine(dir.FullName, "DotCharter.UI", "NamCaptures"));
        return Directory.Exists(bundle) ? bundle : null;
    }

    /// <summary>A live 48 kHz engine + attached chain, or null when native can't load.</summary>
    private sealed class NativeChainEnv : IDisposable
    {
        public required JuceAudioEngine Engine { get; init; }
        public required JuceDspChainService Chain { get; init; }

        public static NativeChainEnv? TryCreate48k()
        {
            JuceAudioEngine? engine = null;
            try
            {
                engine = new JuceAudioEngine(new JuceEngineConfig { SampleRate = 48000, EnableConsoleLog = false });
                var chain = new JuceDspChainService();
                chain.LoadCatalog();
                chain.AttachEngine(engine);
                return new NativeChainEnv { Engine = engine, Chain = chain };
            }
            catch
            {
                engine?.Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            try { Chain.AttachEngine(nint.Zero); } catch { /* ignored */ }
            try { Engine.Dispose(); } catch { /* ignored */ }
        }
    }
}
