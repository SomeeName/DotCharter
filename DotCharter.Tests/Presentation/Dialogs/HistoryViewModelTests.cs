using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Edit ▸ History over the branching undo tree: the rows are depth-first,
///     the current state is marked, a preserved branch is tagged, a click walks
///     the tree, and the list follows the history only while the dialog is up.
/// </summary>
public sealed class HistoryViewModelTests
{
    private static HistoryViewModel Open(HistoryService history) =>
        new(history, FakeLocalization.English(), new FakeAppLogger());

    [Fact]
    public void It_lists_the_tree_depth_first_and_marks_the_current_state()
    {
        var history = new HistoryService(new FakeAppLogger());
        history.Do(new CountingAction());
        history.Do(new CountingAction());

        var vm = Open(history);

        Assert.Equal(3, vm.Nodes.Count); // root + 2 actions
        Assert.True(vm.Nodes[2].IsCurrent);
        Assert.All(vm.Nodes, n => Assert.False(n.IsBranch));
        Assert.True(vm.HasNodes);
        Assert.Equal(FakeLocalization.English()["History_InitialState"], vm.Nodes[0].Description);
        Assert.Equal("Counting", vm.Nodes[1].Description);
    }

    [Fact]
    public void JumpTo_walks_the_history_and_the_list_follows()
    {
        var history = new HistoryService(new FakeAppLogger());
        var a = new CountingAction();
        var b = new CountingAction();
        history.Do(a);
        history.Do(b);
        var vm = Open(history);

        vm.JumpToCommand.Execute(vm.Nodes[1]); // the state after action a

        Assert.Equal(1, b.UndoCount);
        Assert.Equal(0, a.UndoCount);
        Assert.True(vm.Nodes[1].IsCurrent);
        Assert.False(vm.Nodes[2].IsCurrent);
    }

    [Fact]
    public void A_preserved_branch_is_tagged_and_counted_in_the_summary()
    {
        var history = new HistoryService(new FakeAppLogger());
        history.Do(new CountingAction()); // a
        history.Do(new CountingAction()); // b
        history.Undo();
        history.Do(new CountingAction()); // c — b is kept as a branch

        var vm = Open(history);

        Assert.Equal(4, vm.Nodes.Count);
        Assert.Single(vm.Nodes, n => n.IsBranch);
        Assert.Single(vm.Nodes, n => n.IsCurrent);
        Assert.Equal(16, vm.Nodes.Single(n => n.IsBranch).Indent);
        var loc = FakeLocalization.English();
        Assert.Equal(
            $"{loc.Plural("History_Summary_StatesOne", "History_Summary_StatesFmt", 4)} · " +
            loc.Plural("History_Summary_BranchesOne", "History_Summary_BranchesFmt", 1),
            vm.Summary);
    }

    /// <summary>The footer is one line, "1 state · no branches": the zero-branch half
    /// does not start a new sentence after the dot (it read "No branches").</summary>
    [Fact]
    public void With_no_branches_the_summary_reads_as_one_line()
    {
        var history = new HistoryService(new FakeAppLogger());
        var vm = Open(history);

        var after = vm.Summary[(vm.Summary.IndexOf(" · ", StringComparison.Ordinal) + 3)..];
        Assert.Equal(FakeLocalization.English()["History_Summary_NoBranches"], after);
        Assert.False(char.IsUpper(after[0]), $"'{vm.Summary}' starts a new sentence after the dot");
    }

    [Fact]
    public void It_follows_the_history_while_open_and_lets_go_when_the_host_disposes_it()
    {
        var history = new HistoryService(new FakeAppLogger());
        var vm = Open(history);
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IHistoryScreen.Nodes)) raised++;
        };

        history.Do(new CountingAction());
        Assert.Equal(2, vm.Nodes.Count);
        Assert.Equal(1, raised);

        vm.Dispose();
        history.Do(new CountingAction());
        Assert.Equal(2, vm.Nodes.Count);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Close_answers_true_and_a_closed_dialog_stops_rebuilding()
    {
        var history = new HistoryService(new FakeAppLogger());
        var vm = Open(history);

        vm.CloseCommand.Execute(null);
        history.Do(new CountingAction());

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Single(vm.Nodes);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var vm = Open(new HistoryService(new FakeAppLogger()));
        Assert.Empty(ContractConformanceTests.Violations(typeof(IHistoryScreen), vm));
    }

    private sealed class CountingAction : IChartAction
    {
        public int UndoCount;

        public void Execute()
        {
        }

        public void Undo() => UndoCount++;
    }
}
