using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

public sealed class SelectionEditsViewModelTests
{
    private static (SelectionEditsViewModel Vm, ChartEditHarness Harness, RecordingProxy Global) Build()
    {
        var h = new ChartEditHarness();
        var (global, recorder) = RecordingProxy.Create<IGlobalEditingCoordinator>();
        var (highway, _) = RecordingProxy.Create<IHighwayEditingService>();
        var vm = new SelectionEditsViewModel(global, highway, h.Editor.Selection, h.State,
            FakeLocalization.English(), new MatchPatternStore(new FakeConfigurationService()));
        return (vm, h, recorder);
    }

    [Fact]
    public void A_toggle_runs_the_verb_and_says_what_the_selected_notes_carry_afterwards()
    {
        var (vm, h, _) = Build();
        var notes = Enumerable.Range(0, 3).Select(_ => new GuitarNote()).ToList();
        foreach (var n in notes) h.Editor.Selection.SelectedNotes.Add(n);

        vm.Toggle(TechniqueSpec.Hopo, _ => notes.ForEach(n => n.Hopo = HopoType.HammerOn));

        Assert.Equal("Hammer-on set on 3 items", h.State.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Info, h.State.MainDisplaySeverity);
    }

    [Fact]
    public void A_toggle_with_nothing_selected_warns_instead_of_claiming_a_change()
    {
        var (vm, h, _) = Build();

        vm.Toggle(TechniqueSpec.Vibrato, _ => { });

        Assert.Equal("Select notes or chords first.", h.State.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, h.State.MainDisplaySeverity);
    }

    [Fact]
    public void Select_similar_reports_the_coordinators_answer_on_the_status_rail()
    {
        var (vm, h, global) = Build();

        vm.SelectSimilar();

        Assert.Contains("SelectLikePattern()", global.Calls);
        Assert.Contains("Select a note", h.State.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, h.State.MainDisplaySeverity);
    }
}
