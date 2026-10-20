using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// Bridge v2 (JuceSharp, 2026-09-10). The child used to read the device
/// configuration exactly once, in <c>openBridge</c>, from launch arguments and
/// a <c>sampleRate</c> word nothing ever re-read. So after a parent-side
/// <c>dca_engine_reconfigure</c> every sandboxed VST3 went on running at the
/// rate the child was LAUNCHED with, and a plugin prepared for 256 frames could
/// be handed 1024 — which entitles it to allocate, on the very thread the
/// parent's audio callback is blocked waiting for.
/// <para>
/// v2 answers that with a published PAIR (<c>sampleRate</c> +
/// <c>prepareBlockFrames</c>) released by a monotonic
/// <c>prepareGeneration</c>. Two things must hold on the managed side and
/// neither is visible from the layout constants alone: the mirror must agree
/// with the native header it mirrors, and every writer of the rate must move
/// the generation, or the child never re-prepares and the bug is back with a
/// version bump on top of it.
/// </para>
/// </summary>
public class VstSandboxPrepareGenerationTests
{
    // ---------------------------------------------------------------- mirror

    /// <summary>
    /// The managed mirror against the header it claims to mirror. The layout
    /// test next door pins the numbers to what a HUMAN transcribed; this one
    /// pins them to the file, which is what actually decides where the two
    /// processes read each other's memory.
    /// </summary>
    [Fact]
    public void TheManagedMirrorAgreesWithTheNativeHeader()
    {
        var header = ReadNativeHeader();

        Assert.Equal(VstSandboxLayout.BridgeVersion,
            Constant(header, "uint32_t", "kBridgeVersion"));
        Assert.Equal(VstSandboxLayout.Magic,
            Constant(header, "uint32_t", "kMagic"));
        Assert.Equal((uint)VstSandboxLayout.MaxChannels,
            Constant(header, "int", "kMaxChannels"));
        Assert.Equal((uint)VstSandboxLayout.MaxBlockFrames,
            Constant(header, "int", "kMaxBlockFrames"));
        Assert.Equal((uint)VstSandboxLayout.MaxMidiEvents,
            Constant(header, "int", "kMaxMidiEvents"));

        // The two fields v2 added, at the offsets the header documents beside
        // them. `reserved0`/`reserved1` must be GONE, not merely unused: a
        // mirror that still knew them would have two names for one word.
        Assert.Equal(VstSandboxLayout.OffPrepareBlockFrames, FieldOffset(header, "prepareBlockFrames"));
        Assert.Equal(VstSandboxLayout.OffPrepareGeneration, FieldOffset(header, "prepareGeneration"));
        // Declarations only — the header's own v2 note NAMES the two words it
        // retired, and a doc comment is exactly as inert here as a `see cref`.
        Assert.DoesNotContain("reserved0;", header, StringComparison.Ordinal);
        Assert.DoesNotContain("reserved1;", header, StringComparison.Ordinal);

        // ...and the header is still the same size, which is the entire reason
        // every other offset in the mirror could stay put.
        Assert.Contains($"sizeof(SharedHeader) == {VstSandboxLayout.HeaderBytes}", header,
            StringComparison.Ordinal);
        Assert.Matches($@"kTotalBytes\s*==\s*{VstSandboxLayout.TotalBytes}\b", header);

        // Bridge v3 (JuceSharp #28): the MIDI-timing block appended after the
        // audio regions — its offset and its pinned size.
        Assert.Matches($@"kMidiTimingOffset\s*==\s*{VstSandboxLayout.OffMidiTiming}\b", header);
        Assert.Contains($"sizeof(MidiTiming) == {VstSandboxLayout.MidiTimingBytes}", header,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- protocol

    /// <summary>
    /// Arming the bridge publishes rate AND block, and moves the generation —
    /// mirroring <c>RemoteVstBridge::enable</c>.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ArmingPublishesTheWholePairAndMovesTheGeneration()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Shared-memory bridge is Windows-only.");

        using var shm = NewBridge(sampleRate: 48_000, blockFrames: 256);

        Assert.Equal(256u, shm.PrepareBlockFrames);
        Assert.Equal(1u, shm.PrepareGeneration);
    }

    /// <summary>
    /// THE POINT. A device change must move the generation, because that is
    /// the only thing the child compares against. Writing the rate without it
    /// is exactly the v1 bug.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void EveryDeviceChangeMovesTheGenerationMonotonically()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Shared-memory bridge is Windows-only.");

        using var shm = NewBridge(sampleRate: 48_000, blockFrames: 256);
        var armed = shm.PrepareGeneration;

        shm.SetDevicePrepare(44_100, 512);
        Assert.Equal(512u, shm.PrepareBlockFrames);
        Assert.Equal(armed + 1, shm.PrepareGeneration);

        // The SAME configuration re-published still moves it. The counter is
        // fetch_add, never a set: the child re-prepares on any change, so a
        // parent that re-asserted its configuration must not be silently
        // ignored.
        shm.SetDevicePrepare(44_100, 512);
        Assert.Equal(armed + 2, shm.PrepareGeneration);

        // A non-positive value leaves its field alone — the native side skips
        // the store rather than publishing a zero the child would prepare for.
        shm.SetDevicePrepare(0, 0);
        Assert.Equal(512u, shm.PrepareBlockFrames);
        Assert.Equal(armed + 3, shm.PrepareGeneration);
    }

    // ---------------------------------------------------------------- helpers

    [SupportedOSPlatform("windows")]
    private static VstSandboxSharedMemory NewBridge(int sampleRate, int blockFrames)
    {
        var tag = $"dc-test-{Guid.NewGuid():N}";
        return new VstSandboxSharedMemory($"{tag}-shm", $"{tag}-req", $"{tag}-resp",
            sampleRate, blockFrames);
    }

    private static string ReadNativeHeader()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "JuceSharp", "src", "JuceSharp.Native",
            "include", "dotcharter_vst_bridge.h");
        Assert.True(File.Exists(path),
            $"The bridge header is not at {path} — this lint now checks nothing. Repoint it.");
        return File.ReadAllText(path);
    }

    private static uint Constant(string header, string type, string name)
    {
        var m = Regex.Match(header, $@"constexpr\s+{Regex.Escape(type)}\s+{Regex.Escape(name)}\s*=\s*(0x[0-9a-fA-F]+|\d+)");
        Assert.True(m.Success, $"'{name}' is gone from the bridge header — this assertion checks nothing.");
        var text = m.Groups[1].Value;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(text[2..], 16)
            : uint.Parse(text);
    }

    private static int FieldOffset(string header, string field)
    {
        var m = Regex.Match(header, $@"\b{Regex.Escape(field)}\s*;\s*//\s*offset\s+(\d+)");
        Assert.True(m.Success,
            $"'{field}' has no documented offset in the bridge header — the managed mirror "
            + "cannot be checked against it, so this lint would pass vacuously.");
        return int.Parse(m.Groups[1].Value);
    }
}
