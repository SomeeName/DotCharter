using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>An in-memory lyric model service: settable states, and a record of every install and GPU retry.</summary>
internal sealed class FakeLyricModelService : ILyricModelDownloadService
{
    public Dictionary<string, LyricModelStatus> States { get; } = new(StringComparer.Ordinal)
    {
        [LyricModelCatalog.MultilingualId] = new(LyricModelCatalog.MultilingualId, false, 216, LyricModelRunner.NotRunYet, null),
        [LyricModelCatalog.EnglishId] = new(LyricModelCatalog.EnglishId, false, 180, LyricModelRunner.NotRunYet, null)
    };

    public List<(string Id, bool Repair)> Installs { get; } = [];

    public List<string> GpuRetries { get; } = [];

    /// <summary>What an install answers; it marks the model installed when true.</summary>
    public bool InstallSucceeds { get; set; } = true;

    public bool IsRequiredModelInstalled => States[LyricModelCatalog.MultilingualId].IsInstalled;

    public IReadOnlyList<LyricModelStatus> Models => [.. States.Values];

    public int PendingMegabytesFor(LyricSheet sheet) => 0;

    public Task<bool> EnsureModelsForAsync(LyricSheet sheet, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(IsRequiredModelInstalled);

    public Task<bool> InstallAsync(string id, bool repair, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Installs.Add((id, repair));
        if (InstallSucceeds) States[id] = States[id] with { IsInstalled = true };
        return Task.FromResult(InstallSucceeds);
    }

    public bool RetryGpu(string id)
    {
        GpuRetries.Add(id);
        var was = States[id].Runner == LyricModelRunner.CpuFallback;
        States[id] = States[id] with { Runner = LyricModelRunner.NotRunYet };
        return was;
    }
}
