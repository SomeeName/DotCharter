using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Stem models' row for the CUDA runtime an earlier version downloaded.
///     Nothing loads it any more, so the row offers only Remove, and only while the
///     folder holds bytes — it must never look like a live GPU option.
/// </summary>
public sealed class LegacyCudaRuntimeRowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-legacy-row-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    private GpuRuntimeViewModel Row(LegacyGpuRuntimeFolder? folder) =>
        new(FakeLocalization.KeysOnly(), new InlineDispatcher(), folder);

    [Fact]
    public async Task NothingOnDisk_TheRowIsGone()
    {
        var row = Row(new LegacyGpuRuntimeFolder(Path.Combine(_root, "models")));
        await row.RefreshStatusAsync();
        Assert.False(row.IsGroupVisible);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public async Task NoFolderService_TheRowIsGone()
    {
        var row = Row(null);
        await row.RefreshStatusAsync();
        Assert.False(row.IsGroupVisible);
    }

    [Fact]
    public async Task ALeftoverRuntime_OffersRemove_AndRemoveReclaimsIt()
    {
        var folder = new LegacyGpuRuntimeFolder(Path.Combine(_root, "models"));
        Directory.CreateDirectory(folder.RuntimeDirectory);
        File.WriteAllBytes(Path.Combine(folder.RuntimeDirectory, "cublasLt64_13.dll"), new byte[3 << 20]);
        var row = Row(folder);

        await row.RefreshStatusAsync();
        Assert.True(row.IsGroupVisible);
        Assert.True(row.CanRemove);
        Assert.Equal("Settings_StemModels_GpuAccelLegacyStatusFmt(3 MB)", row.Status);

        await row.RemoveCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(folder.RuntimeDirectory));
        Assert.False(row.IsGroupVisible);
        Assert.False(row.CanRemove);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public void PostNormal(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}
