using DotCharter.Services.Audio.StemSeparation.Gpu;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>How much model a run on the default can afford, from where it runs.</summary>
public enum StemDefaultTier
{
    /// <summary>The CPU, or a GPU under <see cref="StemDefaultModel.SwMinimumVideoMemoryBytes"/>.</summary>
    Cpu,

    /// <summary>A GPU with room for the 4 s BS-RoFormer SW but not the 13 s one.</summary>
    Gpu,

    /// <summary>A GPU with <see cref="StemDefaultModel.Sw13MinimumVideoMemoryBytes"/> or more (or an
    /// unknown adapter, which counts by the provider alone).</summary>
    LargeGpu,
}

/// <summary>
/// The stem model a user who has NOT chosen one separates with.
/// <para>
/// <b>"On the default" is an EMPTY <c>AppConfig.HtDemucsActiveModelId</c></b>: the state a
/// fresh install is in, and the one Delete returns a user to when they remove their Active
/// model. It is never written: a user on the default stays on it, so a later change of
/// default reaches them too. Any non-empty id is an explicit choice and is never touched
/// here. A user with <c>htdemucs-6s-onnx</c> or the 4 s SW Active keeps it.
/// </para>
/// <para>
/// <b>The ladder</b> (<see cref="Preference"/>), best first:
/// <list type="bullet">
/// <item><b>BS-RoFormer SW at its 13.35 s training chunk</b> (<see cref="LargeGpuDefaultId"/>) on a
/// GPU with 8 GB: 20.1 s for a 224 s song on an RTX 5070 Ti at ~5.2 GB above idle, and stems
/// that agree with PyTorch's demix at that chunk to 50–61 dB. It is a release asset of the
/// app's PRIVATE repository, so DOWNLOADING it needs the GitHub sign-in; running it does not.</item>
/// <item><b>BS-RoFormer SW at 4 s</b> (<see cref="GpuDefaultId"/>, public Hugging Face) on a GPU
/// with 6 GB, and the fallback above it: 32.5 s at ~4.1 GB.</item>
/// <item><b>htdemucs_6s</b> (<see cref="CpuDefaultId"/>) on the CPU and smaller GPUs (~2.5 GB),
/// and the fallback above. SW on the CPU took 137–203 s for 20 s of audio.</item>
/// </list>
/// </para>
/// <para>
/// <b>Nothing downloads without a yes.</b> The separation itself only ever uses an
/// INSTALLED candidate (<see cref="FirstInstalled"/>). When none is, the Separate Stems
/// verb asks once, naming the model and its size (<see cref="IStemDefaultModelProvisioner"/>),
/// and on a failed download it takes the other candidate the prompt also named
/// (<see cref="DownloadChain"/>). A default user who already separates with the 4 s SW or
/// htdemucs_6s KEEPS it until they agree to the 13 s model (<see cref="UpgradeTarget"/>); a no
/// is remembered and not asked again.
/// </para>
/// </summary>
public static class StemDefaultModel
{
    /// <summary>The default on a GPU with <see cref="Sw13MinimumVideoMemoryBytes"/>, for a user
    /// who is signed in to GitHub or already has the file.</summary>
    public const string LargeGpuDefaultId = StemModelCatalog.BsRoformerSw13sId;

    /// <summary>The default on a GPU with <see cref="SwMinimumVideoMemoryBytes"/>, and the
    /// fallback of <see cref="LargeGpuDefaultId"/>.</summary>
    public const string GpuDefaultId = "bs-roformer-sw-6s";

    /// <summary>The default on the CPU or a small GPU, and the fallback on a larger one.</summary>
    public const string CpuDefaultId = "htdemucs-6s-onnx";

    /// <summary>
    /// The least dedicated video memory the 4 s SW default needs. It peaked at ~4.1 GB above
    /// idle on a whole song (DirectML); 6 GB leaves the desktop and the game their
    /// share. A smaller card gets htdemucs_6s, which peaked at ~2.5 GB.
    /// </summary>
    public const ulong SwMinimumVideoMemoryBytes = 6UL << 30;

    /// <summary>
    /// The least dedicated video memory the 13 s SW default needs: it peaked at ~5.2 GB above
    /// idle on a whole song (DirectML graph fusion on), so an 8 GB card.
    /// </summary>
    public const ulong Sw13MinimumVideoMemoryBytes = 8UL << 30;

    /// <summary>
    /// How far short of a threshold a card may READ and still count as that size. DXGI reports
    /// a card's dedicated memory below its nominal size (measured: a 16 GB RTX 5070 Ti
    /// reads 16,771,973,120 bytes, 15.62 GiB; a 10 GB RTX 3080 10,539,237,376, 9.82 GiB), so an
    /// exact comparison would put every 8 GB card under the 8 GB rule and every 6 GB card under
    /// the 6 GB one.
    /// </summary>
    public const ulong NominalSlackBytes = 512UL << 20;

    /// <summary>The model a default user is offered in place of the one they already
    /// separate with. Only ever the 13 s SW: the 4 s SW and htdemucs_6s were never offered
    /// over each other, and that stays so.</summary>
    public const string UpgradeId = LargeGpuDefaultId;

    /// <summary>Whether <paramref name="activeModelId"/> means "no explicit choice".</summary>
    /// <param name="activeModelId">The persisted Active model id.</param>
    /// <returns><c>true</c> when it is null, empty or blank.</returns>
    public static bool IsOnDefault(string? activeModelId) => string.IsNullOrWhiteSpace(activeModelId);

