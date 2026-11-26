using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #134: the Inspector is a third of the editor's bottom dock, so a 1280 px window gives it
///     ~400 px — and its header broke there. The work-queue bar's filter <c>ComboBox</c> was
///     arranged at its 150 px <c>MinWidth</c> in a narrower cell and overhung it to the LEFT
///     ("ny sound (240)"), and the eight-tab strip wrapped to three rows (the strip's cases are
///     <see cref="InspectorTabStripUiPerfSmokeTests" /> since #573). Since #601 the queue is the
///     Sound tab's ONE slim navigation row (the selection, its place, the two steps, the filter
///     behind a funnel); every part of it must stay inside the row at these widths too.
///     <para>
///         The app's own <see cref="InspectorViewModel" /> over the UI sweep's seeded song, laid
///         out at the pane widths a 1280 px window and the dock's <c>MinWidth</c> give it. Width-
///         shaped, so this class is in the release's de-DE / ja-JP layout filter.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class InspectorNarrowWidthUiPerfSmokeTests
{
    private const double PaneHeight = 300;

    [Theory]
    [InlineData(400)]
    [InlineData(320)]
    public async Task TheNavigationRow_KeepsEveryPartInside(int width) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, view =>
        {
            var sound = view.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            // the navigation half of the pinned footer (inspector design D, #751)
            var row = sound.GetVisualDescendants().OfType<StackPanel>().Single(b => b.Name == "NoteFooterNav");
            var parts = row.Children.Where(c => c.IsVisible).ToList();
            Assert.True(parts.Count >= 5, $"the navigation row holds {parts.Count} visible parts — repoint this test");
            Assert.All(parts, part => Assert.True(part.Bounds is { Width: > 1, Height: > 1 },
                $"a navigation-row part ({part.GetType().Name}) laid out as {part.Bounds} at {width} px"));

            var filter = sound.GetVisualDescendants().OfType<Button>().First(c => c.Name == "NoteFooterFilter");
            var (filterLeft, filterRight) = NarrowWidthLayout.Span(filter, row);
            Console.WriteLine($"[narrow-width] navigation row @{width}: row {row.Bounds.Width:0.#}x{row.Bounds.Height:0.#}, " +
                              $"filter {filterLeft:0.#}..{filterRight:0.#}; parts " +
                              string.Join(", ", parts.Select(p => $"{p.GetType().Name} {p.Bounds.X:0.#}..{p.Bounds.Right:0.#}")));

            Assert.True(filterLeft >= -0.5,
                $"at {width} px the queue filter starts at {filterLeft:0.#} px, LEFT of its row — it is clipped from the left (#134).");
            Assert.All(parts, part =>
            {
                var (left, right) = NarrowWidthLayout.Span(part, row);
                Assert.True(left >= -0.5 && right <= row.Bounds.Width + 0.5,
                    $"at {width} px a navigation-row part ({part.GetType().Name}) spans {left:0.#}..{right:0.#} px in a " +
                    $"{row.Bounds.Width:0.#} px row (#134).");
            });
        }), CancellationToken.None);

    /// <summary>
    ///     #964: the Chord techniques card (#957) at the narrowest inspector widths — its four label +
    ///     segmented rows and the Accent / Tremolo pair stay inside the card, a row's choices clear its
    ///     label, and no text is cut off. Seeded with a templated chord from the fixture, culture-swept.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(320)]
    public async Task TheChordTechniquesCard_KeepsEveryControlInsideItsCard(int width) =>
        await HeadlessSessionFixture.Dispatch(() => NoteSectionStripUiPerfSmokeTests.WithPane(width, 524, pane =>
        {
            NoteSectionStripUiPerfSmokeTests.Select(pane, NoteSectionStripUiPerfSmokeTests.MostUsedShapeChord(pane));
            NoteSectionStripUiPerfSmokeTests.Open(pane, SoundSection.Technique);
            pane.Vm.ChordTemplateEditor.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
            NarrowWidthLayout.Settle(pane.Window);
            var sound = NoteSectionStripUiPerfSmokeTests.Sound(pane);
            var card = NarrowWidthLayout.Named<ColumnFlowCard>(sound, "NoteChordAllStringsCard");
            NarrowWidthLayout.Named<ColumnFlowPanel>(sound, "NoteCards").ShowPage(card);
            NarrowWidthLayout.Settle(pane.Window);
            NarrowWidthLayout.Capture(pane.Window, $"chord-techniques-{width}");

            var group = NarrowWidthLayout.Named<StackPanel>(sound, "ChordAllTechniqueGroup");
            var (groupLeft, groupRight) = NarrowWidthLayout.Span(group, pane.Window);
            var offenders = new List<string>();
            foreach (var row in new[] { "ChordAllMuteRow", "ChordAllHopoRow", "ChordAllHarmonicRow", "ChordAllPickingRow" })
            {
                var choices = NarrowWidthLayout.Named<WrapPanel>(sound, row);
                var label = choices.GetVisualParent()!.GetVisualChildren().OfType<TextBlock>()
                    .Single(t => Grid.GetRow(t) == Grid.GetRow(choices));
                var (labelLeft, labelRight) = NarrowWidthLayout.Span(label, pane.Window);
                NarrowWidthLayout.Report($"[narrow-width] chord card @{width} {row}: label {labelLeft:0.#}..{labelRight:0.#} " +
                                         $"'{label.Text}' needs {NarrowWidthLayout.NaturalWidth(label):0.#}, group {groupLeft:0.#}..{groupRight:0.#}");
                if (NarrowWidthLayout.NaturalWidth(label) > label.Bounds.Width + 1)
                {
                    offenders.Add($"{row}: label '{label.Text}' needs {NarrowWidthLayout.NaturalWidth(label):0.#} px of {label.Bounds.Width:0.#}");
                }

                foreach (var choice in choices.Children.OfType<ToggleButton>())
                {
                    var (left, right) = NarrowWidthLayout.Span(choice, pane.Window);
                    if (left < labelRight - 0.5) offenders.Add($"{row}: a choice starts at {left:0.#}, inside its label (ends {labelRight:0.#})");
                    if (right > groupRight + 0.5) offenders.Add($"{row}: a choice ends at {right:0.#}, past the card edge {groupRight:0.#}");
                }
            }

            foreach (var name in new[] { "ChordAccentChip", "ChordAllTremoloToggle" })
            {
                var toggle = NarrowWidthLayout.Named<ToggleButton>(sound, name);
                var (left, right) = NarrowWidthLayout.Span(toggle, pane.Window);
                var label = toggle.Content as string ?? string.Empty;
                var needed = NarrowWidthLayout.NaturalWidth(label, toggle);
                var slot = toggle.Bounds.Width - toggle.Padding.Left - toggle.Padding.Right;
                NarrowWidthLayout.Report($"[narrow-width] chord card @{width} {name}: {left:0.#}..{right:0.#}, text needs {needed:0.#} of {slot:0.#}");
                if (left < groupLeft - 0.5 || right > groupRight + 0.5) offenders.Add($"{name} spans {left:0.#}..{right:0.#} outside {groupLeft:0.#}..{groupRight:0.#}");
                if (needed > slot + 1 && ToolTip.GetTip(toggle) is null) offenders.Add($"{name}: '{label}' is cut ({needed:0.#} > {slot:0.#}) with no tooltip");
            }

            Assert.True(offenders.Count == 0,
                $"the Chord techniques card at {width} px ({HeadlessSessionFixture.SweptCulture}): {string.Join("; ", offenders)} (#964).");
        }), CancellationToken.None);

    // The tab strip's own cases (#235's one row, #573's all-or-nothing rule, More) are
    // Perf/InspectorTabStripUiPerfSmokeTests, which lays this same pane out through WithPane.

    internal static void WithPane(int width, Action<InspectorView> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();
        var vm = services.GetRequiredService<InspectorViewModel>();
        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        var previousTab = vm.SelectedTabIndex;
        Window? window = null;
        try
        {
            NarrowWidthLayout.SeedSong(state);
            selection.ClearSelections();
            vm.SelectedTabIndex = 0;
            vm.Issues.RefreshIssues();

            var view = new InspectorView { DataContext = vm };
            window = NarrowWidthLayout.NewWindow(width, PaneHeight, view);
            window.Show();
            NarrowWidthLayout.Settle(window);
            HeadlessIssuePass.Settle(vm.Issues);
            Assert.Contains(vm.Issues.Items, issue => issue.Type == ArrangementIssueType.NoteOutsideAnchorRange);
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, $"inspector-{width}");
            body(view);
        }
        finally
        {
            vm.SelectedTabIndex = previousTab;
            window?.Close();
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
        }
    }
}
