using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Online.MusicBrainz;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Services.Session;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The stages of the audio-first New project flow.</summary>
public enum NewProjectStage
{
    Chooser,
    Extracting,
    Form
}

/// <summary>
///     File ▸ New project. One instance per open: the audio pick and its tag read happen
///     here; Create answers <see cref="NewProjectDetails" /> and
///     <see cref="NewProjectCreator" /> builds the project after the dialog closes.
/// </summary>
public sealed class NewProjectViewModel : DialogViewModel<NewProjectDetails?>, INewProjectScreen
{
    private readonly IFileStorageService _storage;
    private readonly IConfigurationService _config;
    private readonly INewProjectSources _sources;
    private readonly ProjectState _state;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IUiDispatcher _ui;

    private NewProjectStage _stage = NewProjectStage.Chooser;
    private string? _audioFilePath;
    private byte[]? _coverArtPng;
    private string _artist = "";
    private string _title = "";
    private string _japaneseArtist = "";
    private string _japaneseTitle = "";
    private string _album = "";
    private string _albumYear = "";
    private string _releaseDate = "";
    private string _genre = "";
    private string _suggestedArtist = "";
    private string _suggestedTitle = "";
    private string _suggestedAlbum = "";

    public NewProjectViewModel(IFileStorageService storage, IConfigurationService config, INewProjectSources sources,
        ProjectState state, ILocalizationService localization, IAppLogger logger, IUiDispatcher ui)
    {
        _storage = storage;
        _config = config;
        _sources = sources;
        _state = state;
        _localization = localization;
        _logger = logger;
        _ui = ui;
        ChooseAudioCommand = new AsyncRelayCommand(ChooseAudioAsync);
        StartWithoutAudioCommand = new RelayCommand(StartWithoutAudio);
        ApplyNameSuggestionCommand = new RelayCommand(ApplyNameSuggestion);
        BackCommand = new RelayCommand(() => Stage = NewProjectStage.Chooser);
        CreateCommand = new RelayCommand(Create);
        CancelCommand = new RelayCommand(() =>
        {
            _logger.Info(AppLogCategory.Project, "New project dialog canceled.");
            Close(null);
        });
        logger.Info(AppLogCategory.Project, "New project dialog opened.");
    }

    public NewProjectStage Stage
    {
        get => _stage;
        private set
        {
            if (!SetProperty(ref _stage, value)) return;
            OnPropertyChanged(nameof(IsChooserStage));
            OnPropertyChanged(nameof(IsExtracting));
            OnPropertyChanged(nameof(IsFormStage));
        }
    }

    public bool IsChooserStage => Stage == NewProjectStage.Chooser;
    public bool IsExtracting => Stage == NewProjectStage.Extracting;
    public bool IsFormStage => Stage == NewProjectStage.Form;

    public string AcceptedAudioFormats { get; } = string.Join(" · ",
        AudioFileTypes.Extensions.Select(extension => extension.TrimStart('.').ToUpperInvariant()));

    public string Artist { get => _artist; set => SetProperty(ref _artist, value); }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string JapaneseArtist { get => _japaneseArtist; set => SetProperty(ref _japaneseArtist, value); }
    public string JapaneseTitle { get => _japaneseTitle; set => SetProperty(ref _japaneseTitle, value); }
    public string Album { get => _album; set => SetProperty(ref _album, value); }
    public string AlbumYear { get => _albumYear; set => SetProperty(ref _albumYear, value); }
    public string Genre { get => _genre; set => SetProperty(ref _genre, value); }

    public byte[]? CoverArtPng
    {
        get => _coverArtPng;
        private set
        {
            if (SetProperty(ref _coverArtPng, value is { Length: > 0 } ? value : null))
                OnPropertyChanged(nameof(HasCoverArt));
        }
    }

    public bool HasCoverArt => CoverArtPng != null;

    public bool HasNameSuggestion =>
        SuggestedArtist.Length > 0 || SuggestedTitle.Length > 0 || SuggestedAlbum.Length > 0;

