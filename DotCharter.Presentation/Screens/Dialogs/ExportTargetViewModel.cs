using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Psarc;

namespace DotCharter.Presentation.Screens.Dialogs;

public sealed partial class ExportArrangementRow : ObservableObject, IExportArrangementRow
{
    [ObservableProperty] private bool _isSelected = true;

    public required string Name { get; init; }

    public required string TypeLabel { get; init; }

    /// <summary>Whether the source arrangement has extended guitar or bass strings.</summary>
    public bool IsExtendedString { get; init; }
}

public sealed record ExportPlatformChoice(ExportPlatform Value, string Label) : IExportChoice;

public sealed record ExportPackageChoice(bool Plain, string Label) : IExportChoice;

public sealed record ExportFindingRow(bool IsError, string Location, string Message, bool HasTime, ICommand JumpCommand,
    ExportFindingFix Fix, ExportFindingLink Link) : IExportFinding
{
    public bool HasSongMetadataLink => Link.Available;

    public ICommand OpenSongMetadataCommand => Link.Command;

    public bool HasFix => Fix.Available;

    public string FixDescription => Fix.Description;

    public ICommand FixCommand => Fix.Command;
}

/// <summary>A finding's way to Song metadata, where its text is edited: whether it has one and the command that opens it.</summary>
public sealed record ExportFindingLink(bool Available, ICommand Command);

/// <summary>A finding's Fix: whether a verb repairs it, what it does, and the command that runs it.</summary>
public sealed record ExportFindingFix(bool Available, string Description, ICommand Command);

/// <summary>
///     The export target: the platform and the package form. <see cref="TargetChanged" /> tells the
///     dialog to re-gate and to re-suffix the output file.
/// </summary>
public sealed partial class ExportTargetViewModel : ObservableObject, IExportTarget
{
    private ExportPlatformChoice _selectedPlatform;
    private ExportPackageChoice _selectedPackage;
    private ExportPackageChoice? _stockPackage;
    private bool _extendedRange;

    public ExportTargetViewModel(ILocalizationService loc)
    {
        PlatformOptions =
        [
            new ExportPlatformChoice(ExportPlatform.Pc, loc["Dialog_Export_PlatformPc"]),
            new ExportPlatformChoice(ExportPlatform.PcAndMac, loc["Dialog_Export_PlatformPcAndMac"]),
            new ExportPlatformChoice(ExportPlatform.Mac, loc["Dialog_Export_PlatformMac"])
        ];
        var plain = new ExportPackageChoice(true, loc["Dialog_Export_PackagePlain"]);
        PackageOptions = RocksmithKeys.CanWriteEncrypted
            ? [new ExportPackageChoice(false, loc["Dialog_Export_PackageEncrypted"]), plain]
            : [plain];
        // Encrypted unless asked otherwise; without keys only plain can be written.
        _selectedPackage = PackageOptions[0];
        // DLC Builder's default, PC + Mac, wherever a Mac package can be encrypted at all.
        _selectedPlatform = PlatformOptions.First(o =>
            o.Value == (RocksmithKeys.CanWriteMac ? ExportPlatform.PcAndMac : ExportPlatform.Pc));
    }

    /// <summary>The platform changed: re-gate and re-suffix.</summary>
    public event EventHandler? TargetChanged;

    /// <summary>The package form changed: re-gate and update the extended-range file tag.</summary>
    public event EventHandler? PackageChanged;

    public IReadOnlyList<ExportPlatformChoice> PlatformOptions { get; }
    public IReadOnlyList<ExportPackageChoice> PackageOptions { get; }

    public bool CanChoosePackage => !_extendedRange && RocksmithKeys.CanWriteEncrypted;

    public bool WritesExtendedRange => _extendedRange;

    /// <summary>The PC package is written plain, by choice or for want of keys.</summary>
    public bool WritesPlain => _extendedRange || SelectedPackageOption.Plain || !RocksmithKeys.CanWriteEncrypted;

    /// <summary>The PC package is written plain, so the dialog warns: a Mac-only target writes no PC package.</summary>
    public bool WritesPlainPackage => WritesPlain && !IsMacOnly && !_extendedRange;

    /// <summary>The stock package's form, which the RS Extended Range package overrides: the standard
    /// package beside it is written so.</summary>
    internal bool StockWritesPlain => (_extendedRange ? _stockPackage ?? PackageOptions[0] : _selectedPackage).Plain || !RocksmithKeys.CanWriteEncrypted;

    /// <summary>The RS Extended Range package is plain on Mac too, so only stock content can be blocked.</summary>
    internal RocksmithMacBlock? MacUnavailableReason(bool extendedStrings) =>
        extendedStrings ? null : RocksmithPlatformTargets.MacUnavailableReason();

    /// <summary>The target writes a Mac package (Mac or PC + Mac).</summary>
    public bool IncludesMac => SelectedPlatformOption.Value != ExportPlatform.Pc;

    /// <summary>The target writes ONLY the Mac package (<c>_m.psarc</c>).</summary>
    public bool IsMacOnly => SelectedPlatformOption.Value == ExportPlatform.Mac;

    public ExportPlatformChoice SelectedPlatformOption
    {
        get => _selectedPlatform;
        set
        {
            if (value is null || !SetProperty(ref _selectedPlatform, value)) return;
            OnPropertyChanged(nameof(IncludesMac));
            OnPropertyChanged(nameof(IsMacOnly));
            OnPropertyChanged(nameof(WritesPlainPackage));
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ExportPackageChoice SelectedPackageOption
    {
        get => _selectedPackage;
        set
        {
            if (value is null || _extendedRange && !value.Plain || !SetProperty(ref _selectedPackage, value)) return;
            OnPropertyChanged(nameof(WritesPlain));
            OnPropertyChanged(nameof(WritesPlainPackage));
            PackageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void SetExtendedContent(bool extendedRange)
    {
        if (_extendedRange == extendedRange) return;
        if (extendedRange) _stockPackage = _selectedPackage;
        _extendedRange = extendedRange;
        _selectedPackage = extendedRange ? PackageOptions.Single(p => p.Plain) : _stockPackage ?? PackageOptions[0];
        OnPropertyChanged(nameof(SelectedPackageOption));
        OnPropertyChanged(nameof(CanChoosePackage));
        OnPropertyChanged(nameof(WritesExtendedRange));
        OnPropertyChanged(nameof(WritesPlain));
        OnPropertyChanged(nameof(WritesPlainPackage));
        PackageChanged?.Invoke(this, EventArgs.Empty);
    }

    IReadOnlyList<IExportChoice> IExportTarget.PackageOptions => PackageOptions;

    IExportChoice IExportTarget.SelectedPackageOption
    {
        get => SelectedPackageOption;
        set => SelectedPackageOption = (value as ExportPackageChoice)!;
    }

    IReadOnlyList<IExportChoice> IExportTarget.PlatformOptions => PlatformOptions;

    IExportChoice IExportTarget.SelectedPlatformOption
    {
        get => SelectedPlatformOption;
        set => SelectedPlatformOption = (value as ExportPlatformChoice)!;
    }
}
