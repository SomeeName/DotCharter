using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Song metadata. One instance per open, seeded from the chart; Save
///     writes the chart back through one <see cref="SetSongMetadataAction" />.
/// </summary>
public sealed partial class SongMetadataViewModel : DialogViewModel<bool>, ISongMetadataScreen
{
    /// <summary>How many quick-picks fit beside the field before the strip wraps.</summary>
    private const int MaxGenreSuggestions = 5;

    // The circle of fifths both ways round: one accidental count spells a
    // different tonic in major and in minor.
    private static readonly string[] MajorSharp = ["C", "G", "D", "A", "E", "B", "F#", "C#"];
    private static readonly string[] MajorFlat = ["C", "F", "Bb", "Eb", "Ab", "Db", "Gb", "Cb"];
    private static readonly string[] MinorSharp = ["A", "E", "B", "F#", "C#", "G#", "D#", "A#"];
    private static readonly string[] MinorFlat = ["A", "D", "G", "C", "F", "Bb", "Eb", "Ab"];

    private readonly ProjectState _state;
    private readonly IHistoryService _history;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;

    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _songName = "";
    [ObservableProperty] private string _japaneseArtist = "";
    [ObservableProperty] private string _japaneseTitle = "";
    [ObservableProperty] private string _albumName = "";
    [ObservableProperty] private string _albumYear = "";
    [ObservableProperty] private string _charter = "";
    [ObservableProperty] private string _genre = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeySignatureLabel), nameof(KeyAccidentalsLabel))]
    private int _keyAccidentals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyIsMajor), nameof(KeySignatureLabel))]
    private bool _keyIsMinor;

    public SongMetadataViewModel(ProjectState state, IHistoryService history, ILocalizationService localization,
        IAppLogger logger, IChartGenreIndex? genreIndex = null)
    {
        _state = state;
        _history = history;
        _localization = localization;
        _logger = logger;

        if (state.ActiveProject?.Chart is { } chart)
        {
            _artist = chart.Artist.Trim();
            _songName = chart.SongName.Trim();
            _japaneseArtist = chart.JapaneseArtist.Trim();
            _japaneseTitle = chart.JapaneseTitle.Trim();
            _albumName = chart.AlbumName.Trim();
            _albumYear = chart.AlbumYear.Trim();
            _charter = chart.Charter;
            _genre = chart.Genre;
            _keyAccidentals = chart.KeyAccidentals;
            _keyIsMinor = chart.KeyIsMinor;
        }

        // Read on every open: the library scan warms the index long after start-up.
        GenreSuggestions = genreIndex?.KnownGenres(MaxGenreSuggestions).ToList() ?? [];
        PickGenreCommand = new RelayCommand<string>(PickGenre);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
        logger.Info(AppLogCategory.Project, $"Song metadata dialog opened. Song='{_songName}'.");
    }

    public IReadOnlyList<string> GenreSuggestions { get; }

    public bool HasGenreSuggestions => GenreSuggestions.Count > 0;

    public ICommand PickGenreCommand { get; }

    public bool KeyIsMajor
    {
        get => !KeyIsMinor;
        set
        {
            if (value == !KeyIsMinor) return;
            KeyIsMinor = !value;
        }
    }

    public string KeySignatureLabel => _localization.Format(
        KeyIsMinor ? "Inspector_Project_KeyMinorFmt" : "Inspector_Project_KeyMajorFmt",
        KeyName(KeyAccidentals, KeyIsMinor));

    public string KeyAccidentalsLabel
    {
        get
        {
            var count = Math.Clamp(Math.Abs(KeyAccidentals), 0, 7);
            if (count == 0) return _localization["Inspector_Project_KeyNatural"];
            return _localization.Format(
                KeyAccidentals > 0 ? "Inspector_Project_KeySharpsFmt" : "Inspector_Project_KeyFlatsFmt", count);
        }
    }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>The tonic a signature spells — a COUNT of accidentals names a different key in major and in minor.</summary>
    public static string KeyName(int accidentals, bool minor)
    {
        var index = Math.Clamp(Math.Abs(accidentals), 0, 7);
        var sharp = accidentals >= 0;
        var names = minor ? sharp ? MinorSharp : MinorFlat : sharp ? MajorSharp : MajorFlat;
        return names[index];
    }

    private void PickGenre(string? genre)
    {
        if (!string.IsNullOrWhiteSpace(genre)) Genre = genre.Trim();
    }

    private void Save()
    {
        if (_state.ActiveProject?.Chart is not { } chart)
        {
            Close(false);
            return;
        }

        var before = SongMetadataFields.From(chart);
        var after = before with
        {
            Artist = Artist,
            // No ArtistSort field here, so it is never authored — except a sort
            // name that only MIRRORED the artist, which follows a rename.
            ArtistSort = string.Equals(before.ArtistSort, before.Artist, StringComparison.Ordinal)
                ? Artist
                : before.ArtistSort,
            SongName = SongName,
            JapaneseArtist = JapaneseArtist,
            JapaneseTitle = JapaneseTitle,
            AlbumName = AlbumName,
            AlbumYear = AlbumYear,
            Charter = Charter,
            Genre = Genre,
            KeyAccidentals = Math.Clamp(KeyAccidentals, -7, 7),
            KeyIsMinor = KeyIsMinor
        };
        var applied = _history.Do(new SetSongMetadataAction(chart, before, after));
        if (applied)
        {
            _logger.Info(AppLogCategory.Project,
                $"Song metadata saved. Song='{SongName}', Artist='{Artist}', Album='{AlbumName}', Key={KeySignatureLabel}.");
        }

        Close(true);
    }
}