    internal string SuggestedArtist { get => _suggestedArtist; set => SetSuggestion(ref _suggestedArtist, value); }
    internal string SuggestedTitle { get => _suggestedTitle; set => SetSuggestion(ref _suggestedTitle, value); }
    internal string SuggestedAlbum { get => _suggestedAlbum; set => SetSuggestion(ref _suggestedAlbum, value); }

    public ICommand ChooseAudioCommand { get; }
    public ICommand StartWithoutAudioCommand { get; }
    public ICommand ApplyNameSuggestionCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>Chooser → form: pick, read the tags off the UI thread, prefill. A failed read still
    ///     advances, falling back to the file name.</summary>
    private async Task ChooseAudioAsync()
    {
        if (IsExtracting) return;

        var path = await _storage.OpenFilePickerAsync(_localization["Picker_SelectAudioFile"],
            [new FilePickerFilter(_localization["Picker_AudioFiles"], AudioFileTypes.PickerPatterns), FilePickerFilters.AllFiles],
            _config.LoadAppConfig().LastAudioDir);
        if (string.IsNullOrEmpty(path))
        {
            _logger.Info(AppLogCategory.Project, "New project audio selection canceled at chooser.");
            return;
        }

        _config.RememberDirectory(path, (c, d) => c.LastAudioDir = d);
        _audioFilePath = path;
        Stage = NewProjectStage.Extracting;

        AudioMetadataResult tags;
        try
        {
            var sources = _sources;
            tags = await Task.Run(() => sources.ReadTags(path));
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project,
                $"Audio metadata extraction failed for '{path}'. Falling back to filename heuristics. {ex.Message}");
            tags = AudioMetadataResult.Empty;
        }

