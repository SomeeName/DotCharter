using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Riff Library's find box is a FILTER over the library, and it stays
///     where the user left it. So the two verbs that mint or re-title a riff —
///     capture and rename — can produce an entry the current filter does not
///     match, and both then reported success over a pane that showed nothing:
///     <c>Entries</c> was rebuilt from the still-active filter, the
///     <c>FirstOrDefault(e =&gt; e.Id == entry.Id)</c> that followed found
///     nothing, and the selection was dropped to null.
///     <para>
///         "Renamed 'Bridge riff'" over an empty list reads exactly like a rename
///         that ate the riff — and a stored riff is the only copy of itself, so
///         that is a genuinely alarming thing to show. The fix is to clear the
///         filter when the entry it produced would be hidden by it, which is the
///         one reading of the gesture that cannot be wrong: the user just NAMED
///         the thing they want to see.
///     </para>
/// </summary>
public sealed class PhraseCatalogSearchFilterTests
{
    private sealed class StubCatalog : IPhraseCatalogService
    {
        public List<PhraseCatalogEntry> Store { get; } = [];

        public event Action? CatalogChanged;

        public IReadOnlyList<PhraseCatalogEntry> Entries => Store;
        public string StorageDirectory => "";

        public PhraseCatalogEntry? Add(string name, Arrangement source, IEnumerable<GuitarNote> notes,
            IEnumerable<Chord> chords, IEnumerable<HandShape> handShapes, IEnumerable<FretAnchor> anchors)
            => null;

        public PhraseCatalogEntry? FindByName(string name) =>
            Store.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The real service rewrites the entry in place under the SAME
        /// id (the file name never follows the display name) and raises the
        /// change event — which is what re-runs the view model's filter.</summary>
        public bool Rename(PhraseCatalogEntry entry, string newName)
        {
            var index = Store.FindIndex(e => e.Id == entry.Id);
            if (index < 0) return false;
            Store[index] = new PhraseCatalogEntry
            {
                Id = entry.Id,
                Name = newName.Trim(),
                Source = entry.Source,
                Payload = entry.Payload,
                CreatedUtc = entry.CreatedUtc
            };
            CatalogChanged?.Invoke();
            return true;
        }

        public bool Remove(PhraseCatalogEntry entry)
        {
            var removed = Store.Remove(entry);
            if (removed) CatalogChanged?.Invoke();
            return removed;
        }

        public void InvalidateCache() { }
    }

    private static (PhraseCatalogViewModel Vm, StubCatalog Catalog) Create()
    {
        var catalog = new StubCatalog();
        catalog.Store.Add(Entry("verse-lick", "Verse lick"));
        catalog.Store.Add(Entry("chorus-stab", "Chorus stab"));
        // Synchronous search pass; the editing/transport/state services are only
        // reached by capture and paste, which these tests never run.
        var vm = new PhraseCatalogViewModel(catalog, null!, null!, null!, null!, FakeLocalization.English(), action => action());
        vm.EnsureLoaded();
        return (vm, catalog);
    }

    private static PhraseCatalogEntry Entry(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Source = new PhraseSourceContext(),
        Payload = new ClipboardPayload(),
        CreatedUtc = DateTime.UtcNow
    };

    [Fact]
    public void RenamingOutOfTheActiveFilter_StillShowsTheRiff()
    {
        var (vm, catalog) = Create();
        vm.SearchText = "verse";
        Assert.Single(vm.Entries);
        vm.SelectedEntry = vm.Entries[0];

        vm.NewPhraseName = "Bridge riff";
        vm.RenameSelectedCommand.Execute(null);

        // The rename worked...
        Assert.Contains(catalog.Store, e => e.Name == "Bridge riff");
        Assert.False(vm.StatusIsProblem);
        // ...and the pane says so by SHOWING it, selected, rather than reporting
        // success over an empty list.
        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal("Bridge riff", vm.SelectedEntry!.Name);
        Assert.Contains(vm.Entries, e => e.Name == "Bridge riff");
        Assert.True(vm.HasEntries);
    }

    /// <summary>A rename the filter still matches must NOT throw the filter
    /// away — clearing it unconditionally would be a different bug, and would
    /// make the test above pass for the wrong reason.</summary>
    [Fact]
    public void RenamingWithinTheActiveFilter_KeepsIt()
    {
        var (vm, _) = Create();
        vm.SearchText = "verse";
        vm.SelectedEntry = vm.Entries[0];

        vm.NewPhraseName = "Verse lick B";
        vm.RenameSelectedCommand.Execute(null);

        Assert.Equal("verse", vm.SearchText);
        Assert.Single(vm.Entries);
        Assert.Equal("Verse lick B", vm.SelectedEntry!.Name);
    }

    // ------------------------------------------------------------------
    // Paste by typed name (EOF's SHIFT+~) over a library where two riffs
    // legitimately share a display name.
    // ------------------------------------------------------------------

