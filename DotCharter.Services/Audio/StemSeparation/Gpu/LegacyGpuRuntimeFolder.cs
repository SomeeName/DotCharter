namespace DotCharter.Services.Audio.StemSeparation.Gpu;

/// <summary>
///     The CUDA runtime an earlier version downloaded: a <c>gpu-runtime/</c> folder of
///     NVIDIA DLLs (~1.4 GB) beside the models. Stem separation runs on DirectML now and
///     the app ships no CUDA provider, so nothing loads these DLLs any more. This type
///     only measures what is left on disk and removes it on request — the folder is the
///     user's to reclaim, and deleting a gigabyte nobody asked to delete is not ours to
///     decide.
/// </summary>
public sealed class LegacyGpuRuntimeFolder
{
    private readonly IAppLogger? _logger;

    /// <param name="modelsDirectory">The stem-models directory; the old runtime sits in a sibling <c>gpu-runtime/</c>.</param>
    /// <param name="logger">App logger.</param>
    /// <param name="runtimeDirectoryOverride">Test hook: use this exact folder.</param>
    public LegacyGpuRuntimeFolder(string modelsDirectory, IAppLogger? logger = null, string? runtimeDirectoryOverride = null)
    {
        _logger = logger;
        RuntimeDirectory = !string.IsNullOrWhiteSpace(runtimeDirectoryOverride)
            ? runtimeDirectoryOverride
            : Path.Combine(Directory.GetParent(modelsDirectory.TrimEnd('/', '\\'))?.FullName ?? modelsDirectory, "gpu-runtime");
    }

    /// <summary>Where the old runtime was installed.</summary>
    public string RuntimeDirectory { get; }

    /// <summary>Bytes the folder still occupies, every file counted (DLLs, the marker, a half-finished download); 0 when it is gone.</summary>
    public long InstalledBytes()
    {
        try
        {
            if (!Directory.Exists(RuntimeDirectory)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(RuntimeDirectory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { /* a file vanishing mid-scan is not worth a failed reading */ }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Measuring the old CUDA runtime folder failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Deletes the folder. Throws when it cannot, so the row can say so.</summary>
    public void Remove()
    {
        try
        {
            if (Directory.Exists(RuntimeDirectory))
                Directory.Delete(RuntimeDirectory, recursive: true);
            _logger?.Info(AppLogCategory.Audio, $"Old CUDA runtime removed from '{RuntimeDirectory}'.");
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"Removing the old CUDA runtime failed: {ex.Message}", ex);
            throw;
        }
    }
}
