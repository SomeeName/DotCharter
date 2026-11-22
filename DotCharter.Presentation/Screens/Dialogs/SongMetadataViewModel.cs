using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Audio.Key;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Services.Online.MusicBrainz;
using DotCharter.Services.Session;
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
    private readonly INewProjectSources _sources;

    // What a build would derive from the names now; a field still equal to it follows the names.
    private string _artistSortDerived = "";
    private string _titleSortDerived = "";
    private string _albumSortDerived = "";
    private string _dlcKeyDerived = "";

    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _songName = "";
    [ObservableProperty] private string _japaneseArtist = "";
    [ObservableProperty] private string _japaneseTitle = "";
    [ObservableProperty] private string _albumName = "";
    [ObservableProperty] private string _albumYear = "";
    [ObservableProperty] private string _charter = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private string _artistSort = "";
    [ObservableProperty] private string _titleSort = "";
    [ObservableProperty] private string _albumSort = "";
    [ObservableProperty] private string _dlcKey = "";
    [ObservableProperty] private string _version = "1";
    [ObservableProperty] private bool _showJapaneseFields;
    [ObservableProperty] private bool _showSortFields;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeySignatureLabel), nameof(KeyAccidentalsLabel))]
    private int _keyAccidentals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyIsMajor), nameof(KeySignatureLabel))]
    private bool _keyIsMinor;

    [ObservableProperty] private bool _keyLocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyEstimate))]
    [NotifyCanExecuteChangedFor(nameof(UseKeyEstimateCommand))]
    private SongKeyEstimate? _keyEstimate;

    [ObservableProperty] private string _keyEstimateLabel = "";

    public SongMetadataViewModel(ProjectState state, IHistoryService history, ILocalizationService localization,
        IAppLogger logger, INewProjectSources sources, ISongKeyAudioEstimator keyAudio,
        IChartGenreIndex? genreIndex = null)
    {
        _sources = sources;
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
            _keyLocked = chart.KeyLocked;

            _artistSortDerived = SongMetadataDefaults.ArtistSort(_artist);
            _titleSortDerived = SongMetadataDefaults.TitleSort(_songName);
            _albumSortDerived = SongMetadataDefaults.AlbumSort(_albumName);
            _dlcKeyDerived = SongMetadataDefaults.DlcKey(_artist, _songName);
            // A sort name that only mirrors the artist was never authored.
            var authoredArtistSort = string.Equals(chart.ArtistSort, chart.Artist, StringComparison.Ordinal) ? "" : chart.ArtistSort;
            _artistSort = SongNamesFollow.Seed(authoredArtistSort, _artistSortDerived);
            _titleSort = SongNamesFollow.Seed(chart.TitleSort, _titleSortDerived);
            _albumSort = SongNamesFollow.Seed(chart.AlbumSort, _albumSortDerived);
            _dlcKey = SongNamesFollow.Seed(chart.DlcKey, _dlcKeyDerived);
            _version = string.IsNullOrWhiteSpace(chart.Version) ? "1" : chart.Version.Trim();
            _showJapaneseFields = _japaneseArtist.Length > 0 || _japaneseTitle.Length > 0;
            _showSortFields = authoredArtistSort.Length > 0 || chart.TitleSort.Length > 0 || chart.AlbumSort.Length > 0;
        }

        MoveToOriginalScriptCommand = new AsyncRelayCommand(MoveToOriginalScriptAsync);

        // Read on every open: the library scan warms the index long after start-up.
        GenreSuggestions = genreIndex?.KnownGenres(MaxGenreSuggestions).ToList() ?? [];
        PickGenreCommand = new RelayCommand<string>(PickGenre);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
        UseKeyEstimateCommand = new RelayCommand(UseKeyEstimate, () => KeyEstimate.HasValue);
        KeyEstimation = EstimateKeyAsync(keyAudio);
        logger.Info(AppLogCategory.Project, $"Song metadata dialog opened. Song='{_songName}'.");
    }

    public IReadOnlyList<string> GenreSuggestions { get; }

    public string ToolVersion => SongMetadataDefaults.ToolVersion;

    public bool HasOriginalScriptOffer =>
        ScriptDetection.NeedsRomanization(Artist) || ScriptDetection.NeedsRomanization(SongName);

    public ICommand MoveToOriginalScriptCommand { get; }

    partial void OnArtistChanged(string value)
    {
        ArtistSort = SongNamesFollow.Next(ArtistSort, ref _artistSortDerived, SongMetadataDefaults.ArtistSort(value));
        DlcKey = SongNamesFollow.Next(DlcKey, ref _dlcKeyDerived, SongMetadataDefaults.DlcKey(value, SongName));
        OnPropertyChanged(nameof(HasOriginalScriptOffer));
    }

    partial void OnSongNameChanged(string value)
    {
        TitleSort = SongNamesFollow.Next(TitleSort, ref _titleSortDerived, SongMetadataDefaults.TitleSort(value));
        DlcKey = SongNamesFollow.Next(DlcKey, ref _dlcKeyDerived, SongMetadataDefaults.DlcKey(Artist, value));
        OnPropertyChanged(nameof(HasOriginalScriptOffer));
    }

    partial void OnAlbumNameChanged(string value) =>
        AlbumSort = SongNamesFollow.Next(AlbumSort, ref _albumSortDerived, SongMetadataDefaults.AlbumSort(value));

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

    public bool HasKeyEstimate => KeyEstimate.HasValue;

    /// <summary>Adopts the estimated key as the song's LOCKED key.</summary>
    public IRelayCommand UseKeyEstimateCommand { get; }

    ICommand ISongMetadataScreen.UseKeyEstimateCommand => UseKeyEstimateCommand;

    /// <summary>Completes once the estimate (notes now, or the audio later) is shown; tests await it.</summary>
    public Task KeyEstimation { get; }

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

    /// <summary>
    ///     The key read from the notes of every arrangement; with no notes, from the
    ///     song's audio, off the UI thread. Neither: "unknown".
    /// </summary>
    private async Task EstimateKeyAsync(ISongKeyAudioEstimator keyAudio)
    {
        var project = _state.ActiveProject;
        var fromNotes = project != null ? SongKeyEstimator.FromArrangements(project.Arrangements) : null;
        if (fromNotes.HasValue || string.IsNullOrWhiteSpace(project?.AudioFileName))
        {
            ShowEstimate(fromNotes, fromAudio: false);
            return;
        }

        KeyEstimateLabel = _localization["Dialog_SongMetadata_KeyEstimateRunning"];
        SongKeyEstimate? fromAudio = null;
        try
        {
            var projectPath = _state.CurrentProjectPath;
            var audioFileName = project.AudioFileName;
            fromAudio = await Task.Run(
                () => keyAudio.EstimateAsync(ProjectAudioLocator.ResolveOrDefault(projectPath, audioFileName), CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project, "Reading the key from the audio failed", ex);
        }

        ShowEstimate(fromAudio, fromAudio: true);
    }

    private void ShowEstimate(SongKeyEstimate? estimate, bool fromAudio) =>
        (KeyEstimate, KeyEstimateLabel) = (estimate, EstimateText(_localization, estimate, fromAudio));

    /// <summary>The estimate line of the dialog: key, runner-up, confidence and the close-call hint; "unknown" for none.</summary>
    public static string EstimateText(ILocalizationService localization, SongKeyEstimate? estimate, bool fromAudio)
    {
        if (estimate is not { } e) return localization["Dialog_SongMetadata_KeyEstimateUnknown"];
        var text = localization.Format(
            fromAudio ? "Dialog_SongMetadata_KeyEstimateAudioFmt" : "Dialog_SongMetadata_KeyEstimateNotesFmt",
            KeyLabel(localization, e.Best), KeyLabel(localization, e.RunnerUp), (int)Math.Round(e.Confidence * 100));
        return e.IsClose ? text + " " + localization["Dialog_SongMetadata_KeyEstimateClose"] : text;
    }

    private static string KeyLabel(ILocalizationService localization, ChordKeySignature key) => localization.Format(
        key.IsMinor ? "Inspector_Project_KeyMinorFmt" : "Inspector_Project_KeyMajorFmt",
        KeyName(key.Accidentals, key.IsMinor));

    private void UseKeyEstimate()
    {
        if (KeyEstimate is not { } e) return;
        KeyAccidentals = e.Best.Accidentals;
        KeyIsMinor = e.Best.IsMinor;
        KeyLocked = true;
    }

    /// <summary>Moves a non-Latin Artist / Title to the Japanese fields, filling the main ones from MusicBrainz's Latin names.</summary>
    private async Task MoveToOriginalScriptAsync()
    {
        var (artist, title, album) = (Artist, SongName, AlbumName);
        var latinArtist = "";
        var latinTitle = "";
        try
        {
            var found = await _sources.FindLatinNamesAsync(artist, title, album, _ => { });
            if (found.Ok && !IsClosed && artist == Artist && title == SongName)
            {
                latinArtist = found.Value!.Artist;
                latinTitle = found.Value.Title;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project, "Online name lookup failed", ex);
        }

        (Artist, JapaneseArtist) = SongMetadataDefaults.MoveToOriginalScript(Artist, JapaneseArtist, latinArtist);
        (SongName, JapaneseTitle) = SongMetadataDefaults.MoveToOriginalScript(SongName, JapaneseTitle, latinTitle);
        ShowJapaneseFields = true;
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
            // An artist sort left at its derived value is not authored, so only a sort name that merely MIRRORED
            // the artist follows a rename; any other authored sort is kept as typed.
            ArtistSort = !string.Equals(ArtistSort, _artistSortDerived, StringComparison.Ordinal)
                ? ArtistSort.Trim()
                : string.Equals(before.ArtistSort, before.Artist, StringComparison.Ordinal)
                    ? Artist
                    : before.ArtistSort,
            TitleSort = SongMetadataDefaults.Authored(TitleSort, _titleSortDerived),
            AlbumSort = SongMetadataDefaults.Authored(AlbumSort, _albumSortDerived),
            DlcKey = SongMetadataDefaults.Authored(DlcKey, _dlcKeyDerived),
            Version = string.IsNullOrWhiteSpace(Version) ? "1" : Version.Trim(),
            SongName = SongName,
            JapaneseArtist = JapaneseArtist,
            JapaneseTitle = JapaneseTitle,
            AlbumName = AlbumName,
            AlbumYear = AlbumYear,
            Charter = Charter,
            Genre = Genre,
            KeyAccidentals = Math.Clamp(KeyAccidentals, -7, 7),
            KeyIsMinor = KeyIsMinor,
            KeyLocked = KeyLocked
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
