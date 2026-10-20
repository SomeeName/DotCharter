using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>The states a fake song-package export opens in.</summary>
public sealed record FakeDcSongState
{
    public string Song { get; init; } = "Polkadot Stingray — Sakasama";
    public IReadOnlyList<(string Name, string Type, bool On)> Arrangements { get; init; } = [];
    public IReadOnlyList<string> Stems { get; init; } = [];
    public bool Lossless { get; init; }
    public bool IncludeStems { get; init; }
    public string Cover { get; init; } = "";
    public string Banner { get; init; } = "";
    public string Output { get; init; } = @"D:\Rocksmith\Songs\Polkadot Stingray - Sakasama.dcsong";
    public string OutputNote { get; init; } = "In your Library folder.";
    public string Size { get; init; } = "Estimated size: about 5.1 MB";
    public string Blocked { get; init; } = "";
    public bool Writing { get; init; }
    public string Result { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Error { get; init; } = "";
}

/// <summary>A services-free <see cref="IDcSongExportScreen" />; Export answers true, Close false.</summary>
public sealed class FakeDcSongExportScreen : FakeDialog<bool>, IDcSongExportScreen
{
    private const string StemsNone = "No separated stems for this song. Tools ▸ Separate stems makes them.";

    private FakeDcSongExportScreen(FakeDcSongState s)
    {
        SongLine = s.Song;
        AudioQualityIndex = s.Lossless ? 1 : 0;
        AudioQualityNote = s.Lossless
            ? "Lossless, for charting: chart from audio and stem separation can use it. About five times larger."
            : "Small, for playing. Compressed audio is playback-only.";
        Arrangements = [.. s.Arrangements.Select(a => new Row(a.Name, a.Type, a.On))];
        CanIncludeStems = s.Stems.Count > 0;
        IncludeStems = s.IncludeStems;
        StemsNote = s.Stems.Count == 0 ? StemsNone : $"{s.Stems.Count} stems: {string.Join(", ", s.Stems)} · adds about {s.Stems.Count * 4.3:0.0} MB";
        CoverPath = s.Cover;
        CoverNote = "Empty uses the chart's own cover art.";
        BannerPath = s.Banner;
        OutputPath = s.Output;
        OutputNote = s.OutputNote;
        EstimatedSize = s.Size;
        ExportBlockedReason = s.Writing ? "" : s.Blocked;
        IsWriting = s.Writing;
        ProgressFraction = s.Writing ? 0.4 : 0;
        ProgressMessage = s.Writing ? "Step 2 of 6: Encoding the song audio" : "";
        ResultMessage = s.Result;
        ResultWarnings = s.Warnings;
        ErrorMessage = s.Error;
        static ICommand Idle() => new RelayCommand(() => { });
        BrowseCoverCommand = Idle();
        BrowseBannerCommand = Idle();
        BrowseOutputCommand = Idle();
        ExportCommand = s.Writing || IsExportBlocked ? Disabled() : Answer(true);
        CancelWriteCommand = s.Writing ? Idle() : Disabled();
        OpenOutputFolderCommand = Idle();
        CloseCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IDcSongExportScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error),
        new("NoFfmpeg", NoFfmpeg),
        new("Written", Written)
    ];

    /// <summary>Three arrangements, four stems separated (offered, off), Opus.</summary>
    public static FakeDcSongExportScreen Default() => new(new FakeDcSongState
    {
        Arrangements = [("Lead", "", true), ("Rhythm", "", true), ("Bass", "", true)],
        Stems = ["bass", "drums", "other", "vocals"]
    });

    /// <summary>A chart with no arrangement and no stems; Export says why it is grey.</summary>
    public static FakeDcSongExportScreen Empty() => new(new FakeDcSongState
    {
        Song = "Untitled", Output = "", OutputNote = "No Library folder is set.", Size = "Estimated size: —",
        Blocked = "Tick at least one arrangement.\nChoose where to save the package."
    });

    /// <summary>German and Japanese names, a deep path and the longest notes.</summary>
    public static FakeDcSongExportScreen LongText() => new(new FakeDcSongState
    {
        Song = "Die Ärzte — Westerland (Live in der Wuhlheide, ungekürzte Fassung) / 東京事変 — 群青日和",
        Arrangements =
        [
            ("Leadgitarre (alternative Stimmung)", "Lead", true), ("リズムギター", "Rhythm", true),
            ("Bass (fünfsaitig, Drop A)", "Bass", false)
        ],
        Stems = ["vocals", "drums", "bass", "other", "guitar", "piano"],
        Cover = @"C:\Benutzer\Musik\Ein sehr langer Ordnername mit Umlauten\Übergang\Cover (hochauflösend).png",
        Output = @"C:\Benutzer\Musik\Bibliothek\Die Ärzte - Westerland (Live in der Wuhlheide, ungekürzte Fassung).dcsong",
        OutputNote = "Outside your Library folder.",
        Size = "Estimated size: about 128.4 MB"
    });