        ApplyTags(path, tags);
        RequestLatinNameSuggestion();
        Stage = NewProjectStage.Form;
        _logger.Info(AppLogCategory.Project,
            $"New project audio chosen. audio='{path}', artist='{Artist}', title='{Title}', hasCover={HasCoverArt}.");
    }

    /// <summary>The empty-project form, cleared — including the previous song's original-script
    ///     names and suggestion, which would otherwise ship in this project's manifest.</summary>
    private void StartWithoutAudio()
    {
        Artist = Title = Album = AlbumYear = Genre = _releaseDate = "";
        JapaneseArtist = JapaneseTitle = "";
        SuggestedArtist = SuggestedTitle = SuggestedAlbum = "";
        _audioFilePath = null;
        CoverArtPng = null;
        Stage = NewProjectStage.Form;
        _logger.Info(AppLogCategory.Project, "New project: starting without audio.");
    }

    private void Create()
    {
        if (string.IsNullOrWhiteSpace(Artist) || string.IsNullOrWhiteSpace(Title))
        {
            _logger.Warning(AppLogCategory.Project, "New project creation rejected because artist or title is empty.");
            _state.SetMessage(_localization["Status_ArtistTitleRequired"]);
            return;
        }

        Close(new NewProjectDetails(Artist, Title, Album, AlbumYear, _releaseDate, Genre, JapaneseArtist, JapaneseTitle,
            CoverArtPng, _audioFilePath));
    }

    /// <summary>Takes the suggestion, moving the original names into the Japanese fields rather than
    ///     discarding them. The album has no such field in the manifest, so it is replaced.</summary>
    private void ApplyNameSuggestion()
    {
        if (SuggestedArtist.Length > 0)
        {
            if (ScriptDetection.NeedsRomanization(Artist)) JapaneseArtist = Artist.Trim();
            Artist = SuggestedArtist;
        }

        if (SuggestedTitle.Length > 0)
        {
            if (ScriptDetection.NeedsRomanization(Title)) JapaneseTitle = Title.Trim();
            Title = SuggestedTitle;
        }

        if (SuggestedAlbum.Length > 0) Album = SuggestedAlbum;
        _logger.Info(AppLogCategory.Project,
            $"Applied online name suggestion. artist='{Artist}', title='{Title}', album='{Album}', " +
            $"originalArtist='{JapaneseArtist}', originalTitle='{JapaneseTitle}'.");
        SuggestedArtist = SuggestedTitle = SuggestedAlbum = "";
    }

    private void ApplyTags(string path, AudioMetadataResult tags)
    {
        var (fileArtist, fileTitle) = NewProjectCreator.ParseArtistTitleFromFileName(path);
        Artist = !string.IsNullOrWhiteSpace(tags.Artist) ? tags.Artist : fileArtist;
        Title = !string.IsNullOrWhiteSpace(tags.Title) ? tags.Title : fileTitle;
        Album = tags.Album;
        AlbumYear = tags.AlbumYear;
        // Not shown: no export reads it. It rides into the project as the
        // file tagged it, and the Year above is already distilled from it.
        _releaseDate = tags.ReleaseDate;
        Genre = tags.Genre;
        CoverArtPng = tags.CoverArtPng;
        // Tags already in their own script ARE the original names.
        JapaneseArtist = ScriptDetection.NeedsRomanization(Artist) ? Artist.Trim() : "";
        JapaneseTitle = ScriptDetection.NeedsRomanization(Title) ? Title.Trim() : "";
        SuggestedArtist = SuggestedTitle = SuggestedAlbum = "";
    }

    /// <summary>
    ///     Fire-and-forget: a convenience may never delay the dialog or throw. EVERY exit logs at Info,
    ///     because a lookup that silently did nothing was indistinguishable from an unwired one.
    /// </summary>
    private void RequestLatinNameSuggestion()
    {
        if (!ScriptDetection.NeedsRomanization(Artist) && !ScriptDetection.NeedsRomanization(Title) &&
            !ScriptDetection.NeedsRomanization(Album))
        {
            _logger.Info(AppLogCategory.Project, "Online name lookup skipped: artist, title and album are already Latin.");
            return;
        }

        if (!_config.LoadAppConfig().OnlineLatinNameLookup)
        {
            _logger.Info(AppLogCategory.Project, "Online name lookup skipped: turned off in settings.");
            return;
        }

        var (artist, title, album) = (Artist, Title, Album);
        var started = Stopwatch.GetTimestamp();
        _logger.Info(AppLogCategory.Project, $"Online name lookup started for artist='{artist}', title='{title}', album='{album}'.");
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _sources.FindLatinNamesAsync(artist, title, album, partial => _ui.Post(() =>
                {
                    if (!StillShowing(artist, title, album) || HasNameSuggestion) return;
                    SuggestedArtist = partial.Artist;
                    _logger.Info(AppLogCategory.Project, $"Online name suggestion (artist only, {Elapsed(started)} ms): '{partial.Artist}'.");
                })).ConfigureAwait(false);
                if (!result.Ok)
                {
                    _logger.Info(AppLogCategory.Project,
                        $"Online name lookup found nothing after {Elapsed(started)} ms ({result.Error}): {result.Message}");
                    return;
                }

                await _ui.InvokeAsync(() =>
                {
                    // Only for the song still on screen: the form may have changed in flight.
                    if (!StillShowing(artist, title, album))
                    {
                        _logger.Info(AppLogCategory.Project, "Online name lookup discarded: the form changed while it was in flight.");
                        return;
                    }

                    SuggestedArtist = result.Value!.Artist;
                    SuggestedTitle = result.Value.Title;
                    SuggestedAlbum = result.Value.Album;
                    _logger.Info(AppLogCategory.Project,
                        $"Online name suggestion after {Elapsed(started)} ms: artist='{SuggestedArtist}', title='{SuggestedTitle}', album='{SuggestedAlbum}'.");
                });
            }
            catch (Exception ex)
            {
                _logger.Warning(AppLogCategory.Project, $"Online name lookup failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private bool StillShowing(string artist, string title, string album) =>
        !IsClosed && string.Equals(artist, Artist, StringComparison.Ordinal) &&
        string.Equals(title, Title, StringComparison.Ordinal) && string.Equals(album, Album, StringComparison.Ordinal);

    private static long Elapsed(long startTimestamp) => (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private void SetSuggestion(ref string field, string? value)
    {
        if (SetProperty(ref field, value?.Trim() ?? "")) OnPropertyChanged(nameof(HasNameSuggestion));
    }
}
