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

    /// <summary>A 7-/8-string guitar: ticked, it rules the Mac package out.</summary>
    public bool IsExtendedString { get; init; }
}

public sealed record ExportPlatformChoice(ExportPlatform Value, string Label) : IExportChoice;

public sealed record ExportPackageChoice(bool Plain, string Label) : IExportChoice;

public sealed record ExportFindingRow(bool IsError, string Location, string Message, bool HasTime, ICommand JumpCommand,
    ExportFindingFix Fix) : IExportFinding
{
    public bool HasFix => Fix.Available;

    public string FixDescription => Fix.Description;

    public ICommand FixCommand => Fix.Command;
}

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

    public ExportTargetViewModel(ILocalizationService loc)
    {
        PlatformOptions =
        [
            new ExportPlatformChoice(ExportPlatform.Pc, loc["Dialog_Export_PlatformPc"]),
            new ExportPlatformChoice(ExportPlatform.PcAndMac, loc["Dialog_Export_PlatformPcAndMac"]),
            new ExportPlatformChoice(ExportPlatform.Mac, loc["Dialog_Export_PlatformMac"])
        ];
        PackageOptions =
        [
            new ExportPackageChoice(false, loc["Dialog_Export_PackageEncrypted"]),
            new ExportPackageChoice(true, loc["Dialog_Export_PackagePlain"])
        ];
        // Encrypted unless asked otherwise; without keys only plain can be written.
        _selectedPackage = PackageOptions[RocksmithKeys.CanWriteEncrypted ? 0 : 1];
        // DLC Builder's default, PC + Mac, wherever a Mac package can be encrypted at all.
        _selectedPlatform = PlatformOptions.First(o =>
            o.Value == (RocksmithKeys.CanWriteMac ? ExportPlatform.PcAndMac : ExportPlatform.Pc));
    }

    /// <summary>The platform changed: re-gate and re-suffix.</summary>
    public event EventHandler? TargetChanged;

    public IReadOnlyList<ExportPlatformChoice> PlatformOptions { get; }
    public IReadOnlyList<ExportPackageChoice> PackageOptions { get; }

    /// <summary>The package form can be picked: it cannot without keys, where plain is the only one.</summary>
    public bool CanChoosePackage => RocksmithKeys.CanWriteEncrypted;

    /// <summary>The PC package is written plain, by choice or for want of keys.</summary>
    public bool WritesPlain => SelectedPackageOption.Plain || !RocksmithKeys.CanWriteEncrypted;

    /// <summary>The PC package is written plain, so the dialog warns: a Mac-only target writes no PC package.</summary>
    public bool WritesPlainPackage => WritesPlain && !IsMacOnly;

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
            if (value is null || !SetProperty(ref _selectedPackage, value)) return;
            OnPropertyChanged(nameof(WritesPlain));
            OnPropertyChanged(nameof(WritesPlainPackage));
        }
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
