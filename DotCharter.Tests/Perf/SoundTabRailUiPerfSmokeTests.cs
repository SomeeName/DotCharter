using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Panels;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #601: the Sound tab as the owner chose it, "Rail (dense)". The app's own
///     <see cref="InspectorViewModel" /> over the UI sweep's 240-sound stress chart (its chords bound
///     to real templates, so the "Apply to" ladder has uses to count), laid out in the REAL
///     <see cref="InspectorView" /> at the default Inspector size — 600 x 524, a third of a 1920 px
///     window's editor split — and at the 560 / 420 / 380 / 320 px a narrower window or the dock's
///     floor gives it.
///     <para>
///         What it pins: an icon-only rail that names every entry twice off one key, offers no
///         Playback section (the whole-chord flags are a group in Technique), pins ONE "Apply to"
///         row over a chord's Shape, Technique and Bends only (#604), and keeps the slim navigation
///         row over every section; the counter follows a clicked sound (#611); the picker shows the ladder's live
///         rung; a string chip masks a technique to ONE string; and every edit goes through the
///         shipped commands (the #581 ladder reach, one undo step) — the bend editor and the
///         keyboard's technique verbs included (#604).
///     </para>
///     <para>
///         Width-shaped, so this class is in the release's de-DE / ja-JP layout filter
///         (<c>$HeadlessLayoutFilter</c>). With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> each case writes
///         PNGs to <c>TestResults/sound-rail/</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SoundTabRailUiPerfSmokeTests
{
    private const double DefaultWidth = 600;
    private const double DefaultHeight = 524;

    /// <summary>The six techniques a chord is given most, by name in <c>SoundChordTechniqueView.axaml</c>.</summary>
    private static readonly string[] CommonTechniques =
        ["ChordFullMute", "ChordPalmMute", "ChordAccentChip", "ChordSlideChip", "ChordVibratoChip", "ChordLinkNextChip"];

    private static string ArtifactDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestResults", "sound-rail");

    /// <summary>
    ///     The rail itself, for a chord: 38 px of icons, every entry named and tipped off ONE key and
    ///     showing no caption; Shape · Technique · Bends · Findings and no Playback; the open entry
    ///     lit with its accent bar; the Findings entry counting the selection's findings.
    /// </summary>
    [Fact]
    public async Task TheRail_IsIconsOnly_NamedTwice_AndOffersNoPlayback() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var chord = FindingChord(pane);
            Select(pane, chord);
            var sound = Sound(pane);

            var rail = Named<Border>(sound, "SoundRail");
            Assert.InRange(rail.Bounds.Width, 30, 44);
            var entries = Named<StackPanel>(sound, "SoundRailEntries").Children.OfType<Panel>().ToList();
            Assert.Equal(Enum.GetValues<SoundSection>().Length, entries.Count);
            foreach (var entry in entries)
            {
                var toggle = entry.Children.OfType<ToggleButton>().Single();
                var name = AutomationProperties.GetName(toggle);
                Assert.False(string.IsNullOrWhiteSpace(name), $"rail entry {toggle.Name} has no accessible name");
                Assert.Equal(name, ToolTip.GetTip(toggle) as string);
                Assert.DoesNotContain(toggle.GetVisualDescendants(), d => d is TextBlock { IsEffectivelyVisible: true });
            }

            var shown = entries.Where(e => e.IsVisible).Select(e => e.Name!["SoundRail".Length..]).ToList();
            Assert.Equal(["Shape", "Technique", "Bends", "Findings"], shown);
            Assert.DoesNotContain(Enum.GetNames<SoundSection>(), n => n == "Playback");

            var shape = Named<ToggleButton>(sound, "SoundRailShapeEntry");
            Assert.True(shape.IsChecked, "the default section's entry is not lit");
            Assert.True(Named<Panel>(sound, "SoundRailShape").Children.OfType<Border>().Single().IsVisible, "no accent bar on the open entry");

            var badge = Named<Panel>(sound, "SoundRailFindingsBadge");
            Assert.True(badge.IsEffectivelyVisible, "the chord has findings but the Findings entry carries no count");
            Assert.Contains(badge.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == pane.Vm.SelectionIssueCount.ToString());

            Select(pane, pane.Level.Notes.First(n => n.BendValues.Count == 0));
            shown = [.. entries.Where(e => e.IsVisible).Select(e => e.Name!["SoundRail".Length..])];
            Assert.Equal(["Placement", "Technique", "Bends", "Findings"], shown);

            pane.Selection.ClearSelections();
            Settle(pane.Window);
            shown = [.. entries.Where(e => e.IsVisible).Select(e => e.Name!["SoundRail".Length..])];
            Assert.Equal(["Task", "Library"], shown);
            Assert.True(Named<Control>(sound, "SoundTaskBody").IsEffectivelyVisible, "nothing selected: the next task is hidden");
        }), CancellationToken.None);

    /// <summary>
    ///     At the default size, with the scroll offset at zero: over Shape the pinned "Apply to" row
    ///     and the chord box (the fingering editor) are whole on screen; over Technique the pinned
    ///     row (#581), the string chips and the six common toggles are, the ladder previews the
    ///     techniques' reach, and the slim navigation row is 28 px.
    /// </summary>
    [Fact]
    public async Task AtTheDefaultSize_TheApplyRowTheFingeringAndTheCommonTechniquesNeedNoScroll() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            Select(pane, FindingChord(pane));
            Open(pane, SoundSection.Shape);

            var sound = Sound(pane);
            var body = Named<ScrollViewer>(sound, "SoundSectionScroll");
            Assert.Equal(0, body.Offset.Y);
            var nav = Named<Border>(sound, "SoundNavRow");
            Assert.InRange(nav.Bounds.Height, 1, 28.5);
            var apply = Named<Border>(sound, "SoundApplyBar");
            var box = Named<SoundShapeView>(sound, "SoundShapeCard").GetVisualDescendants().OfType<ChordBoxControl>().Single();
            AssertWhollyOnScreen(pane, body, apply, "the pinned 'Apply to' row");
            AssertWhollyOnScreen(pane, body, box, "the chord box (fingering editor)");
            Assert.Equal(ChordApplyFamily.Shape, pane.Vm.ChordTemplateEditor.ApplyFamily);

            Open(pane, SoundSection.Technique);
            Assert.Equal(0, body.Offset.Y);
            AssertWhollyOnScreen(pane, body, Named<ItemsControl>(sound, "ChordStringChips"), "the string chips");
            foreach (var name in CommonTechniques)
                AssertWhollyOnScreen(pane, body, Named<ToggleButton>(sound, name), $"the common technique '{name}'");
            AssertWhollyOnScreen(pane, body, apply, "the pinned 'Apply to' row over the techniques");
            Assert.Equal(ChordApplyFamily.Technique, pane.Vm.ChordTemplateEditor.ApplyFamily);

            // #604: a chord's bend follows the rung, so the picker pins over Bends too, whole on screen,
            // and the ladder counts and previews what a bend reaches (the technique family).
            Open(pane, SoundSection.Bends);
            AssertWhollyOnScreen(pane, body, apply, "the pinned 'Apply to' row over the bends");
            AssertWhollyOnScreen(pane, body, Named<ComboBox>(sound, "SoundApplyPicker"), "the 'Apply to' picker over the bends");
            Assert.Equal(ChordApplyFamily.Technique, pane.Vm.ChordTemplateEditor.ApplyFamily);
        }), CancellationToken.None);

    /// <summary>
    ///     The ONE pinned "Apply to" row shows over a chord's Shape, Technique and Bends and nowhere else,
    ///     the navigation row over every section; the Whole chord group (what Playback held) edits
    ///     the chord; Bends draws the compact preset row and gives the graph the pane's width; and
    ///     no state raises a binding fault.
    /// </summary>
    [Fact]
    public async Task TheApplyRow_PinsOverShapeTechniqueAndBendsOnly_AndEverySectionBindsClean() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var sink = UiSweepLogSink.Attach();
            try
            {
                var chord = pane.Level.Chords.First(c => c.TemplateId >= 0 && c.ChordNotes.Count >= 2);
                Select(pane, chord);
                var sound = Sound(pane);
                var apply = Named<Border>(sound, "SoundApplyBar");
                var nav = Named<Border>(sound, "SoundNavRow");
                foreach (var section in SoundSectionsViewModel.For(SoundSelectionKind.Chord))
                {
                    Open(pane, section);
                    var pinned = section is SoundSection.Shape or SoundSection.Technique or SoundSection.Bends;
                    Assert.True(apply.IsEffectivelyVisible == pinned, $"the Apply-to row is {(pinned ? "hidden" : "shown")} over {section}");
                    Assert.True(nav.IsEffectivelyVisible, $"the navigation row left {section}");
                }

                Open(pane, SoundSection.Technique);
                pane.Vm.ChordTemplateEditor.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
                Settle(pane.Window);
                var whole = Named<WrapPanel>(sound, "ChordWholeChordGroup");
                Assert.True(whole.IsEffectivelyVisible && whole.Bounds.Height > 0, "the Whole chord group is not on screen");
                var ignore = whole.Children.OfType<ToggleButton>()
                    .Single(t => AutomationProperties.GetName(t) == LocalizationService.Instance["Inspector_Ignore"]);
                var before = chord.Ignore;
                ignore.IsChecked = !(ignore.IsChecked ?? false);
                Settle(pane.Window);
                Assert.True(chord.Ignore != before, "toggling Ignore in the Whole chord group did not reach the chord");
                ignore.IsChecked = !(ignore.IsChecked ?? false);
                Settle(pane.Window);
                Assert.Equal(before, chord.Ignore);

                Open(pane, SoundSection.Bends);
                var bend = Named<StackPanel>(sound, "ChordBendsBody").GetVisualDescendants().OfType<BendEditorControl>().Single();
                Assert.True(bend.FindControl<Control>("BendPresetRow")!.IsEffectivelyVisible, "Bends: the preset row is hidden");
                var graph = bend.FindControl<Border>("GraphBorder")!;
                Assert.True(graph.Bounds.Width > bend.Bounds.Width * 0.9, $"Bends: the graph is {graph.Bounds.Width:0} of {bend.Bounds.Width:0} px wide");

                foreach (var state in new object?[] { pane.Level.Notes.First(), null })
                {
                    if (state is null) pane.Selection.ClearSelections();
                    else Select(pane, state);
                    foreach (var section in pane.Vm.Sections.Sections) Open(pane, section);
                }

                Select(pane, pane.Level.Notes[0]);
                pane.Selection.SelectedChords.Add(chord);
                Settle(pane.Window);
                foreach (var section in pane.Vm.Sections.Sections) Open(pane, section);

                var faults = sink.Drain().Where(r => r.Area == "Binding" && !r.Message.EndsWith("Value is null.", StringComparison.Ordinal))
                    .Select(r => r.Message).Distinct().ToList();
                Assert.True(faults.Count == 0, "binding faults on the Sound tab:\n  " + string.Join("\n  ", faults));
            }
            finally
            {
                sink.Detach();
            }
        }), CancellationToken.None);

    /// <summary>
    ///     The owner's three findings on the test build, through the real view: the counter follows a
    ///     sound CLICKED in the chart and › steps on from it (#611); the pinned picker shows the
    ///     ladder's live rung, never the text it had when it was picked ("selected (0)"); and a
    ///     string chip sets the string-edit mask, so Vibrato lands on ONE string.
    /// </summary>
    [Fact]
    public async Task CounterFollowsClicks_ChipsMaskOneString_PickerStaysCurrent() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var vm = pane.Vm;
            try
            {
                vm.ChordTemplateEditor.ApplyRung = ChordApplyRung.Selected;
                pane.Selection.ClearSelections();
                Settle(pane.Window);
                var sound = Sound(pane);

                vm.Queue.SelectedFilter = vm.Queue.Filters.Single(f => f.Filter == SoundQueueFilter.AnySound);
                vm.Queue.NextMatch();
                vm.Queue.NextMatch();
                Settle(pane.Window);
                var position = Named<TextBlock>(sound, "SoundNavPosition");
                Assert.StartsWith("2 /", position.Text);
                var chord = pane.Level.Chords.First(c => c.TemplateId >= 0 && c.ChordNotes.Count >= 2 && c.ChordNotes.All(n => !n.Vibrato));
                Select(pane, chord);
                var expected = vm.Queue.Matches(SoundQueueFilter.AnySound).ToList().IndexOf(chord) + 1;
                Assert.StartsWith($"{expected} /", position.Text);
                Named<Button>(sound, "SoundNavNext").Command!.Execute(null);
                Settle(pane.Window);
                Assert.Same(vm.Queue.Matches(SoundQueueFilter.AnySound)[expected], pane.Selection.SelectedNote as object ?? pane.Selection.SelectedChord);
                Assert.StartsWith($"{expected + 1} /", position.Text);
                Select(pane, chord);

                Open(pane, SoundSection.Technique);
                var picker = Named<ComboBox>(sound, "SoundApplyPicker");
                var shown = string.Concat(picker.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
                var ladder = vm.ChordTemplateEditor.Ladder;
                var live = ladder.Rows.Single(r => r.Rung == ladder.Effective).Label;
                Assert.True(shown.Contains(live, StringComparison.Ordinal), $"the picker shows '{shown}', the ladder says '{live}'");
                Assert.DoesNotContain("(0)", shown, StringComparison.Ordinal);

                var chips = Named<ItemsControl>(sound, "ChordStringChips").GetVisualDescendants().OfType<ToggleButton>().ToList();
                Assert.Equal(chord.ChordNotes.Select(n => n.StringIndex).Distinct().Count(), chips.Count);
                var target = chord.ChordNotes.Min(n => n.StringIndex);
                var chip = chips.Single(c => (int)c.CommandParameter! == target);
                var gutterName = GuitarStringLabel.Format(target, vm.NoteStringCount, vm.NoteTuningNames,
                    LocalizationService.Instance["Inspector_StringLabelFmt"]);
                Assert.Equal(gutterName, AutomationProperties.GetName(chip));
                chip.Command!.Execute(chip.CommandParameter);
                Settle(pane.Window);
                Assert.True(vm.ProjectState.StringEditMask.IsExplicit(target), "the chip did not set the string-edit mask");
                Assert.True(chip.IsChecked, "the chip for the masked string is not lit");
                Assert.Contains(gutterName, vm.ChordStringScopeLabel, StringComparison.Ordinal);

                // A change the box makes on its own is never a pick: it must not move the rung (a
                // stray selection widened "selected" to "every use" when the view was re-attached).
                picker.SelectedItem = ladder.Rows.First(r => r.Rung == ChordApplyRung.EveryUse);
                Settle(pane.Window);
                Assert.Equal(ChordApplyRung.Selected, vm.ChordTemplateEditor.ApplyRung);
                Assert.Same(ladder.Rows.Single(r => r.Rung == ladder.Effective), picker.SelectedItem);

                // A user's pick — the keyboard on the focused box — takes the rung.
                Assert.True(picker.Focus(), "the picker took no focus");
                picker.SelectedItem = ladder.Rows.First(r => r.Rung == ChordApplyRung.ThisChord);
                Settle(pane.Window);
                Assert.Equal(ChordApplyRung.ThisChord, vm.ChordTemplateEditor.ApplyRung);
                var vibrato = Named<ToggleButton>(sound, "ChordVibratoChip");
                vibrato.IsChecked = true;
                Settle(pane.Window);
                Assert.Equal([target], chord.ChordNotes.Where(n => n.Vibrato).Select(n => n.StringIndex));
                vibrato.IsChecked = false;
                Settle(pane.Window);
                Assert.DoesNotContain(chord.ChordNotes, n => n.Vibrato);
            }
            finally
            {
                vm.ProjectState.ClearStringEditMask();
                vm.ChordTemplateEditor.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
            }
        }), CancellationToken.None);

    /// <summary>
    ///     Every state at every width: it lays out, it captures, and no text in the Sound tab is
    ///     clipped by its slot without a plan for not fitting (the UI sweep's rule: no wrapping,
    ///     no ellipsis, narrower than its text).
    /// </summary>
    [Theory]
    [InlineData(600)]
    [InlineData(560)]
    [InlineData(420)]
    [InlineData(380)]
    [InlineData(320)]
    public async Task EveryStateLaysOut_WithNoClippedText(int width) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, DefaultHeight, pane =>
        {
            var clipped = new List<string>();
            foreach (var section in pane.Vm.Sections.Sections)
            {
                Open(pane, section);
                AuditAndCapture(pane, $"empty-{section}-{width}", clipped);
            }

            Select(pane, pane.Level.Notes.First(n => n.BendValues.Count == 0));
            foreach (var section in pane.Vm.Sections.Sections)
            {
                Open(pane, section);
                AuditAndCapture(pane, $"note-{section}-{width}", clipped);
            }

            Select(pane, FindingChord(pane));
            foreach (var section in pane.Vm.Sections.Sections)
            {
                Open(pane, section);
                AuditAndCapture(pane, $"chord-{section}-{width}", clipped);
            }

            Assert.True(clipped.Count == 0, $"clipped text at {width} px:\n  " + string.Join("\n  ", clipped));
        }), CancellationToken.None);

    /// <summary>
    ///     The tab remembers the section PER SELECTION TYPE: chosen once, it survives a different
    ///     chord and a detour through a single note (which opens on its own default); and a click
    ///     on the lit entry keeps it lit.
    /// </summary>
    [Fact]
    public async Task TheChosenSection_SurvivesTheNextSelection() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var sound = Sound(pane);
            var technique = Named<ToggleButton>(sound, "SoundRailTechniqueEntry");
            Select(pane, pane.Level.Chords[0]);
            technique.Command!.Execute(technique.CommandParameter);
            Settle(pane.Window);
            Assert.True(technique.IsChecked);
            Assert.True(Named<Control>(sound, "SoundChordTechniqueCard").IsEffectivelyVisible);
            Assert.False(Named<Control>(sound, "SoundShapeCard").IsEffectivelyVisible);

            technique.IsChecked = false;
            technique.Command!.Execute(technique.CommandParameter);
            Settle(pane.Window);
            Assert.True(technique.IsChecked, "clicking the lit entry must leave it lit");

            Select(pane, pane.Level.Notes[0]);
            Assert.Equal(SoundSection.Placement, pane.Vm.Sections.Section);
            Assert.True(Named<Control>(sound, "SoundPlacementCard").IsEffectivelyVisible);

            Select(pane, pane.Level.Chords[1]);
            Assert.Equal(SoundSection.Technique, pane.Vm.Sections.Section);
            Assert.True(Named<Control>(sound, "SoundChordTechniqueCard").IsEffectivelyVisible,
                "the next chord opened on another section than the one the user chose");
        }), CancellationToken.None);

    /// <summary>
    ///     #581, through the real view: over the Technique section the pinned ladder takes the
    ///     technique edits. On "every use of this shape" a click on Palm mute marks every chord of the
    ///     shape before it commits (#485), then writes exactly those chords on every string, touches
    ///     no template, says so on the status rail and is ONE undo; the repeat line re-reads after it.
    /// </summary>
    [Fact]
    public async Task OverTheTechniques_EveryUse_PalmMutesEveryChordOfTheShape_InOneUndo() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var panel = pane.Vm.ChordTemplateEditor;
            var history = App.Services!.GetRequiredService<IHistoryService>();
            var chord = MostUsedShapeChord(pane);
            try
            {
                Select(pane, chord);
                Open(pane, SoundSection.Technique);
                panel.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
                Settle(pane.Window);
                Capture(pane, "chord-technique-every-use-600");

                var uses = pane.Level.Chords.Where(c => c.TemplateId == chord.TemplateId).ToList();
                Assert.True(uses.Count > 1, "the fixture shape has one use — 'every use' is the selection");
                Assert.True(panel.Preview.IsActive, "the uses the edit will change are not marked before it commits");
                Assert.True(Named<Grid>(Sound(pane), "SoundApplyPreview").IsEffectivelyVisible, "the pinned row does not show the preview");
                Assert.True(panel.Preview.Resolved.Chords.SetEquals(uses), "the marks are not the shape's uses");
                Assert.Equal(uses.Count, panel.Ladder.EveryUse.Reach.Chords);
                var others = Snapshot(pane.Level.Chords.Where(c => c.TemplateId != chord.TemplateId));
                var templates = pane.Vm.ProjectState.ActiveArrangement!.ChordTemplates.Select(t => t.Clone()).ToList();
                var steps = history.GetUndoSteps().Count;
                var usesBefore = Snapshot(uses);

                Named<ToggleButton>(Sound(pane), "ChordPalmMute").IsChecked = true;
                Settle(pane.Window);

                Assert.All(uses, c => Assert.All(c.ChordNotes, n => Assert.Equal(MuteType.Palm, n.Mute)));
                Assert.Equal(others, Snapshot(pane.Level.Chords.Where(c => c.TemplateId != chord.TemplateId)));
                Assert.Equal(templates, pane.Vm.ProjectState.ActiveArrangement!.ChordTemplates.ToList());
                Assert.Equal(steps + 1, history.GetUndoSteps().Count);
                Assert.Contains(uses.Count.ToString(), pane.Vm.ProjectState.MainDisplayMessage);
                Assert.Equal(ExpectedVerdict(pane, chord), pane.Vm.ChordRepeatVerdict);

                history.Undo();
                Settle(pane.Window);
                Assert.Equal(usesBefore, Snapshot(uses));
                Assert.Equal(ExpectedVerdict(pane, chord), pane.Vm.ChordRepeatVerdict);
            }
            finally
            {
                panel.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
            }
        }), CancellationToken.None);

    /// <summary>#581's regression half: on the plain "selected" rung the same click reaches the
    /// selection and nothing else.</summary>
    [Fact]
    public async Task OverTheTechniques_Selected_PalmMutesTheSelectionOnly() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var history = App.Services!.GetRequiredService<IHistoryService>();
            var chord = MostUsedShapeChord(pane);
            Select(pane, chord);
            Open(pane, SoundSection.Technique);
            pane.Vm.ChordTemplateEditor.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
            Settle(pane.Window);
            Assert.False(pane.Vm.ChordTemplateEditor.Preview.IsActive, "the selection itself needs no marks");
            var others = Snapshot(pane.Level.Chords.Where(c => !ReferenceEquals(c, chord)));
            var steps = history.GetUndoSteps().Count;
            var before = Snapshot([chord]);

            Named<ToggleButton>(Sound(pane), "ChordPalmMute").IsChecked = true;
            Settle(pane.Window);

            Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.Palm, n.Mute));
            Assert.Equal(others, Snapshot(pane.Level.Chords.Where(c => !ReferenceEquals(c, chord))));
            Assert.Equal(steps + 1, history.GetUndoSteps().Count);
            history.Undo();
            Settle(pane.Window);
            Assert.Equal(before, Snapshot([chord]));
        }), CancellationToken.None);

    /// <summary>
    ///     #604: the chord bend editor takes the pinned ladder like a technique. On "every use of this
    ///     shape" over Bends the shape's uses are marked before the commit; one preset through the real
    ///     editor bends the picked STRING of exactly those chords (no other string, no other chord, no
    ///     template), says so on the status rail and is ONE undo.
    /// </summary>
    [Fact]
    public async Task OverTheBends_EveryUse_APresetBendsThePickedStringOfEveryUse_InOneUndo() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var panel = pane.Vm.ChordTemplateEditor;
            var history = App.Services!.GetRequiredService<IHistoryService>();
            var chord = MostUsedShapeChord(pane);
            try
            {
                Select(pane, chord);
                Open(pane, SoundSection.Bends);
                panel.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
                Settle(pane.Window);
                Capture(pane, "chord-bends-every-use-600");

                var uses = pane.Level.Chords.Where(c => c.TemplateId == chord.TemplateId).ToList();
                Assert.True(uses.Count > 1, "the fixture shape has one use — 'every use' is the selection");
                Assert.True(Named<Border>(Sound(pane), "SoundApplyBar").IsEffectivelyVisible, "no 'Apply to' row over the bends");
                Assert.True(panel.Preview.IsActive, "the uses the bend will change are not marked before it commits");
                Assert.True(panel.Preview.Resolved.Chords.SetEquals(uses), "the marks are not the shape's uses");
                var bent = pane.Vm.BendTargetChordNote!.StringIndex;
                var others = BendSnapshot(pane.Level.Chords.Where(c => c.TemplateId != chord.TemplateId));
                var templates = pane.Vm.ProjectState.ActiveArrangement!.ChordTemplates.Select(t => t.Clone()).ToList();
                var steps = history.GetUndoSteps().Count;
                var usesBefore = BendSnapshot(uses);

                pane.Vm.ChordBendEditor!.PresetBendCommand.Execute(null);
                Settle(pane.Window);

                Assert.All(uses, c => Assert.All(c.ChordNotes, n => Assert.Equal(n.StringIndex == bent, n.BendValues.Count > 0)));
                Assert.Equal(others, BendSnapshot(pane.Level.Chords.Where(c => c.TemplateId != chord.TemplateId)));
                Assert.Equal(templates, pane.Vm.ProjectState.ActiveArrangement!.ChordTemplates.ToList());
                Assert.Equal(steps + 1, history.GetUndoSteps().Count);
                Assert.Contains(uses.Count.ToString(), pane.Vm.ProjectState.MainDisplayMessage);

                history.Undo();
                Settle(pane.Window);
                Assert.Equal(usesBefore, BendSnapshot(uses));
            }
            finally
            {
                panel.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
            }
        }), CancellationToken.None);

    /// <summary>#604's regression half for the bends: on "selected" a preset bends the selected chord
    /// and nothing else.</summary>
    [Fact]
    public async Task OverTheBends_Selected_APresetBendsTheSelectionOnly() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var history = App.Services!.GetRequiredService<IHistoryService>();
            var chord = MostUsedShapeChord(pane);
            Select(pane, chord);
            Open(pane, SoundSection.Bends);
            pane.Vm.ChordTemplateEditor.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
            Settle(pane.Window);
            Assert.False(pane.Vm.ChordTemplateEditor.Preview.IsActive, "the selection itself needs no marks");
            var others = BendSnapshot(pane.Level.Chords.Where(c => !ReferenceEquals(c, chord)));
            var steps = history.GetUndoSteps().Count;

            pane.Vm.ChordBendEditor!.PresetBendCommand.Execute(null);
            Settle(pane.Window);

            Assert.Contains(chord.ChordNotes, n => n.BendValues.Count > 0);
            Assert.Equal(others, BendSnapshot(pane.Level.Chords.Where(c => !ReferenceEquals(c, chord))));
            Assert.Equal(steps + 1, history.GetUndoSteps().Count);
            history.Undo();
            Settle(pane.Window);
            Assert.All(chord.ChordNotes, n => Assert.Empty(n.BendValues));
        }), CancellationToken.None);

    /// <summary>
    ///     #604: the keyboard's mute verb — the very call the key and the command palette make — follows
    ///     the rung while the ladder is on screen: "every use" over Technique mutes every use, in ONE
    ///     undo, and the status rail says how far it went. With the Inspector on Findings no ladder is
    ///     on screen, so the same key mutes the selection only.
    /// </summary>
    [Fact]
    public async Task TheMuteKey_FollowsTheLadderOnScreen_AndOnlyTheLadderOnScreen() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var panel = pane.Vm.ChordTemplateEditor;
            var history = App.Services!.GetRequiredService<IHistoryService>();
            var keys = App.Services!.GetRequiredService<IHighwayEditingService>();
            var chord = MostUsedShapeChord(pane);
            try
            {
                Select(pane, chord);
                Open(pane, SoundSection.Technique);
                panel.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
                Settle(pane.Window);

                var uses = pane.Level.Chords.Where(c => c.TemplateId == chord.TemplateId).ToList();
                Assert.True(uses.Count > 1, "the fixture shape has one use — 'every use' is the selection");
                Assert.True(panel.Preview.Resolved.Chords.SetEquals(uses), "the marks are not the shape's uses");
                var others = Snapshot(pane.Level.Chords.Where(c => c.TemplateId != chord.TemplateId));
                var steps = history.GetUndoSteps().Count;
                var usesBefore = Snapshot(uses);

                keys.ToggleSelectedMute();
                Settle(pane.Window);

                Assert.All(uses, c => Assert.All(c.ChordNotes, n => Assert.Equal(MuteType.Full, n.Mute)));
                Assert.Equal(others, Snapshot(pane.Level.Chords.Where(c => c.TemplateId != chord.TemplateId)));
                Assert.Equal(steps + 1, history.GetUndoSteps().Count);
                Assert.Contains(uses.Count.ToString(), pane.Vm.ProjectState.MainDisplayMessage);
                history.Undo();
                Settle(pane.Window);
                Assert.Equal(usesBefore, Snapshot(uses));

                Open(pane, SoundSection.Findings);
                Assert.False(Named<Border>(Sound(pane), "SoundApplyBar").IsEffectivelyVisible, "a ladder over the findings");
                var restBefore = Snapshot(pane.Level.Chords.Where(c => !ReferenceEquals(c, chord)));

                keys.ToggleSelectedMute();
                Settle(pane.Window);

                Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.Full, n.Mute));
                Assert.Equal(restBefore, Snapshot(pane.Level.Chords.Where(c => !ReferenceEquals(c, chord))));
                history.Undo();
                Settle(pane.Window);
            }
            finally
            {
                panel.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
            }
        }), CancellationToken.None);

    /// <summary>The Findings section lists every folded row of #117 and offers the way to the
    /// Issues tab; nothing selected offers the next task, and "Fix the next one" is the queue's own
    /// triage.</summary>
    [Fact]
    public async Task TheFindings_ListEveryRow_AndTheTaskFixesTheNextOne() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            Select(pane, FindingChord(pane));
            Open(pane, SoundSection.Findings);
            var sound = Sound(pane);
            var list = Named<ItemsControl>(sound, "SoundFindingsList");
            Assert.True(list.IsEffectivelyVisible);
            Assert.Equal(pane.Vm.SelectionIssueCount,
                list.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("FieldIssue")));

            pane.Selection.ClearSelections();
            Settle(pane.Window);
            Assert.True(pane.Vm.NextTask.HasTask, "the stress chart's chords have no fingers, so there is a task");
            var task = pane.Vm.NextTask.Filter!.Value;
            var fix = Named<Button>(sound, "SoundTaskFixNext");
            Assert.True(fix.IsEffectivelyVisible && fix.Bounds.Width > 1, $"Fix the next one laid out as {fix.Bounds}");
            fix.Command!.Execute(null);
            Settle(pane.Window);
            Assert.Equal(task, pane.Vm.Queue.SelectedFilter!.Filter);
            Assert.True(pane.Selection.SelectedChords.Count + pane.Selection.SelectedNotes.Count == 1,
                "Fix the next one selected nothing");
        }), CancellationToken.None);

    // ── checks ─────────────────────────────────────────────────────────────

    /// <summary>A templated chord of the shape the fixture uses most, so "every use" reaches past it.</summary>
    private static Chord MostUsedShapeChord(Pane pane) =>
        pane.Level.Chords.Where(c => c.TemplateId >= 0).GroupBy(c => c.TemplateId)
            .OrderByDescending(g => g.Count()).First().First(c => c.ChordNotes.All(n => n.Mute != MuteType.Palm));

    private static List<string> Snapshot(IEnumerable<Chord> chords) =>
        [.. chords.Select(c => $"{c.Position}|{c.TemplateId}|{c.Accent}|" +
                               string.Join(",", c.ChordNotes.Select(n => $"{n.StringIndex}:{n.Fret}:{n.Mute}:{n.Vibrato}")))];

    private static List<string> BendSnapshot(IEnumerable<Chord> chords) =>
        [.. chords.Select(c => $"{c.Position}|{c.TemplateId}|" + string.Join(",", c.ChordNotes.Select(n =>
            $"{n.StringIndex}:{n.Fret}:" + string.Join("/", n.BendValues.Select(b => $"{b.Position}@{b.Step}")))))];

    private static string ExpectedVerdict(Pane pane, Chord chord)
    {
        var editor = App.Services!.GetRequiredService<ChartEditorViewModel>();
        var verdict = InspectorViewModel.ExplainRepeat(pane.Vm.ProjectState.ActiveArrangement!.ChordTemplates,
            editor.PlacedNotes, editor.PlacedChords, editor.HandShapes, editor.Beats, chord);
        return verdict is { } v
            ? RepeatVerdictText.Format(v, pane.Vm.NoteStringCount, pane.Vm.NoteTuningNames, LocalizationService.Instance)
            : string.Empty;
    }

    private static void AssertWhollyOnScreen(Pane pane, ScrollViewer body, Control part, string what)
    {
        Assert.True(part.IsEffectivelyVisible, $"{what} is not visible.");
        Assert.True(part.Bounds is { Width: > 1, Height: > 1 }, $"{what} laid out as {part.Bounds}.");
        var top = Y(part, pane.Window);
        var bottom = top + part.Bounds.Height;
        var inBody = part.GetVisualAncestors().Contains(body);
        var limit = inBody ? Y(body, pane.Window) + body.Viewport.Height : pane.Window.Bounds.Height;
        Console.WriteLine($"[sound-rail] {what}: {top:0}..{bottom:0} px, limit {limit:0} px ({(inBody ? "body viewport" : "pinned")}).");
        Assert.True(bottom <= limit + 0.5,
            $"{what} ends at {bottom:0} px, below the {(inBody ? "body's viewport" : "pane")} at {limit:0} px — it needs a scroll at " +
            $"{pane.Window.Width:0}x{pane.Window.Height:0}.");
    }

    private static void AuditAndCapture(Pane pane, string name, List<string> clipped)
    {
        Capture(pane, name);
        var sound = Sound(pane);
        foreach (var text in sound.GetVisualDescendants().OfType<TextBlock>())
        {
            if (string.IsNullOrEmpty(text.Text) || !text.IsEffectivelyVisible) continue;
            if (text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None) continue;
            if (text.Bounds.Width <= 1) continue;
            var slot = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
            var needed = text.TextLayout.WidthIncludingTrailingWhitespace;
            if (needed > slot + 1.0) clipped.Add($"{name}: \"{text.Text}\" needs {needed:0.#} px in a {slot:0.#} px slot");
        }
    }

    // ── harness ────────────────────────────────────────────────────────────

    private sealed record Pane(
        Window Window, InspectorView View, InspectorViewModel Vm, ISelectionService Selection, ChartLevel Level);

    private static void WithPane(double width, double height, Action<Pane> body)
    {
        var services = App.Services
                       ?? throw new InvalidOperationException("the headless app session did not boot.");
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();
        var vm = services.GetRequiredService<InspectorViewModel>();

        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        var previousTab = vm.SelectedTabIndex;
        Window? window = null;
        try
        {
            var level = Seed(state);
            vm.SelectedTabIndex = 0;
            vm.Issues.RefreshIssues();

            var view = new InspectorView { DataContext = vm };
            window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            Settle(window);

            body(new Pane(window, view, vm, selection, level));
        }
        finally
        {
            selection.ClearSelections();
            vm.SelectedTabIndex = previousTab;
            window?.Close();
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The UI sweep's 240-sound stress chart, its chords bound to real templates so the
    /// "Apply to" ladder has every use and the same grip elsewhere to count.</summary>
    private static ChartLevel Seed(ProjectState state)
    {
        var chart = StressChartFactory.Chart(soundCount: 240, lengthMinutes: 2);
        var project = new ChartProject { Chart = { SongName = "Sound tab fixture", Artist = "DotCharter test harness" } };
        foreach (var beat in chart.Beats) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        var level = new ChartLevel { DifficultyIndex = 0 };
        foreach (var note in chart.Notes) level.Notes.Add(note);
        foreach (var chord in chart.Chords) level.Chords.Add(chord);
        foreach (var shape in chart.HandShapes) level.HandShapes.Add(shape);
        foreach (var anchor in chart.Anchors) level.Anchors.Add(anchor);
        arrangement.Levels.Add(level);
        foreach (var point in chart.EventPoints) arrangement.EventPoints.Add(point);
        foreach (var tone in chart.Tones) arrangement.ToneChanges.Add(tone);

        var templates = new Dictionary<string, ChordTemplate>(StringComparer.Ordinal);
        foreach (var chord in level.Chords)
        {
            var frets = chord.ChordNotes.ToDictionary(n => n.StringIndex, n => n.Fret);
            var key = string.Join(",", frets.OrderBy(f => f.Key).Select(f => $"{f.Key}:{f.Value}"));
            if (!templates.TryGetValue(key, out var template))
            {
                template = new ChordTemplate { Id = templates.Count, Name = $"C{templates.Count}", Frets = frets };
                templates[key] = template;
                arrangement.ChordTemplates.Add(template);
            }

            chord.TemplateId = template.Id;
        }

        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        return level;
    }

    /// <summary>A templated chord the lint has findings on (the stress chart's chords have no
    /// fingers, so every one of them does).</summary>
    private static Chord FindingChord(Pane pane) =>
        pane.Level.Chords.First(c => c.TemplateId >= 0 && pane.Vm.Issues.Items.Any(i => ReferenceEquals(i.Chord, c)));

    private static void Select(Pane pane, object sound)
    {
        pane.Selection.ClearSelections();
        if (sound is GuitarNote note) pane.Selection.SelectedNotes.Add(note);
        else if (sound is Chord chord) pane.Selection.SelectedChords.Add(chord);
        Settle(pane.Window);
    }

    /// <summary>A click on the section's rail entry — the toggle's own command.</summary>
    private static void Open(Pane pane, SoundSection section)
    {
        var entry = Named<ToggleButton>(Sound(pane), $"SoundRail{section}Entry");
        Assert.True(entry.IsEffectivelyVisible, $"the rail offers no {section} entry for {pane.Vm.SelectionKind}");
        entry.Command!.Execute(entry.CommandParameter);
        Settle(pane.Window);
        Assert.Equal(section, pane.Vm.Sections.Section);
    }

    private static SoundInspectorView Sound(Pane pane) =>
        pane.View.GetVisualDescendants().OfType<SoundInspectorView>().Single();

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} named '{name}' in {root.GetType().Name} — repoint this test.");

    private static double Y(Visual v, Visual root) => v.TranslatePoint(default, root)?.Y ?? double.NaN;

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Pane pane, string name)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        Directory.CreateDirectory(ArtifactDirectory);
        using var frame = pane.Window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ArtifactDirectory, name + ".png"), new PngBitmapEncoderOptions());
    }
}
