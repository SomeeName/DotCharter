using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Catalog;

/// <summary>
///     The folder store: add / list / find-by-name / rename / delete, and the
///     rule that one unreadable file must never cost the rest of the library.
/// </summary>
public class PhraseCatalogServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAppLogger _logger = new();

    public void Dispose()
    {
        BestEffortDelete.Directory(_dir);

        GC.SuppressFinalize(this);
    }

    private PhraseCatalogService NewService() => new(_dir, new RscpChartSerializer(), _logger);

    private static Arrangement Source()
    {
        var arrangement = new Arrangement { Name = "Guitar 1" };
        foreach (var step in TuningUtilities.GetOffsetsForTuningType("E_STANDARD", 6))
            arrangement.TuningPitches.Add(new TuningOffset(step));
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private static GuitarNote[] OneNote(int beat = 0, int fret = 3) =>
    [
        new() { Position = new FractionalPosition(beat), StringIndex = 5, Fret = fret }
    ];

    [Fact]
    public void AnEmptyFolderReadsAsAnEmptyCatalog() => Assert.Empty(NewService().Entries);

    [Fact]
    public void AnAddedPhraseIsReadableByASecondService()
    {
        var added = NewService().Add("Main riff", Source(), OneNote(), [], [], []);
        Assert.NotNull(added);

        var reloaded = NewService().Entries;

        var entry = Assert.Single(reloaded);
        Assert.Equal("Main riff", entry.Name);
        Assert.Equal(1, entry.NoteCount);
    }

    [Fact]
    public void AddRefusesAnEmptyName()
    {
        Assert.Null(NewService().Add("   ", Source(), OneNote(), [], [], []));
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Fact]
    public void AddRefusesAnEmptySelection() => Assert.Null(NewService().Add("Nothing", Source(), [], [], [], []));

    [Fact]
    public void TwoPhrasesWithTheSameNameGetDistinctFiles()
    {
        var svc = NewService();
        var first = svc.Add("Riff", Source(), OneNote(), [], [], []);
        var second = svc.Add("Riff", Source(), OneNote(fret: 5), [], [], []);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Equal(2, NewService().Entries.Count);
    }

    [Fact]
    public void FindByNameIsExactAndCaseInsensitive()
    {
        var svc = NewService();
        svc.Add("Chorus", Source(), OneNote(), [], [], []);

        Assert.NotNull(svc.FindByName("chorus"));
        Assert.NotNull(svc.FindByName("  CHORUS  "));
        Assert.Null(svc.FindByName("chor"));
        Assert.Null(svc.FindByName(""));
    }

    [Fact]
    public void RenameKeepsTheIdAndSurvivesAReload()
    {
        var svc = NewService();
        var entry = svc.Add("Old", Source(), OneNote(), [], [], [])!;

        Assert.True(svc.Rename(entry, "New"));

        Assert.Equal("New", svc.Entries[0].Name);
        Assert.Equal(entry.Id, svc.Entries[0].Id);
        Assert.Equal("New", NewService().Entries[0].Name);
    }

    [Fact]
    public void RemoveDeletesTheFile()
    {
        var svc = NewService();
        var entry = svc.Add("Gone", Source(), OneNote(), [], [], [])!;

        Assert.True(svc.Remove(entry));

        Assert.Empty(svc.Entries);
        Assert.Empty(NewService().Entries);
    }

    [Fact]
    public void OneUnreadableFileDoesNotCostTheRestOfTheLibrary()
    {
        var svc = NewService();
        svc.Add("Good", Source(), OneNote(), [], [], []);
        File.WriteAllText(Path.Combine(_dir, "broken.rscp"), "<not really xml");

        var entries = NewService().Entries;

        var entry = Assert.Single(entries);
        Assert.Equal("Good", entry.Name);
        Assert.True(_logger.HasEntryContaining("WARN", "unreadable"));
    }

    [Fact]
    public void ChangesRaiseCatalogChanged()
    {
        var svc = NewService();
        var raised = 0;
        svc.CatalogChanged += () => raised++;

        var entry = svc.Add("A", Source(), OneNote(), [], [], [])!;
        svc.Rename(entry, "B");
        svc.Remove(svc.Entries[0]);

        Assert.Equal(3, raised);
    }

    [Fact]
    public void InvalidateCacheRescansTheFolder()
    {
        var svc = NewService();
        Assert.Empty(svc.Entries);

        NewService().Add("Late", Source(), OneNote(), [], [], []);
        Assert.Empty(svc.Entries); // still the cached view

        svc.InvalidateCache();

        Assert.Single(svc.Entries);
    }

    [Theory]
    [InlineData("Main riff", "main-riff")]
    [InlineData("Chorus #2 (½ time)", "chorus-2-time")]
    [InlineData("///", "phrase")]
    [InlineData("", "phrase")]
    public void NamesFoldIntoFilesystemSafeIds(string name, string expected) => Assert.Equal(expected, PhraseCatalogService.Sanitize(name));
}