    /// <summary>Eight arrangements, six stems in, FLAC, both images.</summary>
    public static FakeDcSongExportScreen Full() => new(new FakeDcSongState
    {
        Arrangements =
        [
            ("Lead", "", true), ("Lead 2", "Lead", true), ("Rhythm", "", true), ("Rhythm 2", "Rhythm", true),
            ("Bass", "", true), ("Combo", "", false), ("Bonus Lead", "Lead", true), ("Alt. Bass", "Bass", false)
        ],
        Stems = ["vocals", "drums", "bass", "other", "guitar", "piano"],
        Lossless = true, IncludeStems = true,
        Cover = @"D:\Art\sakasama-cover.png", Banner = @"D:\Art\sakasama-banner.jpg",
        Size = "Estimated size: about 47.9 MB"
    });

    /// <summary>The write running: progress, Cancel export, the body disabled.</summary>
    public static FakeDcSongExportScreen Busy() => new(new FakeDcSongState
    {
        Arrangements = [("Lead", "", true), ("Rhythm", "", true), ("Bass", "", true)],
        Stems = ["bass", "drums", "other", "vocals"], IncludeStems = true, Writing = true, Size = "Estimated size: about 22.3 MB"
    });

    /// <summary>The last write failed; the reason is in the dialog.</summary>
    public static FakeDcSongExportScreen Error() => new(new FakeDcSongState
    {
        Arrangements = [("Lead", "", true), ("Rhythm", "", true)],
        Cover = @"D:\Art\cover.webp",
        Error = "The image cannot be used. Choose a PNG or JPEG file under 16 MB."
    });

    /// <summary>No ffmpeg: Export is grey and says so before anything is chosen.</summary>
    public static FakeDcSongExportScreen NoFfmpeg() => new(new FakeDcSongState
    {
        Arrangements = [("Lead", "", true), ("Rhythm", "", true), ("Bass", "", true)],
        Blocked = "ffmpeg was not found, and every song package needs it to encode the audio. " +
                  "Put ffmpeg on your PATH, or ffmpeg.exe in the app's tools folder."
    });

    /// <summary>Written, with what the writer wants the user to know.</summary>
    public static FakeDcSongExportScreen Written() => new(new FakeDcSongState
    {
        Arrangements = [("Lead", "", true), ("Rhythm", "", true), ("Bass", "", true)],
        Lossless = true,
        Result = @"Saved D:\Rocksmith\Songs\Polkadot Stingray - Sakasama.dcsong (27.6 MB).",
        Warnings =
        [
            "The song audio is lossy, so the lossless package holds its decoded audio, not the original quality.",
            "The cover art of the project is missing or not a PNG or JPEG, so the package has no cover."
        ]
    });

    public string SongLine { get; }
    public IReadOnlyList<string> AudioQualityOptions { get; } = ["Opus 192 kb/s", "FLAC (lossless)"];
    public int AudioQualityIndex { get; set; }
    public string AudioQualityNote { get; }
    public IReadOnlyList<IDcSongArrangementRow> Arrangements { get; }
    public bool IncludeStems { get; set; }
    public bool CanIncludeStems { get; }
    public string StemsNote { get; }
    public string CoverPath { get; set; }
    public string CoverNote { get; }
    public string BannerPath { get; set; }
    public string OutputPath { get; set; }
    public string OutputNote { get; }
    public string EstimatedSize { get; }
    public string ExportBlockedReason { get; }
    public bool IsExportBlocked => ExportBlockedReason.Length > 0;
    public bool IsWriting { get; }
    public double ProgressFraction { get; }
    public string ProgressMessage { get; }
    public bool HasResult => ResultMessage.Length > 0;
    public string ResultMessage { get; }
    public IReadOnlyList<string> ResultWarnings { get; }
    public bool HasError => ErrorMessage.Length > 0;
    public string ErrorMessage { get; }
    public ICommand BrowseCoverCommand { get; }
    public ICommand BrowseBannerCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelWriteCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand CloseCommand { get; }

    private sealed class Row(string name, string type, bool on) : ObservableObject, IDcSongArrangementRow
    {
        private bool _isSelected = on;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string Name { get; } = name;

        public string TypeLabel { get; } = type;

        public override string ToString() => Name;
    }
}
