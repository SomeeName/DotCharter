using DotCharter.Analysis.Lyrics;
using DotCharter.Localization;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>Downloads the acoustic models lyric auto-sync needs, on demand.</summary>
public interface ILyricModelDownloadService
{
    /// <summary>True when the multilingual model — the one auto-sync cannot run without — is present.</summary>
    bool IsRequiredModelInstalled { get; }

    /// <summary>Approximate download size, MB, for what <see cref="EnsureModelsForAsync" /> would fetch.</summary>
    int PendingMegabytesFor(LyricSheet sheet);

    /// <summary>
    ///     Ensures the models this SHEET needs are present, fetching what is
    ///     missing. Returns true when auto-sync can run afterwards.
    /// </summary>
    Task<bool> EnsureModelsForAsync(
        LyricSheet sheet, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Every model auto-sync can use, as Settings ▸ AI &amp; models lists them.</summary>
    IReadOnlyList<LyricModelStatus> Models { get; }

    /// <summary>
    ///     Downloads one model, or with <paramref name="repair" /> deletes the
    ///     installed copy first and downloads it again. True when it is installed afterwards.
    /// </summary>
    Task<bool> InstallAsync(string id, bool repair, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Forgets that <paramref name="id" /> fell back to the CPU, so its next run asks for the GPU again.</summary>
    bool RetryGpu(string id);
}

/// <summary>Where a lyric model ran last.</summary>
public enum LyricModelRunner
{
    /// <summary>No auto-sync has used it in this session.</summary>
    NotRunYet,

    /// <summary>On a GPU provider (<see cref="LyricModelStatus.ProviderName" /> says which).</summary>
    Gpu,

    /// <summary>On the CPU, because no GPU provider was available.</summary>
    Cpu,

    /// <summary>On the CPU after failing on the GPU; it stays there until a retry.</summary>
    CpuFallback
}

/// <summary>One lyric model for Settings: installed or not, its download size and what it ran on.</summary>
public sealed record LyricModelStatus(string Id, bool IsInstalled, int Megabytes, LyricModelRunner Runner, string? ProviderName);

/// <summary>
///     Ties <see cref="LyricModelAssetFetcher" /> to <see cref="LyricModelInstaller" />.
///     <para>
///         <b>What gets fetched depends on the sheet, and the two models are not
///         equal.</b> The multilingual model is REQUIRED — without it auto-sync
///         cannot run at all. The English head is an OPTIONAL improvement, so it is
///         only fetched when the router says this sheet is English, and a failure
///         to get it is not a failure of the operation: alignment proceeds on the
///         multilingual model, a little less accurately. That asymmetry is the
///         whole reason this is a service and not one <c>EnsureInstalled</c> call.
///     </para>
/// </summary>
public sealed class LyricModelDownloadService(
    LyricModelAssetFetcher fetcher,
    ILocalizationService localization,
    IAppLogger? logger = null,
    IReadOnlyList<string>? modelRoots = null,
    LyricProviderMemory? providerMemory = null) : ILyricModelDownloadService, IDisposable
{
    private static readonly LyricModelDownload[] Downloads =
        [LyricModelCatalog.MultilingualDownload, LyricModelCatalog.EnglishDownload];

    private readonly LyricModelInstaller _installer = new(fetcher.FetchAsync, localization, logger, modelRoots);
    private readonly LyricProviderMemory _memory = providerMemory ?? LyricProviderMemory.Process;

    public IReadOnlyList<LyricModelStatus> Models =>
        [.. Downloads.Select(d => new LyricModelStatus(
            d.Id, _installer.IsInstalled(d.Id), (int)(d.ApproximateBytes / (1024 * 1024)), RunnerOf(d.Id),
            _memory.LastProvider(d.Id) is { } p && p != OnnxExecutionProvider.Cpu ? ProviderName(p) : null))];

    public async Task<bool> InstallAsync(string id, bool repair, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var download = Downloads.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal))
                       ?? throw new ArgumentException($"No lyric model '{id}'.", nameof(id));
        if (repair) await Task.Run(() => _installer.Remove(id), ct).ConfigureAwait(false);
        return await _installer.EnsureInstalledAsync(download, progress, ct).ConfigureAwait(false);
    }

    public bool RetryGpu(string id) => _memory.ForgetCpu(id);

    private LyricModelRunner RunnerOf(string id)
    {
        if (_memory.IsCpuOnly(id)) return LyricModelRunner.CpuFallback;
        return _memory.LastProvider(id) switch
        {
            null => LyricModelRunner.NotRunYet,
            OnnxExecutionProvider.Cpu => LyricModelRunner.Cpu,
            _ => LyricModelRunner.Gpu
        };
    }

    private static string ProviderName(OnnxExecutionProvider provider) => provider switch
    {
        OnnxExecutionProvider.DirectML => "DirectML",
        OnnxExecutionProvider.Cuda => "CUDA",
        _ => provider.ToString()
    };

    public bool IsRequiredModelInstalled =>
        _installer.IsInstalled(LyricModelCatalog.MultilingualId);

    public int PendingMegabytesFor(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var bytes = 0L;
        if (!IsRequiredModelInstalled) bytes += LyricModelCatalog.MultilingualDownload.ApproximateBytes;
        if (WantsEnglish(sheet) && !_installer.IsInstalled(LyricModelCatalog.EnglishId))
            bytes += LyricModelCatalog.EnglishDownload.ApproximateBytes;

        return (int)(bytes / (1024 * 1024));
    }

    public async Task<bool> EnsureModelsForAsync(
        LyricSheet sheet, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (!IsRequiredModelInstalled &&
            !await _installer.EnsureInstalledAsync(
                LyricModelCatalog.MultilingualDownload, progress, ct).ConfigureAwait(false))
        {
            return false;
        }

        // Optional: a failure here costs accuracy, not the operation.
        if (WantsEnglish(sheet) && !_installer.IsInstalled(LyricModelCatalog.EnglishId))
        {
            await _installer.EnsureInstalledAsync(
                LyricModelCatalog.EnglishDownload, progress, ct).ConfigureAwait(false);
        }

        return IsRequiredModelInstalled;
    }

    private static bool WantsEnglish(LyricSheet sheet) => LyricScriptDetector.IsLikelyEnglish(sheet);

    public void Dispose() => fetcher.Dispose();
}
