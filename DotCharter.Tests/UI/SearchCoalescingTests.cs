using DotCharter.Services.Editing.Catalog;
using DotCharter.UI.ViewModels.CommandPalette;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The per-keystroke refill rule (2026-08-11): a search box may not rebuild
///     its result collection once per key. The command palette and the riff
///     library both did — every keystroke re-ranked and re-filled an
///     ObservableCollection on the UI thread — and the settings search carried
///     its own hand-rolled 150 ms CancellationTokenSource debounce. All three
///     now route through <c>ViewModels/Shell/CoalescedFilterPass</c>
///     (PresetBrowserViewModel's ctor pattern: the post is injectable, tests
///     drive a queue by hand, the product passes the dispatcher's Background
///     post so a typing burst drains first).
/// </summary>
public sealed class SearchCoalescingTests
{
    [Fact]
    public void ATypingBurstInTheCommandPalette_RanksOnce_WithTheFinalQuery()
    {
        var queue = new List<Action>();
        var vm = new CommandPaletteViewModel(
            () =>
            [
                new AppCommand("a", "Add note", "Editing", () => { }),
                new AppCommand("b", "Add chord", "Editing", () => { }),
                new AppCommand("c", "Open project", "File", () => { })
            ],
            FakeLocalization.English(),
            queue.Add);

        vm.Open(); // discrete gesture: fills immediately
        Assert.Equal(3, vm.Results.Count);
        Assert.Empty(queue);

        vm.Query = "a";
        vm.Query = "ad";
        vm.Query = "add";

        // The burst queued exactly ONE pass and nothing ran yet.
        Assert.Single(queue);
        Assert.Equal(3, vm.Results.Count);

        queue[0]();
        Assert.All(vm.Results, r => Assert.StartsWith("Add", r.Title, StringComparison.Ordinal));
        Assert.Equal(2, vm.Results.Count);
    }

    [Fact]
    public void ATypingBurstInTheRiffLibrary_FiltersOnce_WithTheFinalQuery()
    {
        var catalog = new StubCatalog();
        catalog.Store.Add(Entry("verse-lick", "Verse lick"));
        catalog.Store.Add(Entry("chorus-stab", "Chorus stab"));
        var queue = new List<Action>();
        var vm = new PhraseCatalogViewModel(catalog, null!, null!, null!, null!, FakeLocalization.English(), queue.Add);
        vm.EnsureLoaded();

        vm.SearchText = "c";
        vm.SearchText = "ch";
        vm.SearchText = "chorus";

        Assert.Single(queue);
        Assert.Equal(2, vm.Entries.Count);

        queue[0]();
        Assert.Single(vm.Entries);
        Assert.Equal("chorus-stab", vm.Entries[0].Id);
    }

    /// <summary>The settings search must never grow its timer back — the
    /// debounce was a CTS + <c>Task.Delay(150)</c> per keystroke, replaced by
    /// the shared pass (no timer, no guessed delay). It lives in the settings
    /// shell's view model now, so the pass it uses is the Presentation one.</summary>
    [Fact]
    public void TheSettingsSearch_HasNoHandRolledTimerDebounce()
    {
        var code = File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Presentation", "Screens", "Settings", "SettingsShellViewModel.Search.cs"));
        Assert.DoesNotContain("Task.Delay", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", code, StringComparison.Ordinal);
        Assert.Contains("new CoalescedPass(", code, StringComparison.Ordinal);
    }

    private static PhraseCatalogEntry Entry(string id, string name)
    {
        return new PhraseCatalogEntry
        {
            Id = id,
            Name = name,
            Source = new PhraseSourceContext(),
            Payload = new ClipboardPayload(),
            CreatedUtc = DateTime.UtcNow
        };
    }

    private sealed class StubCatalog : IPhraseCatalogService
    {
        public List<PhraseCatalogEntry> Store { get; } = [];

        public event Action? CatalogChanged { add { } remove { } }

        public IReadOnlyList<PhraseCatalogEntry> Entries => Store;
        public string StorageDirectory => "";

        public PhraseCatalogEntry? Add(string name, Arrangement source, IEnumerable<GuitarNote> notes,
            IEnumerable<Chord> chords, IEnumerable<HandShape> handShapes, IEnumerable<FretAnchor> anchors)
            => null;

        public PhraseCatalogEntry? FindByName(string name) => Store.FirstOrDefault(e => e.Name == name);
        public bool Rename(PhraseCatalogEntry entry, string newName) => false;
        public bool Remove(PhraseCatalogEntry entry) => false;
        public void InvalidateCache() { }
    }
}
