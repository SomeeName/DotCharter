using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ISongMetadataScreen" />; Save answers true, Cancel false.</summary>
public sealed partial class FakeSongMetadataScreen : FakeDialog<bool>, ISongMetadataScreen
{
    [ObservableProperty] private string _artist;
    [ObservableProperty] private string _songName;
    [ObservableProperty] private string _japaneseArtist;
    [ObservableProperty] private string _japaneseTitle;
    [ObservableProperty] private string _albumName;
    [ObservableProperty] private string _albumYear;
    [ObservableProperty] private string _releaseDate;
    [ObservableProperty] private string _charter;
    [ObservableProperty] private string _genre;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyAccidentalsLabel), nameof(KeySignatureLabel))]
    private int _keyAccidentals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyIsMajor), nameof(KeySignatureLabel))]
    private bool _keyIsMinor;

    private FakeSongMetadataScreen(string[] fields, IReadOnlyList<string> genres, int accidentals, bool minor)
    {
        (_artist, _songName, _japaneseArtist, _japaneseTitle, _albumName) =
            (fields[0], fields[1], fields[2], fields[3], fields[4]);
        (_albumYear, _releaseDate, _charter, _genre) = (fields[5], fields[6], fields[7], fields[8]);
        _keyAccidentals = accidentals;
        _keyIsMinor = minor;
        GenreSuggestions = genres;
        PickGenreCommand = new RelayCommand<string>(g => Genre = g ?? Genre);
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<ISongMetadataScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>A charted song with two genre picks and a minor key.</summary>
    public static FakeSongMetadataScreen Default() => new(
        ["Muse", "Hysteria", "", "", "Absolution", "2003", "2003-12-01", "alex", "Rock"],
        ["Rock", "Metal"], 3, true);

    /// <summary>A new project: every field blank, no genres authored yet, C major.</summary>
    public static FakeSongMetadataScreen Empty() => new(["", "", "", "", "", "", "", "", ""], [], 0, false);

    public static FakeSongMetadataScreen LongText() => new(
        ["Die Toten Hosen und das Orchester der Deutschen Oper Berlin", "Tage wie diese (Live im Olympiastadion, erweiterte Fassung)",
            "ラルク・アン・シエル", "ハニー（リマスター・スペシャル・エディション）",
            "Ballast der Republik — Jubiläumsausgabe mit allen Bonustiteln", "2012", "2012-05-11",
            "Charterin mit einem sehr langen Namen", "Progressiver Stadion-Rock"],
        ["Progressiver Stadion-Rock", "ビジュアル系", "Neue Deutsche Härte"], -6, false);

    /// <summary>Every field set, five genre picks, the widest key.</summary>
    public static FakeSongMetadataScreen Full() => new(
        ["Dream Theater", "Metropolis Pt. 1", "", "", "Images and Words", "1992", "1992-07-07", "alex", "Progressive metal"],
        ["Progressive metal", "Rock", "Metal", "Djent", "Post-rock"], 7, false);

    public IReadOnlyList<string> GenreSuggestions { get; }

    public bool HasGenreSuggestions => GenreSuggestions.Count > 0;

    public ICommand PickGenreCommand { get; }

    public bool KeyIsMajor
    {
        get => !KeyIsMinor;
        set => KeyIsMinor = !value;
    }

    public string KeyAccidentalsLabel =>
        KeyAccidentals == 0 ? "no accidentals" : $"{Math.Abs(KeyAccidentals)} {(KeyAccidentals > 0 ? "♯" : "♭")}";

    public string KeySignatureLabel =>
        $"{SongMetadataViewModel.KeyName(KeyAccidentals, KeyIsMinor)} {(KeyIsMinor ? "minor" : "major")}";

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }
}
