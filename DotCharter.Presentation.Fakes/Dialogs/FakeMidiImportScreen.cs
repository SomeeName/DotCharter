using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IMidiImportScreen" />; Import answers true, Cancel false.</summary>
public sealed class FakeMidiImportScreen : FakeDialog<bool>, IMidiImportScreen
{
    private int _selectedTrackIndex;
    private bool _includeDrumChannel;
    private bool _replaceBeatGrid;

    private FakeMidiImportScreen(string fileName, IReadOnlyList<string> tracks, bool busy, string summary,
        string error, bool canImport)
    {
        FileName = fileName;
        Tracks = tracks;
        IsBusy = busy;
        Summary = summary;
        ErrorMessage = error;
        _replaceBeatGrid = true;
        ImportCommand = canImport ? Answer(true) : Disabled();
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IMidiImportScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error),
        new(FakeVariantNames.Full, Full)
    ];

    private static readonly string[] SongTracks = ["All tracks", "Guitar (412)", "Bass (288)", "Keys (96)"];

    public static FakeMidiImportScreen Default() =>
        new("song.mid", SongTracks, false, "412 notes will be drafted into a new arrangement.", "", true);

    /// <summary>The honest empty state: nothing in the selection maps onto the tuning.</summary>
    public static FakeMidiImportScreen Empty() =>
        new("drums.mid", ["All tracks"], false, "No note in this selection maps onto the arrangement's tuning.", "",
            false);

    public static FakeMidiImportScreen LongText() =>
        new("Sehr langer Songtitel mit Untertitel (Live-Version, remastert) – ライブ版.mid",
            ["Alle Spuren", "Leadgitarre mit Verzerrung und Hall (412)", "ベースギター（5弦）(288)"], false,
            "412 Noten werden als neues Arrangement entworfen; 既存の譜面は変更されません。", "", true);

    public static FakeMidiImportScreen Busy() => new("song.mid", SongTracks, true, "", "", false);

    /// <summary>A CONVERSION CRASH — the error strip, never the empty summary.</summary>
    public static FakeMidiImportScreen Error() =>
        new("song.mid", SongTracks, false, "",
            "The conversion failed: InvalidOperationException: running status without a previous event.", false);

    /// <summary>A General MIDI arrangement with a track per instrument.</summary>
    public static FakeMidiImportScreen Full() =>
        new("orchestral.mid", ["All tracks", .. Enumerable.Range(1, 16).Select(i => $"Channel {i} ({i * 37})")], false,
            "592 notes will be drafted into a new arrangement.", "", true);

    public string FileName { get; }

    public IReadOnlyList<string> Tracks { get; }

    public int SelectedTrackIndex
    {
        get => _selectedTrackIndex;
        set => SetProperty(ref _selectedTrackIndex, value);
    }

    public bool IncludeDrumChannel
    {
        get => _includeDrumChannel;
        set => SetProperty(ref _includeDrumChannel, value);
    }

    public bool ReplaceBeatGrid
    {
        get => _replaceBeatGrid;
        set => SetProperty(ref _replaceBeatGrid, value);
    }

    public bool IsBusy { get; }

    public string Summary { get; }

    public string ErrorMessage { get; }

    public ICommand ImportCommand { get; }

    public ICommand CancelCommand { get; }
}
