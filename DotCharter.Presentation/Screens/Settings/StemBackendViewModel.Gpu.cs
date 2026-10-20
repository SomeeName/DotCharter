// The GPU half of the backend block: the hardware probe, the provider list it decides, and the GPU picker.
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Settings;

public sealed partial class StemBackendViewModel
{
    private readonly IStemGpuEnvironment? _gpu;
    private StemExecutionReason? _directMlBlocker;
    private string _storedGpuKey = "";
    private IReadOnlyList<RankedStemGpu> _ranked = [];
    private bool _isGpuRowSearchHidden;

    /// <summary>"Automatic", then every hardware GPU in automatic-choice order; greyed entries carry their reason.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGpu))]
    [NotifyPropertyChangedFor(nameof(IsGpuRowVisible))]
    private IReadOnlyList<StemGpuOption> _gpuOptions = [];

    IReadOnlyList<IStemGpuOption> IStemBackendPart.GpuOptions => GpuOptions;

    /// <summary>Two or more GPUs, DirectML in effect, and not filtered out by the search.</summary>
    public bool IsGpuRowVisible => !_isGpuRowSearchHidden
                                   && _ranked.Count >= 2
                                   && _directMlBlocker == null
                                   && PreferredExecutionProvider == StemExecutionProviderOptions.DirectMl;

    /// <summary>
    ///     "Automatic" while nothing is stored; else the stored pick while it is listed and
    ///     usable, else the card the automatic choice falls back to. A greyed pick is refused.
    /// </summary>
    public IStemGpuOption? SelectedGpu
    {
        get => ResolveGpu();
        set
        {
            if (value is StemGpuOption { IsAvailable: true } option && option.Key != ResolveGpu()?.Key)
            {
                _storedGpuKey = option.Key;
                if (!_loading) _manager.GpuAdapterKey = option.Key;
                _logger?.Info(AppLogCategory.Audio, $"Stem separation GPU picked: '{option.Label}'.");
            }

            OnPropertyChanged();
        }
    }

    private StemGpuOption? ResolveGpu()
    {
        if (string.IsNullOrWhiteSpace(_storedGpuKey))
            return GpuOptions.FirstOrDefault(o => o.Key == StemExecutionProviderOptions.AutomaticGpuKey && o.IsAvailable);
        var chosen = StemGpuRanking.Resolve(_ranked, _storedGpuKey);
        return chosen == null ? null : GpuOptions.FirstOrDefault(o => o.Key == chosen.Key);
    }

    private void LoadGpuChoice(string? stored)
    {
        _storedGpuKey = stored ?? "";
        OnPropertyChanged(nameof(SelectedGpu));
    }

    private void SetGpuRowSearchVisibility(bool visible)
    {
        if (_isGpuRowSearchHidden == !visible) return;
        _isGpuRowSearchHidden = !visible;
        OnPropertyChanged(nameof(IsGpuRowVisible));
    }

    /// <summary>
    ///     Reads whether the runtime has DirectML and which GPUs exist (off the UI thread:
    ///     the first read initialises ONNX Runtime), then rebuilds the provider list, the GPU
    ///     list and the provider in effect. Re-normalising persists nothing: a machine whose
    ///     GPU is missing today keeps the stored preference for the day it is back.
    /// </summary>
    public async Task RefreshHardwareAsync()
    {
        if (_gpu == null) return;
        bool directMl;
        IReadOnlyList<StemGpuAdapter> adapters;
        try
        {
            (directMl, adapters) = await Task.Run(() => (_gpu.IsDirectMlAvailable, _gpu.Adapters())).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Stem GPU probe failed: {ex.Message}");
            (directMl, adapters) = (false, []);
        }

        var ranked = StemGpuRanking.Rank(adapters);
        _directMlBlocker = !directMl ? StemExecutionReason.NoDirectMlInRuntime
            : ranked.Any(r => r.IsUsable) ? null
            : StemExecutionReason.NoUsableGpu;

        _loading = true;
        try
        {
            _ranked = ranked;
            Options = StemExecutionProviderOptions.For(_localization, _directMlBlocker);
            GpuOptions = StemExecutionProviderOptions.GpuOptions(_localization, ranked);
            PreferredExecutionProvider = NormalizeProvider(_storedProvider);
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(IsGpuRowVisible));
    }
}
