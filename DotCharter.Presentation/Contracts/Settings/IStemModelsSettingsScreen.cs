using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Stem models: where the separation models live, a custom-model
///     import, and the catalogue itself — plus three blocks with their own
///     lifetimes, each behind its own part contract.
///     <para>
///         The machine-state probes behind those parts (an ONNX Runtime
///         environment init, the GPU adapter list, a scan of the old CUDA
///         runtime folder, and a full walk of the stem cache) are taken on
///         <c>IScreenLifecycle.OnShown</c>, not at construction: this screen
///         is built with the settings shell, and
///         none of that is work the user asked for before opening the tab.
///     </para>
/// </summary>
public interface IStemModelsSettingsScreen : INotifyPropertyChanged
{
    bool IsFolderGroupVisible { get; }

    /// <summary>Where downloaded models are kept — also where a hand-placed one has to go.</summary>
    string ModelsDirectory { get; }

    IStemBackendPart Backend { get; }

    IGpuRuntimePart Gpu { get; }

    IStemCachePart Cache { get; }

    bool IsCustomGroupVisible { get; }

    /// <summary>A direct model-file URL. A PyTorch checkpoint extension routes through the download + conversion pipeline instead.</summary>
    string CustomModelUrl { get; set; }

    ICommand ImportCustomModelCommand { get; }

    string CustomModelImportStatus { get; }

    bool HasCustomModelImportStatus { get; }

    double CustomModelImportProgress { get; }

    bool IsCustomModelImportInProgress { get; }

    bool IsCatalogGroupVisible { get; }

    IReadOnlyList<IStemModelRow> Models { get; }
}

/// <summary>
///     The backend block: which execution provider and GPU run the models, and
///     what the resolved pipeline reports about itself.
/// </summary>
public interface IStemBackendPart : INotifyPropertyChanged
{
    bool IsGroupVisible { get; }

    /// <summary>What this build's ONNX Runtime can and cannot do, as the group's subtitle.</summary>
    string Hint { get; }

    bool IsExecutionProviderRowVisible { get; }

    /// <summary>
    ///     Every provider the picker lists, including one this machine cannot
    ///     run: DirectML stays in the list greyed, with its reason (no DirectX 12
    ///     GPU with enough video memory), so the user learns why the GPU is not
    ///     used instead of finding no entry.
    /// </summary>
    IReadOnlyList<IStemExecutionProviderOption> ExecutionProviderOptions { get; }

    /// <summary>
    ///     The provider in effect, and never an unavailable one: a stored choice
    ///     this build cannot run reads as the automatic choice, and picking a
    ///     greyed entry is refused.
    /// </summary>
    IStemExecutionProviderOption SelectedExecutionProvider { get; set; }

    /// <summary>
    ///     The GPU row: shown only on a machine with two or more hardware GPUs, and
    ///     only while DirectML is the provider in effect — on the CPU there is no GPU
    ///     to choose.
    /// </summary>
    bool IsGpuRowVisible { get; }

    /// <summary>
    ///     Every hardware GPU, fastest first. One that cannot run the model (too
    ///     little video memory) stays listed, greyed, with its reason.
    /// </summary>
    IReadOnlyList<IStemGpuOption> GpuOptions { get; }

    /// <summary>
    ///     The GPU separation runs on: the stored pick while it is present and
    ///     usable, else the fastest. Picking a greyed entry is refused.
    /// </summary>
    IStemGpuOption? SelectedGpu { get; set; }

    bool IsStatusRowVisible { get; }

    string Status { get; }

    ICommand RefreshCommand { get; }
}

/// <summary>One model in the catalogue: what it is, whether it is on disk, and the four verbs.</summary>
public interface IStemModelRow : INotifyPropertyChanged
{
    string DisplayName { get; }

    string Description { get; }

    /// <summary>How many stems it separates into.</summary>
    string SourceLabel { get; }

