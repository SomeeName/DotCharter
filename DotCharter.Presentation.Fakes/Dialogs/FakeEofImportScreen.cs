using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Import.Eof;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IEofImportScreen" />; Import answers a selection, Cancel null.</summary>
public sealed class FakeEofImportScreen : FakeDialog<EofImportSelection?>, IEofImportScreen
{
    private FakeEofImportScreen(IReadOnlyList<string> tracks, bool busy, string error)
    {
        AvailableTracks = tracks.Select(IEofTrackOption (t) => new Track(t)).ToList();
        IsBusy = busy;
        ErrorMessage = error;
        ImportCommand = busy
            ? Disabled()
            : Answer(new EofImportSelection(@"C:\Charts\song.eof", new EofImportResult(), @"C:\Charts\guitar.ogg"));
        CancelCommand = busy ? Disabled() : Answer(null);
    }

    public static IReadOnlyList<FakeVariant<IEofImportScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error),
        new(FakeVariantNames.Full, Full)
    ];

    private static readonly string[] Tracks =
        ["PART REAL_GUITAR (812 notes)", "PART REAL_BASS (604 notes)", "PART VOCALS (214 lyrics)"];

    public static FakeEofImportScreen Default() => new(Tracks, false, "");

    /// <summary>A file with no Pro Guitar track that has notes.</summary>
    public static FakeEofImportScreen Empty() => new([], false, "");

    public static FakeEofImportScreen LongText() =>
        new(["PART REAL_GUITAR_22 Leadgitarre mit alternativer Stimmung (Drop C, Kapodaster 2) (1204 notes)",
            "リズムギター（第二アレンジメント）(604 notes)"], false, "");

    public static FakeEofImportScreen Busy() => new(Tracks, true, "");

    public static FakeEofImportScreen Error() =>
        new(Tracks, false, @"No song audio next to C:\Charts\song.eof — put guitar.ogg or song.ogg beside it.");

    /// <summary>Every Pro Guitar slot EOF has, charted: the list scrolls.</summary>
    public static FakeEofImportScreen Full() =>
        new([.. Enumerable.Range(1, 12).Select(i => $"PART REAL_GUITAR_{i} ({i * 101} notes)")], false, "");

    public IReadOnlyList<IEofTrackOption> AvailableTracks { get; }

    public bool HasNoTracks => AvailableTracks.Count == 0;

    public bool IsBusy { get; }

    public string ErrorMessage { get; }

    public ICommand ImportCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed class Track(string name) : ObservableObject, IEofTrackOption
    {
        private bool _isSelected = true;

        public string Name { get; } = name;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
