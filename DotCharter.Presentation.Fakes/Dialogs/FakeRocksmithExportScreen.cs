using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IRocksmithExportScreen" />; Export answers true.</summary>
public sealed class FakeRocksmithExportScreen : FakeDialog<bool>, IRocksmithExportScreen
{
    private FakeRocksmithExportScreen(IReadOnlyList<(string Name, string Type, bool On)> arrangements, string audio, string output,
        IReadOnlyList<(bool Error, string Location, string Message, string Fix)> findings, bool mac = false, bool building = false,
        string result = "", IReadOnlyList<string>? warnings = null, string dlcKey = "",
        bool extendedStrings = false)
    {
        HasExtendedStringGuitar = extendedStrings;
        // Mac alone (LongText, with a 7-string guitar ticked) or PC + Mac, the default when a
        // Mac package can be written.
        Target = new FakeTarget(mac ? 2 : 1);
        var macBlocked = mac && extendedStrings;
        PlatformNote = macBlocked
            ? SixStringsOnly
            : output.Length == 0 || mac ? "" : $"Writes {Path.GetFileName(output)} and {Path.GetFileName(output).Replace("_p.psarc", "_m.psarc", StringComparison.Ordinal)}.";
        Arrangements = [.. arrangements.Select(a => new Row(a.Name, a.Type, a.On))];
        AudioPath = audio;
        OutputPath = output;
        DlcKey = dlcKey;
        ValidationItems = [.. findings.Select(f => new Finding(f.Error, f.Location, f.Message, f.Location.Contains(':'), f.Fix))];
        IsBuilding = building;
        ProgressFraction = building ? 0.42 : 0;
        ProgressMessage = building ? "Step 3 of 7 — Writing SNG" : "";
        ResultPath = result;
        ResultWarnings = warnings ?? [];
        static ICommand Idle() => new RelayCommand(() => { });
        OpenToneDesignerCommand = Idle();
        ReviewReportCommand = building ? Disabled() : Idle();
        BrowseAudioCommand = Idle();
        BrowsePreviewAudioCommand = Idle();
        BrowseAlbumArtCommand = Idle();
        BrowseOutputCommand = Idle();
        // Greyed exactly where the real dialog greys it: a blocked export never looks live.
        ExportCommand = building || macBlocked || HasInvalidDlcKey || MissingPaths || NothingSelectedToExport || HasBlockingErrors
            ? Disabled()
            : Answer(true);
        // Every failing clause, one per line, in RocksmithExportViewModel.BlockingReasons' order.
        ExportBlockedReason = building ? "" : string.Join("\n", new[]
        {
            (macBlocked, "No Mac package can be written. " + SixStringsOnly),
            (HasBlockingErrors, "Fix the errors in the included arrangements before exporting."),
            (HasInvalidDlcKey, "The DLC key needs at least 6 alphanumeric characters."),
            (MissingPaths, "Choose a main audio file and an output file."),
            (NothingSelectedToExport, "Tick at least one arrangement — or include the vocals — before exporting.")
        }.Where(c => c.Item1).Select(c => c.Item2));
        CancelBuildCommand = building ? Idle() : Disabled();
        OpenOutputFolderCommand = Idle();
        CloseCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IRocksmithExportScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    public static FakeRocksmithExportScreen Default() => new(
        [("Lead", "Lead", true), ("Rhythm", "Rhythm", true), ("Bass", "Bass", false)],
        @"C:\Songs\Sakasma\audio.wem", @"C:\Exports\PolkadotStingray_Sakasma_p.psarc",
        [
            (false, "Lead · 0:01.250", "A level has no fret-hand positions (level has no anchors).",
                "Add fret-hand positions to Lead where none cover the notes; the positions already there stay."),
            (false, "Rhythm", "No phrases: Rocksmith needs at least COUNT and END.",
                "Generate the structure of Rhythm: COUNT and END phrases and the no-guitar sections."),
            (false, "Lead", "Tone \"clean_verse\" has no definition and plays as the default tone.",
                "Open the Tone designer on Lead."),
            (false, "Lead · 1:23.456", "Chord has no fingering.", "")
        ]);

    /// <summary>Nothing to export yet: no arrangement ticked, no audio, and each gate says why.</summary>
    public static FakeRocksmithExportScreen Empty() => new([], "", "", []);

    public static FakeRocksmithExportScreen LongText() => new(
        [("Leadgitarre (alternative Stimmung)", "Lead", true), ("リズムギター", "Rhythm", true)],
        @"C:\Benutzer\Musik\Ein sehr langer Ordnername mit Umlauten\Übergang zur zweiten Strophe\audio.wem",
        @"C:\Benutzer\Exporte\EinSehrLangerKünstlername_EinNochLängererSongtitelMitUmlauten_m.psarc",
        [(true, "Leadgitarre (alternative Stimmung) · 0:12.500", "Eine Note liegt außerhalb des Bundbereichs der Stimmung und würde im Spiel nicht angezeigt.", ""),
            (true, "Leadgitarre (alternative Stimmung) · 0:00.250", "3 Noten liegen vor der ersten Griffhandposition.",
                "Fügt Leadgitarre (alternative Stimmung) Griffhandpositionen vor der ersten hinzu."),
            (false, "リズムギター", "コードの運指がありません。", "")],
        mac: true, dlcKey: "x1", extendedStrings: true);

    public static FakeRocksmithExportScreen Full() => new(
        [.. Enumerable.Range(1, 6).Select(i => ($"Arrangement {i}", i % 2 == 0 ? "Rhythm" : "Lead", i < 5))],
        @"C:\Songs\Sakasma\audio.wem", @"C:\Exports\PolkadotStingray_Sakasma_p.psarc",
        [.. Enumerable.Range(1, 24).Select(i => (i == 1, $"Lead · {i / 6}:{i * 7 % 60:00}.000", FullMessages[i % FullMessages.Length],
            i % 3 == 1 ? "Auto-fix Lead." : ""))],
        result: @"C:\Exports\PolkadotStingray_Sakasma_p.psarc",
        warnings: ["Preview audio was taken from the song start.", "Album art was scaled down to 256 px."])
    {
        WritesUnencrypted = true
    };

    /// <summary>A long list of the validator's usual findings: more rows than a 1280×720 window shows at once.</summary>
    private static readonly string[] FullMessages =
    [
        "Chord has no fingering.",
        "Two notes overlap on the same string.",
        "A level has no fret-hand positions (level has no anchors).",
        "A note sits below the capo.",
        "Phrase \"riff\" has no notes in its hardest level."
    ];

    public static FakeRocksmithExportScreen Busy() => new(
        [("Lead", "Lead", true)], @"C:\Songs\Sakasma\audio.wem", @"C:\Exports\PolkadotStingray_Sakasma_p.psarc", [],
        building: true);

    public IExportTarget Target { get; }
    public string PlatformNote { get; }
    public bool HasPlatformNote => PlatformNote.Length > 0;
    public IReadOnlyList<IExportArrangementRow> Arrangements { get; }
    public bool IncludeVocals { get; set; }
    public bool HasVocals => false;
    public bool SplitExtendedStringChords { get; set; }
    public bool HasExtendedStringGuitar { get; }
    public string AudioPath { get; set; }
    public string PreviewAudioPath { get; set; } = "";
    public double AudioVolume { get; set; } = -8;
    public double PreviewVolume { get; set; } = -7;
    public ICommand AutoVolumeCommand { get; } = new RelayCommand(() => { });
    public ICommand AutoPreviewVolumeCommand { get; } = new RelayCommand(() => { });
    public bool IsMeasuringVolume => false;
    public string VolumeMessage => "";
    public bool HasVolumeMessage => false;
    public bool WritesUnencrypted { get; init; }
    public string DlcKey { get; set; }
    public string Version { get; set; } = "1";
    public string TestFolder { get; set; } = "";
    public string TestFolderWatermark => @"C:\Exports\test";
    public string DlcKeyWatermark => "Automatic — PolkadotStingray_Sakasma";
    public string AppId { get; set; } = "248750";
    public string AlbumArtPath { get; set; } = "";
    public string OutputPath { get; set; }
    public string ValidationSummary => HasFindings
        ? $"{ValidationItems.Count(f => f.IsError)} errors, {ValidationItems.Count(f => !f.IsError)} warnings"
        : "No problems found.";
    public bool HasFindings => ValidationItems.Count > 0;
    public ICommand ReviewReportCommand { get; }
    public IReadOnlyList<IExportFinding> ValidationItems { get; }
    public string ExportBlockedReason { get; }
    public bool IsExportBlocked => ExportBlockedReason.Length > 0;
    public bool IsBuilding { get; }
    public double ProgressFraction { get; }
    public string ProgressMessage { get; }
    public bool HasResult => ResultPath.Length > 0;
    public string ResultPath { get; }
    public IReadOnlyList<string> ResultWarnings { get; }
    public ICommand OpenToneDesignerCommand { get; }
    public ICommand BrowseAudioCommand { get; }
    public ICommand BrowsePreviewAudioCommand { get; }
    public ICommand BrowseAlbumArtCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand BrowseTestFolderCommand { get; } = new RelayCommand(() => { });
    public ICommand TestExportCommand { get; } = new RelayCommand(() => { });
    public ICommand CancelBuildCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand CloseCommand { get; }
    private bool NothingSelectedToExport => !Arrangements.Any(a => a.IsSelected);
    private bool HasInvalidDlcKey => DlcKey.Length is > 0 and < 6;
    private bool MissingPaths => AudioPath.Length == 0 || OutputPath.Length == 0;
    private bool HasBlockingErrors => ValidationItems.Any(f => f.IsError);

    private const string SixStringsOnly = "A 7- or 8-string arrangement is ticked: it plays only on Windows (the rs7string mod).";

    private sealed record Choice(string Label) : IExportChoice;

    private sealed record Finding(bool IsError, string Location, string Message, bool HasTime, string FixDescription) : IExportFinding
    {
        public ICommand JumpCommand { get; } = new RelayCommand(() => { });
        public bool HasFix => FixDescription.Length > 0;
        public ICommand FixCommand { get; } = new RelayCommand(() => { });
    }

    private sealed class Row(string name, string type, bool on) : ObservableObject, IExportArrangementRow
    {
        public string Name { get; } = name;
        public string TypeLabel { get; } = type;
        public bool IsSelected { get; set; } = on;
    }

    private sealed class FakeTarget : ObservableObject, IExportTarget
    {
        public FakeTarget(int platform)
        {
            PlatformOptions = [new Choice("Windows (PC)"), new Choice("Windows + macOS (PC + Mac)"), new Choice("macOS (Mac)")];
            SelectedPlatformOption = PlatformOptions[platform];
            PackageOptions = [new Choice("Encrypted (default)"), new Choice("Plain (needs the CDLC enabler's plain-PSARC support)")];
            SelectedPackageOption = PackageOptions[0];
        }

        public IReadOnlyList<IExportChoice> PlatformOptions { get; }
        public IExportChoice SelectedPlatformOption { get; set; }
        public IReadOnlyList<IExportChoice> PackageOptions { get; }
        public IExportChoice SelectedPackageOption { get; set; }
        public bool CanChoosePackage => true;
    }
}
