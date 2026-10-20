using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.Gpu;

/// <summary>
///     The CUDA runtime an earlier version downloaded: measured (every file, not just
///     the DLLs, so a half-finished download still offers Remove) and removed on request.
/// </summary>
public sealed class LegacyGpuRuntimeFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-legacy-cuda-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void TheFolderSitsBesideTheModels_WhereEarlierVersionsPutIt()
    {
        var folder = new LegacyGpuRuntimeFolder(Path.Combine(_root, "models"));
        Assert.Equal(Path.Combine(_root, "gpu-runtime"), folder.RuntimeDirectory);
    }

    [Fact]
    public void NoFolder_MeasuresZero()
    {
        var folder = new LegacyGpuRuntimeFolder(Path.Combine(_root, "models"));
        Assert.Equal(0, folder.InstalledBytes());
    }

    [Fact]
    public void EveryFileCounts_AndRemoveDeletesThemAll()
    {
        var folder = new LegacyGpuRuntimeFolder(Path.Combine(_root, "models"));
        Directory.CreateDirectory(Path.Combine(folder.RuntimeDirectory, "nested"));
        File.WriteAllBytes(Path.Combine(folder.RuntimeDirectory, "cudnn64_9.dll"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(folder.RuntimeDirectory, "gpu-runtime.json"), new byte[20]);
        File.WriteAllBytes(Path.Combine(folder.RuntimeDirectory, "nested", "wheel.partial"), new byte[300]);

        Assert.Equal(1320, folder.InstalledBytes());

        folder.Remove();
        Assert.False(Directory.Exists(folder.RuntimeDirectory));
        Assert.Equal(0, folder.InstalledBytes());
    }
}