    /// <summary>Records what the paste verb was handed, and nothing else — the
    /// other 23 members of the coordinator are unreachable from this pane.</summary>
    private sealed class SpyEditing : IGlobalEditingCoordinator
    {
        public List<PhraseCatalogEntry> Pasted { get; } = [];

        public PhrasePasteOutcome PastePhrase(PhraseCatalogEntry entry, double currentTimeMs, int gridDivisor)
        {
            Pasted.Add(entry);
            return new PhrasePasteOutcome(PhrasePasteResult.Pasted, null);
        }

        public PhraseCatalogEntry? CapturePhrase(EditorFocusArea focusArea, string name) => null;

        public void Undo() => throw new NotSupportedException();
        public void Redo() => throw new NotSupportedException();
        public void CopySelected(EditorFocusArea focusArea) => throw new NotSupportedException();
        public void Paste(double currentTimeMs, int gridDivisor) => throw new NotSupportedException();
        public void PasteSpecial(double t, int g, PasteSpecialMode m) => throw new NotSupportedException();
        public void SelectAllEvents() => throw new NotSupportedException();
        public void SelectAllNotes() => throw new NotSupportedException();
        public void SelectLikePattern() => throw new NotSupportedException();
        public int SelectByPattern(MatchPattern pattern) => throw new NotSupportedException();
        public double? MoveToPatternMatch(MatchPattern p, double f, int d) => throw new NotSupportedException();
        public int CountPatternMatches(MatchPattern pattern) => throw new NotSupportedException();

        public bool PatternSeedIsFromAnotherArrangement() => throw new NotSupportedException();
        public void GenerateHandShapeFromSelection() => throw new NotSupportedException();
        public void GenerateArpeggioFromSelection() => throw new NotSupportedException();
        public void AcceptSuggestedHandShapesInSelection(bool arpeggio) => throw new NotSupportedException();
        public void SplitHandShapeAtSelection() => throw new NotSupportedException();
        public void DeleteSelected(EditorFocusArea focusArea) => throw new NotSupportedException();
        public void DeleteSelectedRelated(EditorFocusArea focusArea) => throw new NotSupportedException();
        public void SendSelectionToArrangement(Arrangement target) => throw new NotSupportedException();
        public void SnapSelectedNotes(int gridDivisor) => throw new NotSupportedException();
        public void SnapAllNotes(int gridDivisor) => throw new NotSupportedException();
        public void ApplyDoubleTimeToSelection() => throw new NotSupportedException();
        public void ApplyHalfTimeToSelection() => throw new NotSupportedException();
    }

    private static (PhraseCatalogViewModel Vm, StubCatalog Catalog, SpyEditing Editing) CreatePasteable()
    {
        var catalog = new StubCatalog();
        var editing = new SpyEditing();
        var state = new ProjectState();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var vm = new PhraseCatalogViewModel(catalog, editing, null!, transport, state, FakeLocalization.English(), action => action());
        return (vm, catalog, editing);
    }

    /// <summary>
    ///     Riff NAMES are not unique — the store keeps IDS unique and
    ///     deliberately lets two riffs share a display name
    ///     (<c>PhraseCatalogServiceTests.TwoPhrasesWithTheSameNameGetDistinctFiles</c>).
    ///     <c>FindByName</c> then returns whichever the newest-first order puts
    ///     in front, so paste-by-typed-name pasted an arbitrary one of them into
    ///     the chart, reported the name it had been asked for, and left the user
    ///     no way to tell which they had got. A paste is an EDIT; guessing is the
    ///     one thing it may not do.
    /// </summary>
    [Fact]
    public void PasteByName_RefusesToGuessBetweenTwoRiffsOfTheSameName()
    {
        var (vm, catalog, editing) = CreatePasteable();
        catalog.Store.Add(Entry("chorus", "Chorus"));
        catalog.Store.Add(Entry("chorus-2", "Chorus"));
        vm.EnsureLoaded();

        vm.SearchText = "Chorus";
        vm.PasteByNameCommand.Execute(null);

        Assert.Empty(editing.Pasted);
        Assert.True(vm.StatusIsProblem);
        Assert.Contains("Chorus", vm.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>The non-vacuous half: one riff of that name still pastes, so the
    /// guard is the AMBIGUITY and not the gesture.</summary>
    [Fact]
    public void PasteByName_StillPastesAnUnambiguousName()
    {
        var (vm, catalog, editing) = CreatePasteable();
        catalog.Store.Add(Entry("chorus", "Chorus"));
        catalog.Store.Add(Entry("verse", "Verse"));
        vm.EnsureLoaded();

        vm.SearchText = "Chorus";
        vm.PasteByNameCommand.Execute(null);

        Assert.Single(editing.Pasted);
        Assert.Equal("chorus", editing.Pasted[0].Id);
        Assert.False(vm.StatusIsProblem);
    }
}
