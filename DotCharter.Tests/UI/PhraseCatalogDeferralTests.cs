using DotCharter.Services.Editing.Catalog;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>PhraseCatalogViewModel</c> (the Riff Library pane) is a CONSTRUCTOR
///     ARGUMENT of <c>MainWindowViewModel</c>, so its constructor runs on the
///     pre-window path that <c>StartupWorkDeferralTests</c> pins for the other
///     services. It used to call <c>RefreshEntries()</c> straight away, which
///     reads <c>IPhraseCatalogService.Entries</c> — a FULL <c>.rscp</c> project
///     deserialize per stored riff, over a folder that grows with use — for a
///     pane that is not on screen at boot and may never be opened.
///     <para>
///         These are behavioural, not wall-clock: the fake catalog counts how
///         many times the library was actually read. The view model's constructor
///         touches nothing but the catalog subscription, which is exactly why the
///         other collaborators can be passed as null here.
///     </para>
/// </summary>
public sealed class PhraseCatalogDeferralTests
{
    [Fact]
    public void ConstructingThePaneDoesNotReadTheLibrary()
    {
        var catalog = new CountingCatalog();

        var vm = Build(catalog);

        Assert.Equal(0, catalog.EntryReads);
        Assert.Empty(vm.Entries);
        Assert.False(vm.HasEntries);
    }

    [Fact]
    public void EnsureLoadedFillsThePaneFromTheLibrary()
    {
        var catalog = new CountingCatalog();
        catalog.Store.Add(Entry("riff-a", "Riff A"));
        catalog.Store.Add(Entry("riff-b", "Riff B"));
        var vm = Build(catalog);

        vm.EnsureLoaded();

        Assert.Equal(1, catalog.EntryReads);
        Assert.Equal(2, vm.Entries.Count);
        Assert.True(vm.HasEntries);
        Assert.Equal("riff-a", vm.SelectedEntry?.Id);
    }

    [Fact]
    public void ACatalogChangeStillRefreshesThePane()
    {
        // The deferral must not cost the pane its live updates: capturing a riff
        // (or a backup import dropping the cache) has to land in the list.
        var catalog = new CountingCatalog();
        var vm = Build(catalog);

        catalog.Store.Add(Entry("captured", "Captured"));
        catalog.RaiseChanged();

        Assert.Single(vm.Entries);
        Assert.Equal("captured", vm.Entries[0].Id);
    }

    [Fact]
    public void TheSearchBoxStillFiltersWhatIsLoaded()
    {
        var catalog = new CountingCatalog();
        catalog.Store.Add(Entry("verse-lick", "Verse lick"));
        catalog.Store.Add(Entry("chorus-stab", "Chorus stab"));
        var vm = Build(catalog);
        vm.EnsureLoaded();

        vm.SearchText = "chorus";

        Assert.Single(vm.Entries);
        Assert.Equal("chorus-stab", vm.Entries[0].Id);
    }

    // Only the catalog is touched by the constructor — see the class summary.
    private static PhraseCatalogViewModel Build(IPhraseCatalogService catalog)
        => new(catalog, null!, null!, null!, null!, FakeLocalization.English());

    private static PhraseCatalogEntry Entry(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Source = new PhraseSourceContext(),
        Payload = new ClipboardPayload(),
        CreatedUtc = DateTime.UtcNow
    };

    private sealed class CountingCatalog : IPhraseCatalogService
    {
        public List<PhraseCatalogEntry> Store { get; } = [];

        public int EntryReads { get; private set; }

        public event Action? CatalogChanged;

        public void RaiseChanged() => CatalogChanged?.Invoke();

        public IReadOnlyList<PhraseCatalogEntry> Entries
        {
            get
            {
                EntryReads++;
                return Store;
            }
        }

        public string StorageDirectory => "";

        public PhraseCatalogEntry? Add(string name, Arrangement source, IEnumerable<GuitarNote> notes,
            IEnumerable<Chord> chords, IEnumerable<HandShape> handShapes, IEnumerable<FretAnchor> anchors)
            => null;

        public PhraseCatalogEntry? FindByName(string name) => null;

        public bool Rename(PhraseCatalogEntry entry, string newName) => false;

        public bool Remove(PhraseCatalogEntry entry) => false;

        public void InvalidateCache() => RaiseChanged();
    }
}
