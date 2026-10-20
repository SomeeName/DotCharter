using System.Reflection;
using Avalonia.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Infrastructure.Input;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Edit ▸ Pattern builder over a real chart editor and pattern store: the
///     rail is counted live against the seed, the five verbs that need a picked
///     pattern are greyed WITH a reason, a key another owner holds is reported,
///     and nothing reaches the store before Save.
/// </summary>
public sealed class PatternBuilderViewModelTests
{
    private static readonly KeyChord Owned = new(Key.F9, KeyModifiers.Control);

    private sealed class Rig
    {
        public ChartEditHarness Chart { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeDispatch Keybinds { get; } = new();
        public CountingEditing Editing { get; }
        public MatchPatternStore Store { get; }

        public Rig()
        {
            var editing = DispatchProxy.Create<IGlobalEditingCoordinator, CountingEditing>();
            Editing = (CountingEditing)(object)editing;
            EditingProxy = editing;
            Store = new MatchPatternStore(Config);
        }

        public IGlobalEditingCoordinator EditingProxy { get; }

        public PatternBuilderViewModel Open() => new(Chart.Services, Store, EditingProxy, Selection, Keybinds);
    }

    /// <summary>Counts a pattern by its name's length, so each row in the rail gets a different number.</summary>
    public class CountingEditing : RecordingProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var answer = base.Invoke(method, args);
            return method?.Name == nameof(IGlobalEditingCoordinator.CountPatternMatches)
                ? ((MatchPattern)args![0]!).Name.Length
                : answer;
        }
    }

    private sealed class FakeDispatch : IKeybindDispatch
    {
        public int Refreshes { get; private set; }

        public Dictionary<KeyChord, string> Owners { get; } = [];

        public void RefreshBinds() => Refreshes++;

        public string? DescribeBinding(KeyChord chord) => Owners.GetValueOrDefault(chord);
    }

    private static IEnumerable<System.Windows.Input.ICommand> NeedsAPattern(IPatternBuilderScreen vm) =>
        [vm.DuplicatePatternCommand, vm.DeletePatternCommand, .. RunVerbs(vm)];

    private static IEnumerable<System.Windows.Input.ICommand> RunVerbs(IPatternBuilderScreen vm) =>
        [vm.RunSelectCommand, vm.RunNextCommand, vm.RunPreviousCommand];

    [Fact]
    public void Conforms_to_its_contract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IPatternBuilderScreen), new Rig().Open()));

    [Fact]
    public void Opens_on_the_stored_patterns_with_the_first_picked()
    {
        var rig = new Rig();
        rig.Selection.SelectedNotes.Add(new GuitarNote());
        var vm = rig.Open();

        Assert.NotEmpty(vm.Patterns);
        Assert.Same(vm.Patterns[0], vm.SelectedPattern);
        Assert.All(NeedsAPattern(vm), c => Assert.True(c.CanExecute(null)));
        Assert.Equal("", vm.NoPatternSelectedReason);
    }

    [Fact]
    public void The_try_bar_verbs_wait_for_a_usable_seed()
    {
        var rig = new Rig();
        var vm = rig.Open();
        Assert.All(RunVerbs(vm), c => Assert.False(c.CanExecute(null)));
        Assert.True(vm.DuplicatePatternCommand.CanExecute(null));

        rig.Selection.SelectedNotes.Add(new GuitarNote());
        vm.RefreshPreview();
        Assert.All(RunVerbs(vm), c => Assert.True(c.CanExecute(null)));
    }

    [Fact]
    public void With_no_pattern_picked_the_five_verbs_are_greyed_and_say_why()
    {
        var rig = new Rig();
        rig.Selection.SelectedNotes.Add(new GuitarNote());
        var vm = rig.Open();
        while (vm.Patterns.Count > 0) vm.DeletePatternCommand.Execute(null);

        Assert.Null(vm.SelectedPattern);
        Assert.All(NeedsAPattern(vm), c => Assert.False(c.CanExecute(null)));
        Assert.NotEqual("", vm.NoPatternSelectedReason);

        vm.AddPatternCommand.Execute(null);
        Assert.All(NeedsAPattern(vm), c => Assert.True(c.CanExecute(null)));
        Assert.Equal("", vm.NoPatternSelectedReason);
    }

    [Fact]
    public void Every_pattern_in_the_rail_is_counted_against_the_seed()
    {
        var rig = new Rig();
        var vm = rig.Open();
        Assert.False(vm.HasSeed);
        Assert.All(vm.Patterns, p => Assert.Equal("", p.MatchCountLabel));

        rig.Selection.SelectedNotes.Add(new GuitarNote());
        vm.RefreshPreview();

        Assert.True(vm.SeedIsUsable);
        Assert.All(vm.Patterns, p => Assert.Equal(p.Name.Length.ToString(System.Globalization.CultureInfo.CurrentCulture),
            p.MatchCountLabel));
    }

    [Fact]
    public void Renaming_a_pattern_does_not_recount_the_rail()
    {
        var rig = new Rig();
        rig.Selection.SelectedNotes.Add(new GuitarNote());
        var vm = rig.Open();
        var counted = rig.Editing.Calls.Count(c => c.StartsWith("CountPatternMatches", StringComparison.Ordinal));

        vm.SelectedPattern!.Name = "Renamed";
        Assert.Equal(counted, rig.Editing.Calls.Count(c => c.StartsWith("CountPatternMatches", StringComparison.Ordinal)));

        vm.SelectedPattern.MatchFingering = !vm.SelectedPattern.MatchFingering;
        Assert.True(rig.Editing.Calls.Count(c => c.StartsWith("CountPatternMatches", StringComparison.Ordinal)) > counted);
    }

    [Fact]
    public void A_key_someone_else_owns_is_reported_and_the_patterns_own_is_not()
    {
        var rig = new Rig();
        var vm = rig.Open();
        var row = vm.SelectedPattern!;
        rig.Keybinds.Owners[Owned] = $"pattern:{row.Id}:select";

        row.SelectKey = Owned;
        vm.RefreshPreview();
        Assert.False(vm.HasConflict);

        rig.Keybinds.Owners[Owned] = "Toggle metronome";
        vm.RefreshPreview();
        Assert.True(vm.HasConflict);
        Assert.Contains("Toggle metronome", vm.ConflictSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_keeps_the_facets_and_drops_the_keys()
    {
        var vm = new Rig().Open();
        var original = vm.SelectedPattern!;
        original.SelectKey = Owned;

        vm.DuplicatePatternCommand.Execute(null);

        var copy = vm.SelectedPattern!;
        Assert.NotSame(original, copy);
        Assert.False(copy.HasKeys);
        Assert.Equal(original.ScopeChords, copy.ScopeChords);
        Assert.NotEqual(original.Id, copy.Id);
    }

    [Fact]
    public void Save_persists_every_row_refreshes_the_binds_and_answers_true()
    {
        var rig = new Rig();
        var vm = rig.Open();
        vm.AddPatternCommand.Execute(null);
        var count = vm.Patterns.Count;

        vm.SaveCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(1, rig.Keybinds.Refreshes);
        Assert.Equal(count, rig.Store.EnsureSeeded().Count);
    }

    [Fact]
    public void Cancel_answers_false_and_stores_nothing()
    {
        var rig = new Rig();
        var before = rig.Store.EnsureSeeded().Count;
        var vm = rig.Open();
        vm.AddPatternCommand.Execute(null);

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(before, rig.Store.EnsureSeeded().Count);
        Assert.Equal(0, rig.Keybinds.Refreshes);
    }
}
