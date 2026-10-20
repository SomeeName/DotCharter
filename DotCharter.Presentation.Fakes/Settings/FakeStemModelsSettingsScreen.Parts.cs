using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>The catalogue row and the three blocks, each reachable in every state the screen can show.</summary>
public sealed partial class FakeStemModelsSettingsScreen
{
    private sealed class FakeStemModelRow : ObservableObject, IStemModelRow
    {
        private bool _isActive;

        public FakeStemModelRow()
        {
            DownloadCommand = new RelayCommand(() => { });
            CancelCommand = new RelayCommand(() => { });
            DeleteCommand = new RelayCommand(() => { });
            RemoveCustomCommand = new RelayCommand(() => { });
        }

        public string DisplayName { get; init; } = "";

        public string Description { get; init; } = "";

        public string SourceLabel { get; init; } = "";

        public string SizeLabel { get; init; } = "";

        public string DownloadedSizeLabel { get; init; } = "";

        public bool IsInstalled { get; init; }

        public bool IsDownloadingNow { get; init; }

        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        public bool CanSetActive => IsInstalled;

        public bool CanDownload => !IsInstalled && !IsDownloadingNow && !IsDownloadUnavailable;

        public bool IsDownloadUnavailable => DownloadUnavailableReason.Length > 0;

        public string DownloadUnavailableReason { get; init; } = "";

        public ICommand DownloadCommand { get; }

        public bool CanCancel => IsDownloadingNow;

        public ICommand CancelCommand { get; }

        public bool CanDelete => IsInstalled && !IsDownloadingNow;

        public ICommand DeleteCommand { get; }

        public bool IsCustom { get; init; }

        public ICommand RemoveCustomCommand { get; }

        public bool IsDownloading => IsDownloadingNow;

        public double ProgressFraction { get; init; }

        public string ProgressText { get; init; } = "";
    }

    private sealed class FakeStemBackendPart : ObservableObject, IStemBackendPart
    {
        private string _provider = "DirectML";
        private int _gpu;

        private FakeStemBackendPart() => RefreshCommand = new RelayCommand(() => { });

        /// <summary>One GPU: DirectML runs, and there is nothing to choose, so no GPU row.</summary>
        internal static FakeStemBackendPart DirectMl() => new()
        {
            GpuOptions = [new FakeGpuOption("NVIDIA GeForce RTX 4080 · 16 GB", true, "")],
            Status = "ONNX backend READY (HtdemucsOnnx): available=[DmlExecutionProvider, CPUExecutionProvider]. " +
                     "Runs on: DirectML on 'NVIDIA GeForce RTX 4080' (adapter 0, 16376 MB)."
        };

        /// <summary>A desktop with a second card and an integrated GPU too small to run the model.</summary>
        internal static FakeStemBackendPart TwoGpus() => new()
        {
            GpuOptions =
            [
                new FakeGpuOption("NVIDIA GeForce RTX 5070 Ti · 16 GB", true, ""),
                new FakeGpuOption("NVIDIA GeForce RTX 3080 · 10 GB", true, ""),
                new FakeGpuOption("Intel(R) UHD Graphics 770 · 128 MB", false,
                    "Needs 3 GB of video memory; this GPU has 128 MB.")
            ],
            Status = "ONNX backend READY (HtdemucsOnnx): available=[DmlExecutionProvider, CPUExecutionProvider]. " +
                     "Runs on: DirectML on 'NVIDIA GeForce RTX 5070 Ti' (adapter 0, 15995 MB)."
        };

        /// <summary>No GPU that can run the model: DirectML greyed with its reason, the CPU in effect.</summary>
        internal static FakeStemBackendPart Cpu() => new()
        {
            _provider = "cpu",
            DirectMlAvailable = false,
            Status = "ONNX backend READY (HtdemucsOnnx): available=[DmlExecutionProvider, CPUExecutionProvider]. " +
                     "Runs on: CPU — no DirectX 12 GPU with 3 GB of video memory."
        };

        internal static FakeStemBackendPart Unregistered() => new()
        {
            Status = "No stem-separation service registered."
        };

        internal static FakeStemBackendPart LongText() => new()
        {
            GpuOptions =
            [
                new FakeGpuOption("NVIDIA GeForce RTX 5070 Ti Founders Edition (sehr langer Adaptername) · 16 GB", true, ""),
                new FakeGpuOption("AMD Radeon RX 7900 XTX — 非常に長いアダプター名のテストケース · 24 GB", true, "")
            ],
            Status = "Die Backend-Abfrage ist fehlgeschlagen: 非常に長いエラーメッセージのテストケースです。onnxruntime のインストールを確認してください。"
        };

        internal bool DirectMlAvailable { get; private init; } = true;

        public bool IsGroupVisible => true;

        public string Hint =>
            "The ONNX models run on ONNX Runtime with DirectML, which uses any DirectX 12 GPU — NVIDIA, AMD or " +
            "Intel — with nothing extra to download. Without one they run on the CPU. A change applies to the next " +
            "separation.";

