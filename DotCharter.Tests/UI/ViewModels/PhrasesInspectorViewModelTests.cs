using DotCharter.Services.Editing.Phrases;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels;

/// <summary>
///     v0.29 phrase-iteration browser: grouping/ordering from the arrangement,
///     the deliberate-seek jump, and the two-step destructive propagate (arm →
///     execute; re-arm elsewhere or refresh disarms). All seams injected — no
///     chart editor, no Avalonia session.
/// </summary>
public sealed class PhrasesInspectorViewModelTests
{
    private static readonly List<BeatMarker> Beats =
        Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0 }).ToList();

    private static Arrangement BuildArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Phrases.Add("verse");
        arrangement.Phrases.Add("chorus");
        // chorus first in the list but verse plays first — ordering must be
        // by TIME, not by insertion or phrase id.
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8, new Fraction(0, 1)), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0, new Fraction(0, 1)), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(16, new Fraction(0, 1)), 1));
        return arrangement;
    }

    private sealed class FakePropagation : IPhraseIterationPropagationService
    {
        public PhraseIteration? LastSource;
        public int Calls;

        public int PropagateToAllIterations(PhraseIteration source)
        {
            Calls++;
            LastSource = source;
            return 2;
        }
    }

    private static (PhrasesInspectorViewModel Vm, FakePropagation Propagation, List<double> Seeks) Build(
        Arrangement? arrangement)
    {
        var propagation = new FakePropagation();
        var seeks = new List<double>();
        var vm = new PhrasesInspectorViewModel(() => arrangement, () => Beats, seeks.Add, propagation, FakeLocalization.English());
        vm.Refresh();
        return (vm, propagation, seeks);
    }

    [Fact]
    public void Rows_GroupPerPhrase_TimeOrdered_WithOrdinals()
    {
        var (vm, _, _) = Build(BuildArrangement());

        Assert.True(vm.HasPhrases);
        Assert.Equal(2, vm.Phrases.Count);
        Assert.Equal("verse", vm.Phrases[0].Name);  // plays first
        Assert.Equal("chorus", vm.Phrases[1].Name);

        var chorus = vm.Phrases[1];
        Assert.Equal(2, chorus.Iterations.Count);
        Assert.True(chorus.CanPropagate);
        Assert.Equal(1, chorus.Iterations[0].Ordinal);
        Assert.Equal(4000, chorus.Iterations[0].TimeMs, 3); // beat 8 × 500 ms
        Assert.Equal(8000, chorus.Iterations[1].TimeMs, 3);

        Assert.False(vm.Phrases[0].CanPropagate); // a single iteration propagates nowhere
    }

    /// <summary>A phrase played once has nothing to propagate to: its row offered Propagate
    /// anyway, and the click armed "propagate to 0 others".</summary>
    [Fact]
    public void ALonePhrasesRow_OffersNoPropagate_AndIgnoresTheCommand()
    {
        var (vm, propagation, _) = Build(BuildArrangement());
        var lone = vm.Phrases[0].Iterations[0];

        Assert.False(lone.CanPropagate);
        Assert.All(vm.Phrases[1].Iterations, r => Assert.True(r.CanPropagate));

        vm.PropagateCommand.Execute(lone);
        vm.PropagateCommand.Execute(lone);
        Assert.False(lone.IsConfirming);
        Assert.Equal(0, propagation.Calls);
    }

    [Fact]
    public void NoArrangement_IsEmpty()
    {
        var (vm, _, _) = Build(null);
        Assert.False(vm.HasPhrases);
        Assert.Empty(vm.Phrases);
    }

    [Fact]
    public void JumpTo_SeeksTheRowTime()
    {
        var (vm, _, seeks) = Build(BuildArrangement());

        vm.JumpToCommand.Execute(vm.Phrases[1].Iterations[1]);

        Assert.Equal([8000.0], seeks);
    }

    [Fact]
    public void Propagate_IsTwoStep_ArmThenExecute()
    {
        var (vm, propagation, _) = Build(BuildArrangement());
        var row = vm.Phrases[1].Iterations[0];

        vm.PropagateCommand.Execute(row);
        Assert.True(row.IsConfirming);
        Assert.Equal(0, propagation.Calls); // armed, not executed
        Assert.False(string.IsNullOrEmpty(vm.StatusLine));

        vm.PropagateCommand.Execute(row);
        Assert.Equal(1, propagation.Calls);
        Assert.Same(row.Iteration, propagation.LastSource);
        Assert.Contains("2", vm.StatusLine); // the rewritten count surfaces
    }

    [Fact]
    public void ArmingAnotherRow_DisarmsTheFirst()
    {
        var (vm, propagation, _) = Build(BuildArrangement());
        var first = vm.Phrases[1].Iterations[0];
        var second = vm.Phrases[1].Iterations[1];

        vm.PropagateCommand.Execute(first);
        vm.PropagateCommand.Execute(second);

        Assert.False(first.IsConfirming);
        Assert.True(second.IsConfirming);
        Assert.Equal(0, propagation.Calls);
    }

    [Fact]
    public void Refresh_Disarms_AndClearsTheStatus()
    {
        var (vm, propagation, _) = Build(BuildArrangement());
        vm.PropagateCommand.Execute(vm.Phrases[1].Iterations[0]);

        vm.Refresh();

        Assert.Equal("", vm.StatusLine);
        // The re-armed click on a FRESH row must arm again, not execute.
        vm.PropagateCommand.Execute(vm.Phrases[1].Iterations[0]);
        Assert.Equal(0, propagation.Calls);
    }

    /// <summary>
    ///     #141: switching to the Phrases tab used to clear and re-add every card, so
    ///     each switch re-templated and laid out the whole un-virtualized list. An
    ///     unchanged arrangement now keeps its rows (the same instances, nothing for
    ///     the view to rebuild); a moved phrase rebuilds them.
    /// </summary>
    [Fact]
    public void Activation_KeepsTheRows_UntilThePhrasesMove()
    {
        var arrangement = BuildArrangement();
        var vm = new PhrasesInspectorViewModel(() => arrangement, () => Beats, _ => { }, null, FakeLocalization.English());
        vm.EnsureCurrent();
        var first = vm.Phrases.ToArray();
        Assert.NotEmpty(first);

        vm.EnsureCurrent();
        // The SAME instances (rows are records, so Equal would pass a rebuild too).
        Assert.True(first.Zip(vm.Phrases).All(p => ReferenceEquals(p.First, p.Second)), "an unchanged activation rebuilt the rows");

        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(24, new Fraction(0, 1)), 0));
        vm.EnsureCurrent();
        Assert.NotSame(first[0], vm.Phrases[0]);
        Assert.Contains(vm.Phrases, g => g.Iterations.Count == 2 && g.Name == "verse");

        arrangement.Phrases[1] = "bridge";   // a rename moves the rows too
        vm.EnsureCurrent();
        Assert.Contains(vm.Phrases, g => g.Name == "bridge");
    }
}
