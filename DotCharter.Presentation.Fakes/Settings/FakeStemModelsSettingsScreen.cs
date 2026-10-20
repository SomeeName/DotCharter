using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Stem models tab in every state the screen can be in.</summary>
public sealed partial class FakeStemModelsSettingsScreen : ObservableObject, IStemModelsSettingsScreen
{
    private string _customModelUrl = "";

    private FakeStemModelsSettingsScreen(FakeStemBackendPart backend, FakeGpuRuntimePart gpu, FakeStemCachePart cache)
    {
        Backend = backend;
        Gpu = gpu;
        Cache = cache;
        ImportCustomModelCommand = new RelayCommand(() => { });
    }

    public static IReadOnlyList<FakeVariant<IStemModelsSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error),
        new("NoGpuRuntime", NoGpuRuntime),
        new("TwoGpus", TwoGpus),
        new("NeedsGitHubSignIn", NeedsGitHubSignIn)
    ];

    /// <summary>One model installed and active, one GPU on DirectML, a cache with something in it.</summary>
    public static FakeStemModelsSettingsScreen Default() =>
        new(FakeStemBackendPart.DirectMl(), FakeGpuRuntimePart.Absent(), FakeStemCachePart.Populated())
        {
            Models = [Row("HTDemucs 4-stem", installed: true, active: true), Row("UVR-MDX-NET Inst HQ 3")]
        };

    /// <summary>A fresh install on a machine with no usable GPU: nothing downloaded, no cache.</summary>
    public static FakeStemModelsSettingsScreen Empty() =>
        new(FakeStemBackendPart.Cpu(), FakeGpuRuntimePart.Absent(), FakeStemCachePart.Empty())
        {
            Models = []
        };

    public static FakeStemModelsSettingsScreen LongText() =>
        new(FakeStemBackendPart.LongText(), FakeGpuRuntimePart.LongText(), FakeStemCachePart.LongText())
        {
            _customModelUrl = "https://huggingface.co/owner/ein-sehr-langer-repository-name/resolve/main/htdemucs-ft-4stem-fp16.onnx",
            CustomModelImportStatus = "Die Konvertierung ist fehlgeschlagen: 非常に長いエラーメッセージのテストケースです。埋め込み Python インタープリタを確認してください。",
            Models =
            [
                Row("HTDemucs 4-stem ONNX (GPU — experimental, sehr langer Katalogname)", installed: true, active: true),
                Row("UVR-MDX-NET Inst HQ 3 — 非常に長いモデル名のテストケース")
            ]
        };

    /// <summary>Every group populated, for layout: two GPUs, the old CUDA runtime, one catalogue row of each state.</summary>
    public static FakeStemModelsSettingsScreen Full() =>
        new(FakeStemBackendPart.TwoGpus(), FakeGpuRuntimePart.Leftover(), FakeStemCachePart.Populated())
        {
            _customModelUrl = "https://huggingface.co/owner/repo/resolve/main/model.onnx",
            CustomModelImportStatus = "Added 'UVR-MDX-NET Inst HQ 3' (2 stems). Download it below, then set it active.",
            Models =
            [
                Row("HTDemucs 4-stem", installed: true, active: true),
                Row("HTDemucs 6-stem", installed: true),
                Row("UVR-MDX-NET Inst HQ 3"),
                Row("My exported model", custom: true, installed: true),
                Row("Downloading right now", downloading: true),
                Row("Needs a sign-in", unavailable: "Sign in to GitHub in Settings ▸ Updates to download this model.")
            ]
        };

    /// <summary>A catalogue download and a custom import in flight at the same time.</summary>
    public static FakeStemModelsSettingsScreen Busy() =>
        new(FakeStemBackendPart.DirectMl(), FakeGpuRuntimePart.Absent(), FakeStemCachePart.Populated())
        {
            IsCustomModelImportInProgress = true,
            CustomModelImportProgress = 0.42,
            CustomModelImportStatus = "184.2 / 438.1 MB (42%)",
            Models = [Row("HTDemucs 4-stem", installed: true, active: true), Row("UVR-MDX-NET Inst HQ 3", downloading: true)]
        };

    public static FakeStemModelsSettingsScreen Error() =>
        new(FakeStemBackendPart.Unregistered(), FakeGpuRuntimePart.Failed(), FakeStemCachePart.Failed())
        {
            CustomModelImportStatus = "The import failed: the server answered 404 Not Found.",
            Models = [Row("HTDemucs 4-stem")]
        };

    /// <summary>No old CUDA runtime on disk and no usable GPU: that row is gone, not greyed.</summary>
    public static FakeStemModelsSettingsScreen NoGpuRuntime() =>
        new(FakeStemBackendPart.Cpu(), FakeGpuRuntimePart.Absent(), FakeStemCachePart.Empty())
        {
            Models = [Row("HTDemucs 4-stem", installed: true, active: true)]
        };

    /// <summary>Two GPUs and an integrated one too small for the model: the GPU picker shows, the small one greyed.</summary>
    public static FakeStemModelsSettingsScreen TwoGpus() =>
        new(FakeStemBackendPart.TwoGpus(), FakeGpuRuntimePart.Leftover(), FakeStemCachePart.Populated())
        {
            Models = [Row("HTDemucs 6-stem", installed: true, active: true)]
        };

    /// <summary>Signed out of GitHub: the 13 s SW, hosted on the app's private releases, is listed
    /// with a disabled Download and its reason; the public 4 s SW beside it stays live.</summary>
    public static FakeStemModelsSettingsScreen NeedsGitHubSignIn() =>
        new(FakeStemBackendPart.DirectMl(), FakeGpuRuntimePart.Absent(), FakeStemCachePart.Populated())
        {
            Models =
            [
                Row("HTDemucs 6-stem (ONNX)", installed: true),
                Row("BS-RoFormer SW 6-stem (ONNX)"),
                Row("BS-RoFormer SW 6-stem 13 s (ONNX)",
                    unavailable: "Sign in to GitHub in Settings ▸ Updates to download this model.")
            ]
        };

    public bool IsFolderGroupVisible => true;

    public string ModelsDirectory => @"C:\Users\me\AppData\Local\DotCharter\stem-models";

    public IStemBackendPart Backend { get; }

    public IGpuRuntimePart Gpu { get; }

    public IStemCachePart Cache { get; }

    public bool IsCustomGroupVisible => true;

    public string CustomModelUrl
    {
        get => _customModelUrl;
        set => SetProperty(ref _customModelUrl, value);
    }

    public ICommand ImportCustomModelCommand { get; }

    public string CustomModelImportStatus { get; private init; } = "";

    public bool HasCustomModelImportStatus => CustomModelImportStatus.Length > 0;

    public double CustomModelImportProgress { get; private init; }

    public bool IsCustomModelImportInProgress { get; private init; }

    public bool IsCatalogGroupVisible => true;

    public IReadOnlyList<IStemModelRow> Models { get; private init; } = [];

    private static FakeStemModelRow Row(
        string name, bool installed = false, bool active = false, bool downloading = false, bool custom = false,
        string unavailable = "") =>
        new()
        {
            DisplayName = name,
            Description = "Separates a mix into stems with a trained model.",
            SourceLabel = "4 stems",
            SizeLabel = "438.1 MB",
            DownloadedSizeLabel = installed ? "441.0 MB on disk" : "",
            IsActive = active,
            IsInstalled = installed,
            IsDownloadingNow = downloading,
            IsCustom = custom,
            DownloadUnavailableReason = unavailable,
            ProgressFraction = downloading ? 0.42 : 0,
            ProgressText = downloading ? "184.2 / 438.1 MB (42%)" : ""
        };
}
