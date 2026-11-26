using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;
using Microsoft.Extensions.DependencyInjection;
using static DotCharter.Tests.Perf.NoteSectionStripUiPerfSmokeTests;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #953 through the real view: a chord's Technique page carries the "Chord techniques" card
///     (<c>SoundChordAllStringsView.axaml</c>), constructed, attached and laid out with every row measured
///     non-zero; one click on its Full mute mutes every string of the chord in one undo step, and undo restores.
///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> it writes PNGs to <c>TestResults/note-section-strip/</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChordAllStringsTechniqueUiPerfSmokeTests
{
    [Fact]
    public async Task TheChordTechniquesCard_LaysOut_AndOneClickMutesEveryString_InOneUndo() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(600, 524, pane =>
        {
            var history = App.Services!.GetRequiredService<IHistoryService>();
            var chord = MostUsedShapeChord(pane);
            Select(pane, chord);
            Open(pane, SoundSection.Technique);
            pane.Vm.ChordTemplateEditor.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
            Settle(pane.Window);
            var sound = Sound(pane);
            Named<ColumnFlowPanel>(sound, "NoteCards").ShowPage(Named<ColumnFlowCard>(sound, "NoteChordAllStringsCard"));
            Settle(pane.Window);

            foreach (var name in new[] { "ChordAllTechniqueRows", "ChordAllMuteRow", "ChordAllHopoRow", "ChordAllHarmonicRow", "ChordAllPickingRow" })
            {
                var part = Named<Control>(sound, name);
                Assert.True(part.IsEffectivelyVisible && part.Bounds is { Width: > 0, Height: > 0 }, $"'{name}' is not laid out");
            }

            Assert.True(Named<ToggleButton>(sound, "ChordAllTremoloToggle").Bounds.Height > 0, "the tremolo toggle has no size");
            Capture(pane, "chord-all-strings-before-600");

            var steps = history.GetUndoSteps().Count;
            var before = chord.ChordNotes.Select(n => n.Mute).ToList();
            var fullMute = Named<WrapPanel>(sound, "ChordAllMuteRow").Children.OfType<ToggleButton>()
                .Single(b => Equals(b.CommandParameter, "1"));
            fullMute.Command!.Execute(fullMute.CommandParameter);
            Settle(pane.Window);
            var apply = Named<Button>(sound, "NoteFooterApplyButton");
            if (pane.Vm.Staged.Engine.ApplyStep) ApplyStaged(pane, apply);

            Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.Full, n.Mute));
            Assert.Equal(steps + 1, history.GetUndoSteps().Count);
            Assert.True(fullMute.IsChecked, "the row does not light Full mute after the click");
            Capture(pane, "chord-all-strings-after-600");

            history.Undo();
            Settle(pane.Window);
            Assert.Equal(before, chord.ChordNotes.Select(n => n.Mute).ToList());
        }), CancellationToken.None);
}
