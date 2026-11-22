using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf.InspectorCapture;

/// <summary>
///     #601's review captures: the real <see cref="InspectorView" /> over the seeded project of
///     <see cref="InspectorCaptureFixture" />, every tab and every Sound-tab section, at the
///     default 600 x 524 Inspector and at 380 px (or the sizes in <c>DOTCHARTER_INSPECTOR_CAPTURE_SIZES</c>,
///     e.g. <c>530x370,480x370</c>). In en-US every tab is written; in another
///     culture (<c>DOTCHARTER_HEADLESS_CULTURE</c>) the Sound tab only. Opt-in:
///     <c>DOTCHARTER_INSPECTOR_CAPTURE=1</c> and <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>; the PNGs land in
///     <c>DOTCHARTER_INSPECTOR_CAPTURE_OUT</c> under <c>after/&lt;culture&gt;/&lt;width&gt;/</c>
///     (<c>&lt;width&gt;x&lt;height&gt;</c> for a size that is not the default height).
/// </summary>
[Collection("HeadlessUI")]
public sealed class InspectorCaptureUiPerfSmokeTests
{
    [Fact]
    public async Task CaptureTheInspector_EveryTabAndSoundSection() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            Assert.SkipUnless(InspectorCaptureFixture.Enabled, "review capture; set DOTCHARTER_INSPECTOR_CAPTURE=1 and DOTCHARTER_UI_SWEEP_DUMP=1");
            var services = InspectorCaptureFixture.Services;
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var vm = InspectorCaptureFixture.Inspector;
            var (oldProject, oldArrangement) = (state.ActiveProject, state.ActiveArrangement);
            var level = InspectorCaptureFixture.Seed(state);
            InspectorCaptureFixture.SeedApp(vm, state.ActiveArrangement!, level);
            vm.Issues.RefreshIssues();
            HeadlessIssuePass.Settle(vm.Issues);
            var culture = HeadlessSessionFixture.SweptCulture;
            try
            {
                foreach (var (width, height) in InspectorCaptureFixture.Sizes)
                {
                    var view = new InspectorView { DataContext = vm };
                    var window = InspectorCaptureFixture.NewWindow(width, height, view);
                    window.Show();
                    try
                    {
                        var dir = Path.Combine("after", culture, InspectorCaptureFixture.SizeFolder(width, height));
                        foreach (var (name, tab, prepare) in States(vm, state, level))
                        {
                            if (culture != "en-US" && tab != 0) continue;
                            selection.ClearSelections();
                            state.ClearStringEditMask();
                            vm.SelectedTabIndex = tab;
                            prepare(selection);
                            InspectorCaptureFixture.Settle(window);
                            Assert.True(vm.Staged.Footer.Count == 0,
                                $"{name}: showing it staged an edit ({vm.Staged.Engine.Placement}, {vm.Staged.Engine.Bulk})");
                            InspectorCaptureFixture.Save(window, dir, name);
                        }
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                selection.ClearSelections();
                state.ClearStringEditMask();
                vm.SelectedTabIndex = 0;
                state.ActiveProject = oldProject;
                state.ActiveArrangement = oldArrangement;
            }
        }, CancellationToken.None);

    private static IEnumerable<(string Name, int Tab, Action<ISelectionService> Prepare)> States(InspectorViewModel vm, ProjectState state,
        ChartLevel level)
    {
        var note = level.Notes.First(n => n.BendValues.Count == 0 && n.Fret > 2);
        var chord = level.Chords.First(c => c.TemplateId >= 0 && vm.Issues.Items.Any(i => ReferenceEquals(i.Chord, c)));
        yield return ("sound-note-placement", 0, s => Open(vm, () => s.SelectedNotes.Add(note), SoundSection.Placement));
        yield return ("sound-note-technique", 0, s => Open(vm, () => s.SelectedNotes.Add(note), SoundSection.Technique));
        yield return ("sound-note-bends", 0, s => Open(vm, () => s.SelectedNotes.Add(note), SoundSection.Bends));
        yield return ("sound-chord-shape", 0, s => Open(vm, () => s.SelectedChords.Add(chord), SoundSection.Shape));
        yield return ("sound-chord-technique", 0, s => Open(vm, () => s.SelectedChords.Add(chord), SoundSection.Technique));
        yield return ("sound-chord-technique-masked", 0, s => Open(vm, () => SelectMasked(s, state, chord), SoundSection.Technique));
        yield return ("sound-chord-bends", 0, s => Open(vm, () => s.SelectedChords.Add(chord), SoundSection.Bends));
        yield return ("sound-chord-findings", 0, s => Open(vm, () => s.SelectedChords.Add(chord), SoundSection.Findings));
        yield return ("sound-multi-selection", 0, s => Open(vm, () => SelectTwo(s, note, level.Notes.First(n => !ReferenceEquals(n, note))),
            SoundSection.Selection));
        yield return ("sound-nothing-task", 0, _ => Open(vm, () => { }, SoundSection.Task));
        yield return ("sound-nothing-library", 0, _ => Open(vm, () => { }, SoundSection.Library));
        yield return ("mixer", 1, _ => vm.MixerSections.ChooseCommand.Execute(MixerSection.Eq));
        yield return ("mixer-midi", 1, _ => vm.MixerSections.ChooseCommand.Execute(MixerSection.Midi));
        yield return ("song", 2, _ => { });
        yield return ("lyrics", 3, _ => { });
        yield return ("issues", 4, _ => vm.IssuesSections.Open(IssuesSection.Findings));
        yield return ("issues-project", 4, _ => vm.IssuesSections.Open(IssuesSection.Project));
        yield return ("issues-review", 4, _ => vm.IssuesSections.Open(IssuesSection.EditorOnFire));
        yield return ("annotations", 5, _ => { });
        yield return ("riffs", InspectorViewModel.RiffLibraryTabIndex, _ => { });
        yield return ("chord-shapes", InspectorViewModel.ChordShapesTabIndex, _ => { });
    }

    private static void Open(InspectorViewModel vm, Action select, SoundSection section)
    {
        select();
        vm.Sections.ChooseCommand.Execute(section);
    }

    /// <summary>The chord with its lowest string alone in the string-edit mask (its chip lit).</summary>
    private static void SelectMasked(ISelectionService selection, ProjectState state, Chord chord)
    {
        selection.SelectedChords.Add(chord);
        state.ToggleStringEditMask(chord.ChordNotes.Min(n => n.StringIndex));
    }

    private static void SelectTwo(ISelectionService selection, GuitarNote first, GuitarNote second)
    {
        selection.SelectedNotes.Add(first);
        selection.SelectedNotes.Add(second);
    }
}
