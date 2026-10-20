using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Services.Audio.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>What a user on the default would download before separating, named so the prompt
/// can say it.</summary>
/// <param name="ModelId">The model downloaded first.</param>
/// <param name="DisplayName">Its user-facing name.</param>
/// <param name="Bytes">Its exact size.</param>
/// <param name="FallbackId">A first download: the model taken if <paramref name="ModelId"/>
/// cannot be fetched. An upgrade: the installed model the user keeps if they say no or the
/// download fails.</param>
/// <param name="FallbackDisplayName">Its user-facing name.</param>
/// <param name="FallbackBytes">Its exact size.</param>
/// <param name="Chain">The ids the download walks, first to last
/// (<see cref="StemDefaultModel.DownloadFirstAsync"/>).</param>
/// <param name="IsUpgrade">An upgrade over an installed default (<see cref="StemDefaultModel.UpgradeTarget"/>)
/// rather than a first download.</param>
public sealed record StemDefaultDownloadOffer(
    string ModelId,
    string DisplayName,
    long Bytes,
    string FallbackId,
    string FallbackDisplayName,
    long FallbackBytes,
    IReadOnlyList<string> Chain,
    bool IsUpgrade = false);

/// <summary>The one-click "download the default stem model?" step in front of a
/// separation. Nothing here downloads unless a caller that asked the user calls
/// <see cref="DownloadAsync"/>.</summary>
public interface IStemDefaultModelProvisioner
{
    /// <summary>The download a separation should ask about first, or <c>null</c> when none: an
    /// explicit model is Active, or the best default this machine can get is installed, or the
    /// user already declined the upgrade.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The offer, or <c>null</c>.</returns>
    Task<StemDefaultDownloadOffer?> GetOfferAsync(CancellationToken ct = default);

    /// <summary>Downloads the offer's chain until one model arrives. Does not make it Active:
    /// the user stays on the default.</summary>
    /// <param name="offer">What the user agreed to.</param>
    /// <param name="progress">0..1.</param>
    /// <param name="ct">Cancellation; keeps the part file for a resume.</param>
    /// <returns>The installed id, or <c>null</c> when none could be fetched.</returns>
    Task<string?> DownloadAsync(StemDefaultDownloadOffer offer, IProgress<double>? progress, CancellationToken ct = default);

    /// <summary>Remembers a NO to an upgrade offer, so it is not asked again. The installed
    /// default keeps separating; Settings can still download the model.</summary>
    /// <param name="offer">The declined upgrade.</param>
    void DeclineUpgrade(StemDefaultDownloadOffer offer);
}

/// <summary>
/// <see cref="IStemDefaultModelProvisioner"/> over the model manager and the same
/// execution plan the ONNX backend separates with, so the offer names the model the run
/// will use.
/// </summary>
public sealed class StemDefaultModelProvisioner : IStemDefaultModelProvisioner
{
    private readonly StemModelManager _manager;
    private readonly IStemGpuEnvironment? _gpu;
    private readonly IAppLogger? _logger;

    /// <param name="manager">The models directory, the Active choice and what can be downloaded.</param>
    /// <param name="gpu">The GPU environment; <c>null</c> decides by the provider preference alone.</param>
    /// <param name="logger">Optional log.</param>
    public StemDefaultModelProvisioner(StemModelManager manager, IStemGpuEnvironment? gpu = null, IAppLogger? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _gpu = gpu;
        _logger = logger;
    }

    /// <summary>The tier this machine runs the default on right now. Enumerates adapters,
    /// so it runs off the UI thread.</summary>
    internal Task<StemDefaultTier> TierAsync(CancellationToken ct) =>
        Task.Run(() => StemDefaultModel.TierFor(OnnxStemSeparationService.ResolvePlan(_manager, _gpu)), ct);

    /// <summary>The installed default for a user on the default, or <c>null</c>.</summary>
    internal async Task<string?> InstalledDefaultAsync(CancellationToken ct)
    {
        var tier = await TierAsync(ct).ConfigureAwait(false);
        return StemDefaultModel.FirstInstalled(tier, _manager.IsInstalled);
    }

    /// <inheritdoc />
    public async Task<StemDefaultDownloadOffer?> GetOfferAsync(CancellationToken ct = default)
    {
        if (!StemDefaultModel.IsOnDefault(_manager.ActiveModelId)) return null;
        var tier = await TierAsync(ct).ConfigureAwait(false);
        var installed = StemDefaultModel.FirstInstalled(tier, _manager.IsInstalled);
        if (installed == null) return FirstDownloadOffer(tier);

        var upgrade = StemDefaultModel.UpgradeTarget(
            tier, _manager.IsInstalled, _manager.CanDownload, _manager.DeclinedDefaultUpgradeId);
        if (upgrade == null) return null;
        var model = StemModelCatalog.FindById(upgrade);
        var kept = StemModelCatalog.FindById(installed);
        if (model == null || kept == null) return null;
        return new StemDefaultDownloadOffer(model.Id, model.DisplayName, model.ExactBytes,
            kept.Id, kept.DisplayName, kept.ExactBytes, [model.Id], IsUpgrade: true);
    }

    private StemDefaultDownloadOffer? FirstDownloadOffer(StemDefaultTier tier)
    {
        var chain = StemDefaultModel.DownloadChain(tier, _manager.CanDownload);
        if (chain.Count == 0) return null;
        var first = StemModelCatalog.FindById(chain[0]);
        var fallback = StemModelCatalog.FindById(chain[^1]);
        if (first == null || fallback == null) return null;
        return new StemDefaultDownloadOffer(first.Id, first.DisplayName, first.ExactBytes,
            fallback.Id, fallback.DisplayName, fallback.ExactBytes, chain);
    }

    /// <inheritdoc />
    public Task<string?> DownloadAsync(StemDefaultDownloadOffer offer, IProgress<double>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return StemDefaultModel.DownloadFirstAsync(offer.Chain, _manager.IsInstalled,
            (id, token) =>
            {
                _logger?.Info(AppLogCategory.Audio, $"Stem separation: downloading the default model '{id}' (the user agreed).");
                return _manager.DownloadAsync(id,
                    new Progress<ModelDownloadProgress>(p => progress?.Report(p.Fraction)),
                    token, activateIfNoneActive: false);
            },
            (id, ex) => _logger?.Warning(AppLogCategory.Audio,
                $"Stem separation: the default model '{id}' could not be downloaded ({ex.Message})" +
                (offer.IsUpgrade ? $"; keeping '{offer.FallbackId}'." : "; trying the fallback.")),
            ct);
    }

    /// <inheritdoc />
    public void DeclineUpgrade(StemDefaultDownloadOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (!offer.IsUpgrade) return;
        _manager.DeclinedDefaultUpgradeId = offer.ModelId;
    }
}
