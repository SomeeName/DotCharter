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
        var vm = new PhrasesInspectorViewModel(() => arrangement, () => Beats, seeks.Add, propagation, FakeLocalization.English(), new HistoryService(new FakeAppLogger()));
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
        var vm = new PhrasesInspectorViewModel(() => arrangement, () => Beats, _ => { }, null, FakeLocalization.English(), new HistoryService(new FakeAppLogger()));
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

    /// <summary>A chart placed in the editor (or imported from EOF) keeps its phrases as markers
    ///     only, and the browser read "No phrase iterations" for it.</summary>
    [Fact]
    public void AMarkersOnlyArrangement_ListsItsPhrases()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        foreach (var (beat, name) in new[] { (0, "verse"), (8, "verse_b"), (16, "verse") })
            arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = name });

        var (vm, _, _) = Build(arrangement);

        Assert.True(vm.HasPhrases);
        Assert.Equal(["verse", "verse_b"], vm.Phrases.Select(p => p.Name).ToArray());
        Assert.Equal(2, vm.Phrases[0].Iterations.Count);
        Assert.True(vm.Phrases[0].CanPropagate);
    }

    // ── the one Phrases panel: rename, delete, export note ─────────────────

    private static Arrangement MarkersOnly()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        foreach (var (beat, name) in new[] { (0, "verse"), (8, "chorus"), (16, "verse") })
            arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = name });
        return arrangement;
    }

    private static (PhrasesInspectorViewModel Vm, HistoryService History) BuildEditable(Arrangement arrangement)
    {
        var history = new HistoryService(new FakeAppLogger());
        var vm = new PhrasesInspectorViewModel(() => arrangement, () => Beats, _ => { }, null, FakeLocalization.English(), history);
        vm.Refresh();
        return (vm, history);
    }

    /// <summary>Apply shows only once the text differs from the name; Esc puts the name back.</summary>
    [Fact]
    public void TheRenameBox_IsDirtyOnlyWhileTheTextDiffers_AndCancelPutsTheNameBack()
    {
        var (vm, _) = BuildEditable(MarkersOnly());
        var verse = vm.Phrases[0];

        Assert.False(verse.IsRenameDirty);
        verse.EditName = "verse ";
        Assert.False(verse.IsRenameDirty);
        verse.EditName = "A";
        Assert.True(verse.IsRenameDirty);

        vm.CancelRenameCommand.Execute(verse);

        Assert.Equal("verse", verse.EditName);
        Assert.False(verse.IsRenameDirty);
    }

    [Fact]
    public void Rename_RenamesThePhrase_AsOneUndoNode_AndTheRowsFollowTheUndo()
    {
        var arrangement = MarkersOnly();
        var (vm, history) = BuildEditable(arrangement);
        var verse = vm.Phrases[0];

        verse.EditName = "A";
        vm.RenamePhraseCommand.Execute(verse);

        Assert.Equal(["A", "chorus"], vm.Phrases.Select(p => p.Name));
        Assert.Equal("Renamed “verse” to “A”.", vm.StatusLine);

        history.Undo();

        Assert.Equal(["verse", "chorus"], vm.Phrases.Select(p => p.Name));
    }

    [Fact]
    public void Delete_RemovesEveryIteration_NamesItsEffect_AndUndoBringsItBack()
    {
        var arrangement = MarkersOnly();
        var (vm, history) = BuildEditable(arrangement);
        var verse = vm.Phrases[0];
        Assert.Equal("Delete “verse”: removes all 2 of its iterations. Ctrl+Z undoes it.", verse.DeleteTip);

        vm.DeletePhraseCommand.Execute(verse);

        Assert.Equal(["chorus"], vm.Phrases.Select(p => p.Name));
        Assert.Equal("Deleted “verse” (2 iterations). Ctrl+Z brings them back.", vm.StatusLine);

        history.Undo();

        Assert.Equal(["verse", "chorus"], vm.Phrases.Select(p => p.Name));
    }

    /// <summary>Explicit iterations, markers, and both at once: the panel lists the union.</summary>
    [Fact]
    public void ThePanel_ListsEveryStore()
    {
        var mixed = BuildArrangement();
        mixed.EventPoints.Add(new EventPoint { Position = new FractionalPosition(24), Phrase = "outro" });

        Assert.Equal(["verse", "chorus"], Build(BuildArrangement()).Vm.Phrases.Select(p => p.Name));
        Assert.Equal(["verse", "chorus"], Build(MarkersOnly()).Vm.Phrases.Select(p => p.Name));
        Assert.Equal(["verse", "chorus", "outro"], Build(mixed).Vm.Phrases.Select(p => p.Name));
    }

    /// <summary>A markers-only chart is told what its export adds; an imported one carries its own.</summary>
    [Fact]
    public void TheExportNote_SaysWhatAnExportAdds()
    {
        Assert.Equal("On export: COUNT is added at the start and END after the last note.", Build(MarkersOnly()).Vm.ExportNote);
        Assert.Equal("", Build(BuildArrangement()).Vm.ExportNote);
    }
}
