using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// The managed request writer follows the sequence lock of the native bridge
/// header: the writing marker goes into the sequence field before the first
/// payload byte, the new sequence after the last, and a sequence is never 0 or
/// the marker. A child still copying the previous request relies on exactly
/// that to drop a copy the parent overwrote. Pure over a memory block, no child.
/// </summary>
public class VstSandboxRequestWriterTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheSequenceFieldReadsTheWritingMarkerWhileThePayloadIsWritten()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Shared-memory bridge is Windows-only.");

        using var shm = NewBridge(null);
        shm.PostRequest(() => { });
        var before = shm.RequestSequence;
        uint during = 0;
        var wrote = false;

        shm.PostRequest(() =>
        {
            during = shm.RequestSequence;
            wrote = true;
        });

        Assert.True(wrote, "The payload writer never ran.");
        Assert.NotEqual(VstSandboxLayout.RequestWriting, before);
        Assert.Equal(VstSandboxLayout.RequestWriting, during);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheNewSequenceIsPublishedAfterThePayload()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Shared-memory bridge is Windows-only.");

        using var shm = NewBridge(41u);

        var seq = shm.PostRequest(() => { });

        Assert.Equal(42u, seq);
        Assert.Equal(42u, shm.RequestSequence);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheSequenceAfterTheLastValueBelowTheMarkerIsOneNeverZeroOrTheMarker()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Shared-memory bridge is Windows-only.");

        using var shm = NewBridge(0xFFFFFFFEu);

        var seq = shm.PostRequest(() => { });

        Assert.Equal(1u, seq);
        Assert.Equal(1u, shm.RequestSequence);
        Assert.Equal(1u, VstSandboxLayout.NextRequestSeq(0xFFFFFFFEu));
        Assert.Equal(1u, VstSandboxLayout.NextRequestSeq(0xFFFFFFFFu));
        Assert.Equal(1u, VstSandboxLayout.NextRequestSeq(0u));
        Assert.Equal(2u, VstSandboxLayout.NextRequestSeq(1u));
    }

    /// <summary>
    /// A bridge over a mapping whose request sequence already holds
    /// <paramref name="seedSeq"/> (the writer continues from what the mapping says).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static VstSandboxSharedMemory NewBridge(uint? seedSeq)
    {
        var tag = $"dc-test-{Guid.NewGuid():N}";
        var name = $"{tag}-shm";
        using var seeded = MemoryMappedFile.CreateOrOpen(name, VstSandboxLayout.TotalBytes);
        if (seedSeq is { } seed)
        {
            using var view = seeded.CreateViewAccessor(0, VstSandboxLayout.TotalBytes);
            view.Write(VstSandboxLayout.OffRequestSeq, seed);
        }
        // Opens the same mapping, so it outlives the seeding handle.
        return new VstSandboxSharedMemory(name, $"{tag}-req", $"{tag}-resp", 48_000, 256);
    }
}