    /// <summary>The tier a run on <paramref name="plan"/> gets: the CPU provider is
    /// <see cref="StemDefaultTier.Cpu"/>; a GPU by its dedicated video memory. An unknown
    /// adapter (no GPU environment, e.g. a tool) counts by the provider alone.</summary>
    /// <param name="plan">Where the separation will run.</param>
    /// <returns>The tier.</returns>
    public static StemDefaultTier TierFor(StemExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Provider == OnnxExecutionProvider.Cpu) return StemDefaultTier.Cpu;
        if (plan.Adapter is null) return StemDefaultTier.LargeGpu;
        var vram = plan.Adapter.DedicatedVideoMemoryBytes;
        if (HasNominal(vram, Sw13MinimumVideoMemoryBytes)) return StemDefaultTier.LargeGpu;
        return HasNominal(vram, SwMinimumVideoMemoryBytes) ? StemDefaultTier.Gpu : StemDefaultTier.Cpu;
    }

    /// <summary>Whether a card that reports <paramref name="reported"/> bytes is nominally at
    /// least <paramref name="threshold"/> (<see cref="NominalSlackBytes"/>).</summary>
    internal static bool HasNominal(ulong reported, ulong threshold) => reported + NominalSlackBytes >= threshold;

    /// <summary>The models a run on <paramref name="tier"/> may separate with, best first.
    /// The 13 s SW is never in a smaller tier: it would not fit.</summary>
    /// <param name="tier">The run's tier.</param>
    /// <returns>Catalogue ids.</returns>
    public static IReadOnlyList<string> Preference(StemDefaultTier tier) => tier switch
    {
        StemDefaultTier.LargeGpu => [LargeGpuDefaultId, GpuDefaultId, CpuDefaultId],
        StemDefaultTier.Gpu => [GpuDefaultId, CpuDefaultId],
        _ => [CpuDefaultId, GpuDefaultId],
    };

    /// <summary>The first installed model of <see cref="Preference"/>, or <c>null</c>. What a
    /// separation on the default runs with; it never downloads, and it does not ask about
    /// the sign-in: an installed file runs signed in or not.</summary>
    /// <param name="tier">The run's tier.</param>
    /// <param name="isInstalled">Whether a model id's file is on disk.</param>
    /// <returns>A catalogue id, or <c>null</c> when none is installed.</returns>
    public static string? FirstInstalled(StemDefaultTier tier, Func<string, bool> isInstalled)
    {
        ArgumentNullException.ThrowIfNull(isInstalled);
        return Preference(tier).FirstOrDefault(isInstalled);
    }

    /// <summary>
    /// What the first-download prompt offers when nothing is installed: the first two models
    /// of <see cref="Preference"/> that can be downloaded NOW. Without a GitHub sign-in the 13 s
    /// SW is skipped, so a large GPU is offered the 4 s SW with htdemucs_6s behind it.
    /// </summary>
    /// <param name="tier">The run's tier.</param>
    /// <param name="canDownload">Whether a model id can be downloaded now.</param>
    /// <returns>The model to download, then the one taken if it fails.</returns>
    public static IReadOnlyList<string> DownloadChain(StemDefaultTier tier, Func<string, bool> canDownload)
    {
        ArgumentNullException.ThrowIfNull(canDownload);
        return Preference(tier).Where(canDownload).Take(2).ToArray();
    }

    /// <summary>
    /// The model to offer a default user who already separates with another default, or
    /// <c>null</c>: <see cref="UpgradeId"/> on a <see cref="StemDefaultTier.LargeGpu"/> when it is
    /// not installed, can be downloaded now, was not declined before, and something else of
    /// the ladder IS installed (with nothing installed, the first-download prompt asks instead).
    /// </summary>
    /// <param name="tier">The run's tier.</param>
    /// <param name="isInstalled">Whether a model id's file is on disk.</param>
    /// <param name="canDownload">Whether a model id can be downloaded now.</param>
    /// <param name="declinedId">The upgrade the user said no to, or empty.</param>
    /// <returns><see cref="UpgradeId"/> or <c>null</c>.</returns>
    public static string? UpgradeTarget(
        StemDefaultTier tier, Func<string, bool> isInstalled, Func<string, bool> canDownload, string? declinedId)
    {
        ArgumentNullException.ThrowIfNull(isInstalled);
        ArgumentNullException.ThrowIfNull(canDownload);
        if (tier != StemDefaultTier.LargeGpu) return null;
        if (isInstalled(UpgradeId) || !canDownload(UpgradeId)) return null;
        if (string.Equals(declinedId?.Trim(), UpgradeId, StringComparison.OrdinalIgnoreCase)) return null;
        return FirstInstalled(tier, isInstalled) == null ? null : UpgradeId;
    }

    /// <summary>
    /// Downloads <paramref name="chain"/> in order until one is installed, AFTER the user
    /// agreed. A download that FAILS moves on to the next (the prompt named it); a cancel
    /// propagates and starts nothing else.
    /// </summary>
    /// <param name="chain">The ids the user agreed to, first to last.</param>
    /// <param name="isInstalled">Whether a model id's file is on disk.</param>
    /// <param name="download">Fetches a model id.</param>
    /// <param name="onDownloadFailed">Told about each failed download before the next candidate.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The installed id, or <c>null</c> when none could be fetched.</returns>
    public static async Task<string?> DownloadFirstAsync(
        IReadOnlyList<string> chain,
        Func<string, bool> isInstalled,
        Func<string, CancellationToken, Task> download,
        Action<string, Exception>? onDownloadFailed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(isInstalled);
        ArgumentNullException.ThrowIfNull(download);
        foreach (var id in chain)
        {
            if (isInstalled(id)) return id;
            ct.ThrowIfCancellationRequested();
            try
            {
                await download(id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                onDownloadFailed?.Invoke(id, ex);
                continue;
            }

            if (isInstalled(id)) return id;
        }

        return null;
    }
}
