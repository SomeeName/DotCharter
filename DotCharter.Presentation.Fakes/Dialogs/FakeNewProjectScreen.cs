using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Session;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="INewProjectScreen" />; Create answers the details shown.</summary>
public sealed class FakeNewProjectScreen : FakeDialog<NewProjectDetails?>, INewProjectScreen
{
    private FakeNewProjectScreen(bool chooser, bool extracting, string artist = "", string title = "",
        string album = "", string year = "", string date = "", string genre = "", string japaneseArtist = "",
        string japaneseTitle = "", bool suggestion = false, byte[]? cover = null)
    {
        IsChooserStage = chooser;
        IsExtracting = extracting;
        IsFormStage = !chooser && !extracting;
        Artist = artist;
        Title = title;
        Album = album;
        AlbumYear = year;
        Genre = genre;
        JapaneseArtist = japaneseArtist;
        JapaneseTitle = japaneseTitle;
        HasNameSuggestion = suggestion;
        CoverArtPng = cover;
        ChooseAudioCommand = new RelayCommand(() => { });
        StartWithoutAudioCommand = new RelayCommand(() => { });
        ApplyNameSuggestionCommand = new RelayCommand(() => { });
        BackCommand = new RelayCommand(() => { });
        CreateCommand = Answer(new NewProjectDetails(artist, title, album, year, date, genre, japaneseArtist,
            japaneseTitle, cover, null));
        CancelCommand = Answer(null);
    }

    public static IReadOnlyList<FakeVariant<INewProjectScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new("Chooser", Chooser),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    /// <summary>The form after an audio pick: Create is the default button there.</summary>
    public static FakeNewProjectScreen Default() => new(false, false, "Polkadot Stingray", "Sakasma", "Ultra Pop", "2019");

    /// <summary>The first stage: pick the audio or start without.</summary>
    public static FakeNewProjectScreen Chooser() => new(chooser: true, extracting: false);

    /// <summary>"Start without audio": the plain, empty form.</summary>
    public static FakeNewProjectScreen Empty() => new(chooser: false, extracting: false);

    /// <summary>Japanese tags with a Latin-name suggestion on offer, and a German genre.</summary>
    public static FakeNewProjectScreen LongText() => new(false, false, "ヨルシカ", "だから僕は音楽を辞めた",
        "エルマ（初回限定盤）", "2019", "2019-04-10", "Alternativer Rock / Japanischer Indie-Pop",
        "ヨルシカ", "だから僕は音楽を辞めた", suggestion: true);

    /// <summary>Every field filled from the tags, with a cover.</summary>
    public static FakeNewProjectScreen Full() => new(false, false, "Polkadot Stingray", "Sakasma", "Ultra Pop",
        "2019", "2019-11-13", "J-Rock", cover: FakeCover.Png);

    /// <summary>Reading the tags off the picked file.</summary>
    public static FakeNewProjectScreen Busy() => new(chooser: false, extracting: true);

    public bool IsChooserStage { get; }
    public bool IsExtracting { get; }
    public bool IsFormStage { get; }
    public string AcceptedAudioFormats { get; } = string.Join(" · ",
        AudioFileTypes.Extensions.Select(e => e.TrimStart('.').ToUpperInvariant()));
    public string Artist { get; set; }
    public string Title { get; set; }
    public string JapaneseArtist { get; set; }
    public string JapaneseTitle { get; set; }
    public string Album { get; set; }
    public string AlbumYear { get; set; }
    public string Genre { get; set; }
    public byte[]? CoverArtPng { get; }
    public bool HasCoverArt => CoverArtPng != null;
    public bool HasNameSuggestion { get; }
    public ICommand ChooseAudioCommand { get; }
    public ICommand StartWithoutAudioCommand { get; }
    public ICommand ApplyNameSuggestionCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }
}

/// <summary>A 16×16 gradient PNG standing in for embedded cover art.</summary>
internal static class FakeCover
{
    public static byte[] Png { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAACf0lEQVR42gXBIautMAAA4P2cl1666bBwLCueIAcGsqBlRcMQBFnQsjKDGMQgBjGIQQxikBPGCXKC3CA3yA3ygmm/430f+AOzv0b2hbKbmd2tDOHsQbKnm9k0c7yMsswPs4BnUZLFIhMyA39h8WUUN1TczQJZxQMXT1LYbuHQgnqFz4ogLCJexEkhRJHKAnzB6mZUd1Qhs3pY1RNXNqkct6K08r0qYFUUVjGvRFKlosplBW6wuRsNQs3DbJ5WY+PGIQ11G582gddErInDRvAmTZpcNKVswB12yOgeqHuanW11Du4o6Xy3C2gXeV3MOhF2Ke/ypCtFV8sOIDg8jOGJBtscHGugePDJELhDRIfYGwQb0nDI+VAmQy2GVg7gAaenMdlocsyJWpOPp4BMkTvFdBLelLIpD6eST3UytWLq5QSecLGNxUELNRffWgK8RGSJ3UXQJfWWnC1luNR8aZOlF8soF2BD5RiKIuWbKrBUhFVMlHBVSlXuqZKpOlQtV32iRqFmqYADV2qsPloDc42sNcarIGvqrjldS2+t2dqGa8/XMVlnsb7kCijcfGML0BaZW2xtAm8p2XJ3K+lWe1vLtj7cRr7NyfYS21tuwId7YOwR2mNzF9ae4j0ne+nuNd1bb+/ZPob7zPdXsr/F/pE7COARGUeMDmEeqXXk+CjJUbtHS4/eO0Z2zOHx4sc7OT7i+JYHiOAZG6dAZ2qeuXWW+KzJ2bpnT8/RO2d2vsLzzc9Pcn6L80eeIIaXMK4UXbl5ldZV46slV+9eI71m73qx6x1eH359J9ePuH7lBQTUqaFzpEtT15Zuse6JHl09U/3y9JvpT6i/uf5J9K/Q/6T+DwXMnJDpc+D7AAAAAElFTkSuQmCC");
}
