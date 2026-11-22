using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing;
using DotCharter.Services.Online.MusicBrainz;
using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The DLC Builder fields of the form: sort names, DLC key, version, author, and the toggles that show them.</summary>
public sealed partial class NewProjectViewModel
{
    private string _artistSort = "";
    private string _titleSort = "";
    private string _albumSort = "";
    private string _dlcKey = "";
    private string _version = "1";
    private string _charter = "";
    private bool _showJapaneseFields;
    private bool _showSortFields;
    private string _artistSortDerived = "";
    private string _titleSortDerived = "";
    private string _albumSortDerived = "";
    private string _dlcKeyDerived = "";
    private ICommand? _moveToOriginalScript;

    public bool ShowJapaneseFields { get => _showJapaneseFields; set => SetProperty(ref _showJapaneseFields, value); }
    public bool ShowSortFields { get => _showSortFields; set => SetProperty(ref _showSortFields, value); }
    public string ArtistSort { get => _artistSort; set => SetProperty(ref _artistSort, value); }
    public string TitleSort { get => _titleSort; set => SetProperty(ref _titleSort, value); }
    public string AlbumSort { get => _albumSort; set => SetProperty(ref _albumSort, value); }
    public string DlcKey { get => _dlcKey; set => SetProperty(ref _dlcKey, value); }
    public string Version { get => _version; set => SetProperty(ref _version, value); }
    public string Charter { get => _charter; set => SetProperty(ref _charter, value); }

    public string ToolVersion => SongMetadataDefaults.ToolVersion;

    public bool HasOriginalScriptOffer =>
        !HasNameSuggestion && (ScriptDetection.NeedsRomanization(Artist) || ScriptDetection.NeedsRomanization(Title));

    public ICommand MoveToOriginalScriptCommand => _moveToOriginalScript ??= new RelayCommand(MoveToOriginalScript);

    /// <summary>Re-derives what follows the names (sort names, DLC key) and the offer; called when a name changes.</summary>
    private void NamesChanged()
    {
        ArtistSort = SongNamesFollow.Next(ArtistSort, ref _artistSortDerived, SongMetadataDefaults.ArtistSort(Artist));
        TitleSort = SongNamesFollow.Next(TitleSort, ref _titleSortDerived, SongMetadataDefaults.TitleSort(Title));
        AlbumSort = SongNamesFollow.Next(AlbumSort, ref _albumSortDerived, SongMetadataDefaults.AlbumSort(Album));
        DlcKey = SongNamesFollow.Next(DlcKey, ref _dlcKeyDerived, SongMetadataDefaults.DlcKey(Artist, Title));
        OnPropertyChanged(nameof(HasOriginalScriptOffer));
    }

    private void MoveToOriginalScript()
    {
        (Artist, JapaneseArtist) = SongMetadataDefaults.MoveToOriginalScript(Artist, JapaneseArtist, "");
        (Title, JapaneseTitle) = SongMetadataDefaults.MoveToOriginalScript(Title, JapaneseTitle, "");
        ShowJapaneseFields = true;
        _logger.Info(AppLogCategory.Project,
            $"Moved the non-Latin names to the Japanese fields. originalArtist='{JapaneseArtist}', originalTitle='{JapaneseTitle}'.");
    }

    /// <summary>Back to a blank form for a project without audio; the author stays the default charter.</summary>
    private void ResetNames()
    {
        Artist = Title = Album = "";
        JapaneseArtist = JapaneseTitle = "";
        ArtistSort = TitleSort = AlbumSort = DlcKey = "";
        Version = "1";
        ShowJapaneseFields = ShowSortFields = false;
    }

    /// <summary>The fields a derived value fills stay blank in the project while they equal it.</summary>
    private NewProjectDetails Details() => new(Artist, Title, Album, AlbumYear, _releaseDate, Genre, JapaneseArtist, JapaneseTitle,
        CoverArtPng, _audioFilePath,
        ArtistSort: SongMetadataDefaults.Authored(ArtistSort, _artistSortDerived),
        TitleSort: SongMetadataDefaults.Authored(TitleSort, _titleSortDerived),
        AlbumSort: SongMetadataDefaults.Authored(AlbumSort, _albumSortDerived),
        DlcKey: SongMetadataDefaults.Authored(DlcKey, _dlcKeyDerived),
        Version: Version, Charter: Charter);
}
