using DotCharter.Services.Editing.Catalog;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     A stored riff is the ONLY copy of itself — it lives in the per-user
///     library, not in the project, so no undo, no reload and no file on disk
///     brings it back. Delete ran on ONE click of a button sitting in the same
///     strip as Paste and Rename, and when it worked it said nothing at all.
///     It now takes the two-click confirm latch (the idiom in
///     <c>docs/agents/ui/dialogs.md</c>, mirroring
///     <c>CheckpointDeleteConfirmTests</c>): first click arms and relabels,
///     second click removes; moving the selection or a catalogue refresh
///     disarms, and both outcomes are reported.
/// </summary>
public sealed class PhraseCatalogDeleteConfirmTests
{
    private sealed class StubCatalog : IPhraseCatalogService
    {
        public List<PhraseCatalogEntry> Store { get; } = [];

        /// <summary>Set to make <see cref="Remove" /> fail the way a locked or
        /// already-deleted file does.</summary>
        public bool RemoveFails { get; set; }

        public event Action? CatalogChanged;

        public IReadOnlyList<PhraseCatalogEntry> Entries => Store;
        public string StorageDirectory => "";

        public PhraseCatalogEntry? Add(string name, Arrangement source, IEnumerable<GuitarNote> notes,
            IEnumerable<Chord> chords, IEnumerable<HandShape> handShapes, IEnumerable<FretAnchor> anchors)
            => null;

        public PhraseCatalogEntry? FindByName(string name) => Store.FirstOrDefault(e => e.Name == name);
        public bool Rename(PhraseCatalogEntry entry, string newName) => false;

        public bool Remove(PhraseCatalogEntry entry)
        {
            if (RemoveFails) return false;
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
        // Synchronous search post; the other services are only reached by the
        // capture/paste verbs, which these tests never run.
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
    public void TheFirstClick_Arms_AndDeletesNothing()
    {
        var (vm, catalog) = Create();
        vm.SelectedEntry = vm.Entries[0];

        vm.DeleteSelectedCommand.Execute(null);

        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal(2, catalog.Store.Count);
        Assert.Equal(FakeLocalization.English()["PhraseCatalog_DeleteConfirm"], vm.DeleteCaption);
        // …and it SAYS what the second click will do — the delete used to be
        // silent in both directions.
        Assert.Contains("Verse lick", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecondClick_Deletes_AndReportsIt()
    {
        var (vm, catalog) = Create();
        vm.SelectedEntry = vm.Entries.First(e => e.Id == "verse-lick");

        vm.DeleteSelectedCommand.Execute(null);
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Single(catalog.Store);
        Assert.Equal("chorus-stab", catalog.Store[0].Id);
        Assert.False(vm.IsConfirmingDelete);
        Assert.False(vm.StatusIsProblem);
        Assert.Contains("Verse lick", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PickingAnotherRiff_Disarms_SoTheNextClickCannotDelete()
    {
        var (vm, catalog) = Create();
        vm.SelectedEntry = vm.Entries.First(e => e.Id == "verse-lick");
        vm.DeleteSelectedCommand.Execute(null);

        vm.SelectedEntry = vm.Entries.First(e => e.Id == "chorus-stab");

        Assert.False(vm.IsConfirmingDelete);
        vm.DeleteSelectedCommand.Execute(null);
        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal(2, catalog.Store.Count);
    }

    [Fact]
    public void ARefreshOfTheList_Disarms()
    {
        var (vm, catalog) = Create();
        vm.SelectedEntry = vm.Entries[0];
        vm.DeleteSelectedCommand.Execute(null);

        // A rebuilt list is a new set of rows — an armed Delete must not ride
        // across it. The selection is unchanged, so nothing else would disarm.
        vm.EnsureLoaded();

        Assert.False(vm.IsConfirmingDelete);
        vm.DeleteSelectedCommand.Execute(null);
        Assert.Equal(2, catalog.Store.Count);
    }

    [Fact]
    public void AFailedRemove_SaysSo_InsteadOfLookingLikeANoOp()
    {
        var (vm, catalog) = Create();
        catalog.RemoveFails = true;
        vm.SelectedEntry = vm.Entries[0];

        vm.DeleteSelectedCommand.Execute(null);
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal(2, catalog.Store.Count);
        Assert.True(vm.StatusIsProblem);
        Assert.Contains("Verse lick", vm.StatusMessage, StringComparison.Ordinal);
    }
}
