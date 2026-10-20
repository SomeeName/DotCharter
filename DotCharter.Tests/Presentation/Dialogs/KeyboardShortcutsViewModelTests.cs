using Avalonia.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Contracts.Shell;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.ViewModels.CommandPalette;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Help ▸ Keyboard shortcuts is GENERATED from the palette catalog's live
///     chord lookups: the shell's seam keeps only bound commands, the dialog
///     groups them by category in catalog order and filters by title or chord.
/// </summary>
public sealed class KeyboardShortcutsViewModelTests
{
    private sealed class Catalog(params ShortcutEntry[] entries) : IShortcutCatalog
    {
        public int Asked { get; private set; }

        public IReadOnlyList<ShortcutEntry> CurrentShortcuts()
        {
            Asked++;
            return entries;
        }
    }

    private static AppCommand Cmd(string title, string category, KeyChord? chord = null) =>
        new($"test.{title}", title, category, static () => { }, chord is { } c ? () => c : null);

    [Fact]
    public void The_shells_seam_keeps_only_bound_commands_in_catalog_order()
    {
        var entries = ShortcutEntries.From(
        [
            Cmd("Play", "Transport", new KeyChord(Key.Space)),
            Cmd("Unbound thing", "Transport"),
            Cmd("Save", "File", new KeyChord(Key.S, KeyModifiers.Control))
        ], FakeLocalization.English());

        Assert.Equal(["Play", "Save"], entries.Select(e => e.Title));
        Assert.Equal(["Transport", "File"], entries.Select(e => e.Category));
        Assert.All(entries, e => Assert.False(string.IsNullOrEmpty(e.Chord)));
    }

    [Fact]
    public void Rows_group_by_category_in_catalog_order()
    {
        var groups = KeyboardShortcutsViewModel.BuildGroups(
        [
            new ShortcutEntry("Transport", "Play", "Space"),
            new ShortcutEntry("File", "Save", "Ctrl+S"),
            new ShortcutEntry("Transport", "Metronome", "M")
        ], "");

        Assert.Equal(["Transport", "File"], groups.Select(g => g.Name));
        Assert.Equal(["Play", "Metronome"], groups[0].Rows.Select(r => r.Title));
    }

    [Fact]
    public void The_filter_matches_a_title_or_a_chord_case_insensitively()
    {
        var entries = new[] { new ShortcutEntry("Transport", "Play", "Space"), new ShortcutEntry("File", "Save", "Ctrl+S") };

        Assert.Equal("Play", Assert.Single(Assert.Single(KeyboardShortcutsViewModel.BuildGroups(entries, "play")).Rows).Title);
        Assert.Equal("Save", Assert.Single(Assert.Single(KeyboardShortcutsViewModel.BuildGroups(entries, "ctrl+s")).Rows).Title);
        Assert.Empty(KeyboardShortcutsViewModel.BuildGroups(entries, "zzz-no-match"));
    }

    [Fact]
    public void It_reads_the_catalog_once_on_open_and_typing_refilters()
    {
        var catalog = new Catalog(new ShortcutEntry("Transport", "Play", "Space"), new ShortcutEntry("File", "Save", "Ctrl+S"));
        var vm = new KeyboardShortcutsViewModel(catalog);
        Assert.Equal(2, vm.Groups.Count);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.FilterText = "save";
        Assert.Single(vm.Groups);
        Assert.False(vm.HasNoMatches);

        vm.FilterText = "nothing-matches-this";
        Assert.Empty(vm.Groups);
        Assert.True(vm.HasNoMatches);
        Assert.Contains(nameof(IKeyboardShortcutsScreen.Groups), changed);
        Assert.Equal(1, catalog.Asked);
    }

    [Fact]
    public void An_empty_catalog_opens_empty_and_Close_answers_true()
    {
        var vm = new KeyboardShortcutsViewModel(new Catalog());
        Assert.True(vm.HasNoMatches);

        vm.CloseCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var vm = new KeyboardShortcutsViewModel(new Catalog(new ShortcutEntry("File", "Save", "Ctrl+S")));
        Assert.Empty(ContractConformanceTests.Violations(typeof(IKeyboardShortcutsScreen), vm));
    }
}