        public bool IsExecutionProviderRowVisible => true;

        public IReadOnlyList<IStemExecutionProviderOption> ExecutionProviderOptions =>
        [
            new FakeProviderOption("DirectML", "DirectML", DirectMlAvailable,
                DirectMlAvailable ? "" : "No DirectX 12 GPU with 3 GB of video memory was found, so separation runs on the CPU."),
            new FakeProviderOption("cpu", "CPU", true, "")
        ];

        public IStemExecutionProviderOption SelectedExecutionProvider
        {
            get => ExecutionProviderOptions.First(o => ((FakeProviderOption)o).Value == _provider);
            set
            {
                if (value is FakeProviderOption { IsAvailable: true } option && option.Value != _provider)
                {
                    _provider = option.Value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsGpuRowVisible));
                }
            }
        }

        public bool IsGpuRowVisible => GpuOptions.Count >= 2 && _provider == "DirectML";

        public IReadOnlyList<IStemGpuOption> GpuOptions { get; private init; } = [];

        public IStemGpuOption? SelectedGpu
        {
            get => GpuOptions.Count == 0 ? null : GpuOptions[_gpu];
            set
            {
                if (value is not FakeGpuOption { IsAvailable: true } option) return;
                var index = GpuOptions.ToList().IndexOf(option);
                if (index < 0 || index == _gpu) return;
                _gpu = index;
                OnPropertyChanged();
            }
        }

        public bool IsStatusRowVisible => true;

        public string Status { get; private init; } = "";

        public ICommand RefreshCommand { get; }
    }

    private sealed class FakeGpuRuntimePart : ObservableObject, IGpuRuntimePart
    {
        private FakeGpuRuntimePart()
        {
            RemoveCommand = new RelayCommand(() => { });
            RefreshCommand = new RelayCommand(() => { });
        }

        /// <summary>The CUDA runtime an earlier version downloaded is still on disk.</summary>
        internal static FakeGpuRuntimePart Leftover() => new()
        {
            Status = "1.4 GB on disk, unused: stem separation runs on DirectML.",
            CanRemove = true
        };

        internal static FakeGpuRuntimePart Failed() => new()
        {
            Status = "1.4 GB on disk, unused: stem separation runs on DirectML.",
            CanRemove = true,
            Detail = "Removing failed: access to 'cudnn64_9.dll' is denied."
        };

        /// <summary>Nothing left to reclaim — the usual case: the row is gone, not greyed.</summary>
        internal static FakeGpuRuntimePart Absent() => new() { Hidden = true };

        internal static FakeGpuRuntimePart LongText() => new()
        {
            Status = "1,4 GB auf dem Datenträger, ungenutzt — 非常に長いステータス行のテストケースです。",
            CanRemove = true,
            Detail = "Das Entfernen ist fehlgeschlagen: Zugriff verweigert (0x80070005). 非常に長い詳細行。"
        };

        internal bool Hidden { get; private init; }

        public bool IsGroupVisible => !Hidden;

        public bool IsStatusRowVisible => true;

        public string Status { get; private init; } = "";

        public bool CanRemove { get; private init; }

        public ICommand RemoveCommand { get; }

        public ICommand RefreshCommand { get; }

        public string Detail { get; private init; } = "";

        public bool HasDetail => Detail.Length > 0;
    }

    private sealed class FakeStemCachePart : ObservableObject, IStemCachePart
    {
        private FakeStemCachePart()
        {
            ClearCommand = new RelayCommand(() => { });
            RefreshCommand = new RelayCommand(() => { });
        }

        internal static FakeStemCachePart Populated() => new()
        {
            Usage = "41 songs, 12.7 GB",
            HasEntries = true
        };

        internal static FakeStemCachePart Empty() => new() { Usage = "Nothing cached yet." };

        internal static FakeStemCachePart Failed() => new()
        {
            Usage = "41 songs, 12.7 GB",
            HasEntries = true,
            Detail = "Access to the path 'C:\\Users\\me\\AppData\\Local\\DotCharter\\stems' is denied."
        };

        internal static FakeStemCachePart LongText() => new()
        {
            Usage = "41 Songs, 12,7 GB — 非常に長いステータス行のテストケースです。",
            HasEntries = true,
            Detail = "Der Zwischenspeicher konnte nicht geleert werden: Zugriff verweigert (0x80070005)."
        };

        public bool IsGroupVisible => true;

        public bool IsUsageRowVisible => true;

        public string Usage { get; private init; } = "";

        public bool HasEntries { get; private init; }

        public ICommand ClearCommand { get; }

        public ICommand RefreshCommand { get; }

        public string Detail { get; private init; } = "";

        public bool HasDetail => Detail.Length > 0;
    }
}

internal sealed record FakeProviderOption(string Value, string Label, bool IsAvailable, string UnavailableReason)
    : IStemExecutionProviderOption;

internal sealed record FakeGpuOption(string Label, bool IsAvailable, string UnavailableReason) : IStemGpuOption;
