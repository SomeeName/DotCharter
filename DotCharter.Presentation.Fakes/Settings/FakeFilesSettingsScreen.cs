using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Files &amp; backups tab in every state the screen can be in.</summary>
public sealed partial class FakeFilesSettingsScreen : ObservableObject, IFilesSettingsScreen
{
    [ObservableProperty] private bool _saveCompanionEof = true;
    [ObservableProperty] private bool _saveCompanionEofAudio = true;
    [ObservableProperty] private bool _saveCompanionPsarc = true;
    [ObservableProperty] private int _saveCompanionPsarcCodecIndex;
    [ObservableProperty] private bool _saveCompanionRs2Dlc = true;
    [ObservableProperty] private bool _rsXmlSixStringProjection;
    [ObservableProperty] private bool _exportGenerateDynamicDifficulty = true;
    [ObservableProperty] private bool _exportDdUseMlModel;
    [ObservableProperty] private bool _exportApplyDlcBuilderFixes = true;
    [ObservableProperty] private bool _autoBackupEnabled = true;
    [ObservableProperty] private int _autoBackupIntervalMinutes = 5;
    [ObservableProperty] private int _autoBackupKeepCount = 50;
    [ObservableProperty] private bool _psarcOpenHardestLevelOnly;

    private FakeFilesSettingsScreen(FakeBackupMigratePart backup) => Backup = backup;

    public static IReadOnlyList<FakeVariant<IFilesSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new("Cleared", Cleared),
        new(PackageReaderOffVariant, PackageReaderOff)
    ];

    /// <summary>The variant of a build that cannot read game packages.</summary>
    public const string PackageReaderOffVariant = "PackageReaderOff";

    /// <summary>The keys a build without the package reader hides: the decoded-audio cache and the open options.</summary>
    public static IReadOnlySet<string> PackageReaderKeys { get; } =
        new HashSet<string> { "files.group.2", "files.psarcCache", "files.group.3", "files.psarcHardestOnly" };

    public static FakeFilesSettingsScreen Default() => new(new FakeBackupMigratePart())
    {
        LibraryAudioCacheUsage = "412 songs, 6.1 GB of 8.0 GB",
        LibraryAudioCacheHasEntries = true
    };

    /// <summary>Nothing written on save, no timed backup, an empty cache.</summary>
    public static FakeFilesSettingsScreen Empty() => new(new FakeBackupMigratePart())
    {
        SaveCompanionEof = false,
        SaveCompanionPsarc = false,
        SaveCompanionRs2Dlc = false,
        AutoBackupEnabled = false,
        LibraryAudioCacheUsage = "Empty — no Rocksmith song audio has been decoded yet."
    };

    public static FakeFilesSettingsScreen LongText() =>
        new(new FakeBackupMigratePart
        {
            StatusText = @"Sicherung gespeichert (1.284 Dateien): D:\Benutzerdaten\Sicherungen\DotCharter\DotCharter-backup-2026-09-22.zip — 復元にはこのファイルを使用してください。"
        })
        {
            SaveCompanionPsarcCodecIndex = 1,
            AutoBackupIntervalMinutes = 1440,
            LibraryAudioCacheUsage = "2.543 Songs, 127,6 GB von 8,0 GB (wird beim nächsten Start bereinigt)",
            LibraryAudioCacheHasEntries = true,
            LibraryAudioCacheDetail = "Der Ordner konnte nicht gelöscht werden: Der Prozess kann nicht auf die Datei zugreifen, da sie von einem anderen Prozess verwendet wird."
        };

    /// <summary>An export in flight: both backup actions are unavailable while it runs.</summary>
    public static FakeFilesSettingsScreen Busy()
    {
        var screen = Default();
        ((FakeBackupMigratePart)screen.Backup).IsBusy = true;
        return screen;
    }

    /// <summary>Everything a save can write, the ADPCM codec, a backup result and a populated cache.</summary>
    public static FakeFilesSettingsScreen Full() =>
        new(new FakeBackupMigratePart { StatusText = @"Backup saved (1,284 files): D:\Backups\DotCharter-backup-2026-09-22.zip" })
        {
            SaveCompanionPsarcCodecIndex = 1,
            LibraryAudioCacheUsage = "2,543 songs, 7.9 GB of 8.0 GB",
            LibraryAudioCacheHasEntries = true
        };

    /// <summary>The cache just cleared, with the result line under it.</summary>
    public static FakeFilesSettingsScreen Cleared() => new(new FakeBackupMigratePart())
    {
        LibraryAudioCacheUsage = "Empty — no Rocksmith song audio has been decoded yet.",
        LibraryAudioCacheDetail = "Cleared 412 decoded songs."
    };

    /// <summary>No package reader: those groups are ABSENT, the companion package rows (the writer) stay.</summary>
    public static FakeFilesSettingsScreen PackageReaderOff() => new(new FakeBackupMigratePart())
    {
        HiddenKeys = PackageReaderKeys
    };

    public IBackupMigratePart Backup { get; }

    public string LibraryAudioCacheUsage { get; private init; } = "";

    public bool LibraryAudioCacheHasEntries { get; private init; }

    public string LibraryAudioCacheDetail { get; private init; } = "";

    public bool HasLibraryAudioCacheDetail => LibraryAudioCacheDetail.Length > 0;

    public ICommand ClearLibraryAudioCacheCommand { get; } = new RelayCommand(() => { });

    public ICommand RefreshLibraryAudioCacheCommand { get; } = new RelayCommand(() => { });

    public IReadOnlySet<string> HiddenKeys { get; private init; } = new HashSet<string>();
}

/// <summary>The export / import block with its status line.</summary>
public sealed class FakeBackupMigratePart : ObservableObject, IBackupMigratePart
{
    public ICommand ExportCommand { get; } = new RelayCommand(() => { });

    public ICommand ImportCommand { get; } = new RelayCommand(() => { });

    public bool IsBusy { get; set; }

    public string StatusText { get; init; } = "";

    public bool HasStatus => StatusText.Length > 0;
}