    /// <summary>The download size, or an em dash when the catalogue does not say.</summary>
    string SizeLabel { get; }

    /// <summary>What it occupies on disk, empty when it is not installed.</summary>
    string DownloadedSizeLabel { get; }

    /// <summary>The one model separation uses. The CheckBox writes it; <see cref="CanSetActive" /> is the guard.</summary>
    bool IsActive { get; set; }

    bool CanSetActive { get; }

    bool CanDownload { get; }

    ICommand DownloadCommand { get; }

    /// <summary>
    ///     Not installed and cannot be downloaded now — a model on the app's private releases
    ///     with nobody signed in to GitHub. Shown as a DISABLED Download with
    ///     <see cref="DownloadUnavailableReason" /> printed under the row, never as a live button.
    /// </summary>
    bool IsDownloadUnavailable { get; }

    /// <summary>Why the download is unavailable; empty when it is not.</summary>
    string DownloadUnavailableReason { get; }

    bool CanCancel { get; }

    ICommand CancelCommand { get; }

    bool CanDelete { get; }

    ICommand DeleteCommand { get; }

    /// <summary>True for a model the user imported — only those can leave the catalogue.</summary>
    bool IsCustom { get; }

    ICommand RemoveCustomCommand { get; }

    bool IsDownloading { get; }

    double ProgressFraction { get; }

    string ProgressText { get; }
}

/// <summary>
///     The CUDA runtime an earlier version downloaded (~1.4 GB). Separation runs
///     on DirectML and nothing loads it, so the row offers one verb — Remove —
///     and shows only while the folder still holds bytes.
/// </summary>
public interface IGpuRuntimePart : INotifyPropertyChanged
{
    bool IsGroupVisible { get; }

    bool IsStatusRowVisible { get; }

    /// <summary>What is left on disk, e.g. "1.4 GB on disk, unused".</summary>
    string Status { get; }

    /// <summary>Follows the BYTES on disk: a half-finished or stale install is just as much dead weight.</summary>
    bool CanRemove { get; }

    ICommand RemoveCommand { get; }

    ICommand RefreshCommand { get; }

    /// <summary>Why a removal failed; empty otherwise.</summary>
    string Detail { get; }

    bool HasDetail { get; }
}

/// <summary>
///     The durable stem cache. There is deliberately no size cap, age eviction
///     or LRU behind it: stems are never regenerated, so every archive is
///     minutes of separation the user already paid for on a song they may not
///     have open. This row exists so that reclaiming it is an informed choice
///     rather than an unexplained tens-of-gigabytes folder.
/// </summary>
public interface IStemCachePart : INotifyPropertyChanged
{
    bool IsGroupVisible { get; }

    bool IsUsageRowVisible { get; }

    string Usage { get; }

    bool HasEntries { get; }

    ICommand ClearCommand { get; }

    ICommand RefreshCommand { get; }

    string Detail { get; }

    bool HasDetail { get; }
}

/// <summary>One entry of the execution-provider picker.</summary>
public interface IStemExecutionProviderOption
{
    /// <summary>The provider as people write it: "DirectML", "CPU".</summary>
    string Label { get; }

    /// <summary>False when this machine cannot run the provider; the entry is greyed and cannot be picked.</summary>
    bool IsAvailable { get; }

    /// <summary>Why the machine cannot run it, printed under the greyed entry; empty when available.</summary>
    string UnavailableReason { get; }
}

/// <summary>One entry of the GPU picker.</summary>
public interface IStemGpuOption
{
    /// <summary>The adapter's name and video memory, e.g. "NVIDIA GeForce RTX 3080 · 10 GB".</summary>
    string Label { get; }

    /// <summary>False when the adapter cannot run the model; the entry is greyed and cannot be picked.</summary>
    bool IsAvailable { get; }

    /// <summary>Why it cannot, printed under the greyed entry; empty when available.</summary>
    string UnavailableReason { get; }
}
