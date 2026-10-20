using DotCharter.Services.Library.Index;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     The cover Home shows for a play-stats key: a library song's from the
///     index, an own chart's from its project (#815).
/// </summary>
public sealed class SongLibraryLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-cover-" + Guid.NewGuid().ToString("N"));

    public SongLibraryLookupTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void AProject_FindsTheCoverBesideIt()
    {
        var project = WriteProject("a", "<title>Song</title><artistName>Band</artistName>");
        var cover = Touch(Path.Combine(_root, "a", "cover.png"));

        Assert.Equal(cover, new SongLibraryLookup(new StubIndex()).FindCover(project));
    }

    [Fact]
    public void AProject_PrefersItsDeclaredCoverOverCoverPng()
    {
        var project = WriteProject("a", "<title>Song</title><dcCoverArt>art.jpg</dcCoverArt>");
        Touch(Path.Combine(_root, "a", "cover.png"));
        var declared = Touch(Path.Combine(_root, "a", "art.jpg"));

        Assert.Equal(declared, new SongLibraryLookup(new StubIndex()).FindCover(project));
    }

    [Fact]
    public void AProjectWithNoCoverFile_FallsBackToTheLibraryMatchOnArtistAndTitle()
    {
        var project = WriteProject("a", "<title>Song</title><artistName>Band</artistName>");
        var index = new StubIndex { ByTitle = { [("Band", "Song")] = "library.png" } };

        Assert.Equal("library.png", new SongLibraryLookup(index).FindCover(project));
        Assert.Equal([("Band", "Song")], index.TitleQueries);
    }

    [Fact]
    public void AProjectWithNoCoverAnywhere_GivesNothing()
    {
        var project = WriteProject("a", "<title>Song</title><artistName>Band</artistName>");

        Assert.Equal("", new SongLibraryLookup(new StubIndex()).FindCover(project));
    }

    [Fact]
    public void AMissingOrMalformedProject_GivesNothing()
    {
        var broken = Path.Combine(_root, "broken.dcproj");
        File.WriteAllText(broken, "<project><title>x");

        var lookup = new SongLibraryLookup(new StubIndex());

        Assert.Equal("", lookup.FindCover(Path.Combine(_root, "gone.dcproj")));
        Assert.Equal("", lookup.FindCover(broken));
    }

    [Fact]
    public void ALibraryKey_AsksTheIndexByItsSongKey()
    {
        var index = new StubIndex { ByKey = { ["Key"] = "k.png" } };

        Assert.Equal("k.png", new SongLibraryLookup(index).FindCover("lib:Key"));
    }

    [Fact]
    public void TheHeaderRead_StopsAtTheBulk_AndFollowsAChangedFile()
    {
        // Everything after <beats> is garbage: a reader that walked it would throw.
        var project = WriteProject("a", "<title>Song</title><artistName>Band</artistName>", "<beats><<<<");
        var index = new StubIndex();
        var lookup = new SongLibraryLookup(index);

        lookup.FindCover(project);
        lookup.FindCover(project);
        Assert.Equal(2, index.TitleQueries.Count);

        File.WriteAllText(project, "<project><title>Other</title><artistName>Band</artistName><beats/></project>");
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddMinutes(5));
        lookup.FindCover(project);

        Assert.Equal(("Band", "Other"), index.TitleQueries[^1]);
    }

    private string WriteProject(string name, string header, string tail = "<beats/>")
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "song.dcproj");
        File.WriteAllText(path, $"<project>{header}{tail}</project>");
        return path;
    }

    private static string Touch(string path)
    {
        File.WriteAllBytes(path, [1]);
        return path;
    }

    private sealed class StubIndex : ILibraryIndex
    {
        public Dictionary<string, string> ByKey { get; } = [];
        public Dictionary<(string, string), string> ByTitle { get; } = [];
        public List<(string, string)> TitleQueries { get; } = [];

        public ILibraryIndexScan? BeginScan() => null;
        public void TrySetSongRating(string psarcPath, string songKey, double rating) { }
        public LibraryIndexFacts ReadFacts() => LibraryIndexFacts.Empty;
        public string FindCoverImage(string songKey) => ByKey.GetValueOrDefault(songKey, "");

        public string FindCoverImageByTitle(string artist, string title)
        {
            TitleQueries.Add((artist, title));
            return ByTitle.GetValueOrDefault((artist, title), "");
        }
    }
}
