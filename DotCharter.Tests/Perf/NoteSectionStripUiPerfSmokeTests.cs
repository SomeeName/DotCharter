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
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #601 / #751: the Note tab's sections in inspector design D's shell. The app's own
///     <see cref="InspectorViewModel" /> over the UI sweep's 240-sound stress chart (its chords bound
///     to real templates, so the "Apply to" ladder has uses to count), laid out in the REAL
///     <see cref="InspectorView" /> at the default Inspector size — 600 x 524, a third of a 1920 px
///     window's editor split — and at the 560 / 420 / 380 / 320 px a narrower window or the dock's
///     floor gives it.
///     <para>
///         What it pins: a section strip of captions, each entry named and tipped off its caption's
///         key, offers no Playback section (the whole-chord flags are a group in Technique), pins ONE
///         "Apply to" ladder in the footer over a chord's Shape, Technique and Bends only (#604), and
///         keeps the pinned footer under every section; the counter follows a clicked sound (#611); the picker shows the ladder's live
///         rung; a string chip masks a technique to ONE string; and every edit goes through the
///         shipped commands (the #581 ladder reach, one undo step) — the bend editor and the
///         keyboard's technique verbs included (#604).
///     </para>
///     <para>
///         Width-shaped, so this class is in the release's de-DE / ja-JP layout filter
///         (<c>$HeadlessLayoutFilter</c>). With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> each case writes
///         PNGs to <c>TestResults/note-section-strip/</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class NoteSectionStripUiPerfSmokeTests
{
    private const double DefaultWidth = 600;
    private const double DefaultHeight = 524;

    /// <summary>The techniques a chord string is given most, by name in <c>SoundChordTechniqueView.axaml</c> (the
    /// strings card, which leads the Technique page; Accent is on the whole-chord card beside or after it).</summary>
    private static readonly string[] CommonTechniques =
        ["ChordMutePicker", "ChordSlideChip", "ChordVibratoChip", "ChordLinkNextChip"];

    private static string ArtifactDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestResults", "note-section-strip");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosingSettingsRefreshesNamingWorkAndItsCard(bool soundTabVisible)
    {
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var services = App.Services!;
            var configuration = services.GetRequiredService<IConfigurationService>();
            var config = configuration.LoadAppConfig();
            var suppress = config.SuppressDoubleStopChordNames;
            var keepPowerNames = config.KeepPowerChordNames;
            var shell = services.GetRequiredService<DotCharter.UI.ViewModels.Shell.MainWindowViewModel>();
            var session = services.GetRequiredService<DotCharter.Presentation.Shared.SettingsSession>();
            var previousShellTab = shell.SelectedMainTabIndex;
            try
            {
                config.SuppressDoubleStopChordNames = true;
                config.KeepPowerChordNames = false;
                var arrangement = pane.Vm.ProjectState.ActiveArrangement!;
                pane.Level.Notes.Clear();
                pane.Level.Chords.Clear();
                arrangement.ChordTemplates.Clear();
                var template = new ChordTemplate
                {
                    Id = 0, Fingerless = true, Frets = new Dictionary<int, int> { [3] = 4, [4] = 3 }
                };
                arrangement.ChordTemplates.Add(template);
                var chord = new Chord { TemplateId = 0 };
                foreach (var (s, fret) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
                pane.Level.Chords.Add(chord);
                pane.Vm.SelectedTabIndex = soundTabVisible ? InspectorViewModel.SoundTabIndex : InspectorViewModel.IssuesTabIndex;
                pane.Vm.Queue.Refresh();
                Assert.Equal(0, pane.Vm.Queue.UnnamedChordCount);

                shell.OpenSettingsCommand.Execute(null);
                config.SuppressDoubleStopChordNames = false;
                session.IsOpen = false;
                if (!soundTabVisible)
                {
                    Assert.True(pane.Vm.Queue.IsStale);
                    pane.Vm.SelectedTabIndex = InspectorViewModel.SoundTabIndex;
                }

                Assert.Equal(1, pane.Vm.Queue.UnnamedChordCount);
                Assert.Equal(1, pane.Vm.Queue.Filters.Single(f => f.Filter == SoundQueueFilter.UnnamedChord).Count);
                Assert.Equal(SoundQueueFilter.UnnamedChord, pane.Vm.NextTask.Filter);
                var loc = services.GetRequiredService<ILocalizationService>();
                Assert.Equal(loc.Plural("Inspector_Task_UnnamedOne", "Inspector_Task_Unnamed", 1), pane.Vm.NextTask.Title);
            }
            finally
            {
                config.SuppressDoubleStopChordNames = suppress;
                config.KeepPowerChordNames = keepPowerNames;
                session.IsOpen = false;
                shell.SelectedMainTabIndex = previousShellTab;
            }
        }), CancellationToken.None);
    }

    [Fact]
    public async Task QueueFilterMenu_ShowsTheChordWorkFixture()
    {
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var arrangement = pane.Vm.ProjectState.ActiveArrangement!;
            pane.Level.Notes.Clear();
            pane.Level.Chords.Clear();
            arrangement.ChordTemplates.Clear();
            arrangement.Capo = 3;
            for (var i = 0; i < 8; i++)
            {
                var template = new ChordTemplate
                {
                    Id = i, Frets = new Dictionary<int, int> { [0] = 3, [1] = 5, [2] = 5 }
                };
                if (i is not (4 or 5)) template.Frets.Remove(2);
                if (i == 1) template.Frets[1] = 3;
                template.Fingerless = i == 2;
                template.Unnamed = i == 5;
                if (i is >= 4 and not 7) template.Fingers = template.Frets.ToDictionary(kv => kv.Key, _ => 1);
                var chord = new Chord { Position = new FractionalPosition(i * 2), TemplateId = i, SplitIntoNotes = i == 0 };
                foreach (var (s, fret) in template.Frets)
                    chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret, Mute = i == 3 ? MuteType.Full : MuteType.None });
                arrangement.ChordTemplates.Add(template);
                pane.Level.Chords.Add(chord);
            }

            pane.Selection.ClearSelections();
            pane.Vm.Queue.Refresh();
            Settle(pane.Window);
            var button = Named<Button>(Sound(pane), "NoteFooterFilter");
            button.Flyout!.ShowAt(button);
            Settle(pane.Window);
            var list = Assert.IsType<ListBox>(Assert.IsAssignableFrom<Flyout>(button.Flyout).Content);
            Assert.True(list.Bounds.Width > 0 && list.Bounds.Height > 0);
            Capture(pane, "queue-filter-fixture");
            if (UiSweepRunner.CaptureEnabled && TopLevel.GetTopLevel(list) is { } popup)
            {
                using var frame = popup.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(ArtifactDirectory, "queue-filter-menu.png"), new PngBitmapEncoderOptions());
            }

            button.Flyout.Hide();
        }), CancellationToken.None);
    }

    /// <summary>
    ///     The section strip, for a chord: one row of captions in D's nav style, every entry named
    ///     off the key of the caption it shows and untipped (a tip would repeat the caption); Shape · Technique · Bends · Findings and
    ///     no Playback; the open entry lit; the Findings entry counting the selection's findings.
    /// </summary>
    [Fact]
    public async Task TheSectionStrip_IsCaptions_NamedOffTheirKey_AndOffersNoPlayback() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(DefaultWidth, DefaultHeight, pane =>
        {
            var chord = FindingChord(pane);
            Select(pane, chord);
            var sound = Sound(pane);

            var strip = Named<Border>(sound, "NoteSectionStrip");
            Assert.InRange(strip.Bounds.Height, 28, 36);
            var row = Named<OverflowTabStrip>(sound, "NoteSectionEntries");
            var entries = row.Children.OfType<ToggleButton>().Where(t => !OverflowTabStrip.GetIsOverflowButton(t)).ToList();
            Assert.Equal(Enum.GetValues<SoundSection>().Length, entries.Count);
            foreach (var toggle in entries)
            {
                var name = AutomationProperties.GetName(toggle);
                Assert.False(string.IsNullOrWhiteSpace(name), $"section entry {toggle.Name} has no accessible name");
                Assert.Null(ToolTip.GetTip(toggle));
                var caption = ((Panel)toggle.Content!).Children.OfType<TextBlock>().First();
                Assert.Equal(name, caption.Text);
            }

            var shown = Shown(row);
            Assert.Equal(["Shape", "Technique", "Bends", "Findings"], shown);
            Assert.DoesNotContain(Enum.GetNames<SoundSection>(), n => n == "Playback");
            Assert.All(row.ShownTabs, tab => Assert.True(((Panel)((ToggleButton)tab).Content!).Children.OfType<TextBlock>().First()
                .IsEffectivelyVisible, $"{tab.Name} shows no caption"));

            var shape = Named<ToggleButton>(sound, "NoteSectionShapeEntry");
            Assert.True(shape.IsChecked, "the default section's entry is not lit");

            var badge = Named<Panel>(sound, "NoteSectionFindingsBadge");
            Assert.True(badge.IsEffectivelyVisible, "the chord has findings but the Findings entry carries no count");
            Assert.Contains(badge.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == pane.Vm.SelectionIssueCount.ToString());

            Select(pane, pane.Level.Notes.First(n => n.BendValues.Count == 0));
            Assert.Equal(["Placement", "Technique", "Bends", "Findings"], Shown(row));

            pane.Selection.ClearSelections();
            Settle(pane.Window);
            Assert.Equal(["Task", "Library"], Shown(row));
            Assert.True(Named<Control>(sound, "SoundTaskBody").IsEffectivelyVisible, "nothing selected: the next task is hidden");
        }), CancellationToken.None);

    private static List<string> Shown(OverflowTabStrip row) =>
        [.. row.ShownTabs.Concat(row.OverflowTabs).Where(t => t.IsVisible).OrderBy(t => row.Children.IndexOf(t))
            .Select(t => t.Name!["NoteSection".Length..^"Entry".Length])];

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
            var body = OpenCard(sound);
            Assert.Equal(0, body.Offset.Y);
            var nav = Named<Border>(sound, "NoteFooter");
            Assert.InRange(nav.Bounds.Height, 1, 36);
            var apply = Named<Border>(sound, "SoundApplyBar");
            var box = Named<SoundShapeView>(sound, "SoundShapeCard").GetVisualDescendants().OfType<ChordBoxControl>().Single();
            AssertWhollyOnScreen(pane, body, apply, "the pinned 'Apply to' row");
            AssertWhollyOnScreen(pane, body, box, "the chord box (fingering editor)");
            Assert.Equal(ChordApplyFamily.Shape, pane.Vm.ChordTemplateEditor.ApplyFamily);

            Open(pane, SoundSection.Technique);
            body = OpenCard(sound);
            Assert.Equal(0, body.Offset.Y);
            AssertWhollyOnScreen(pane, body, Named<ItemsControl>(sound, "ChordStringKeys"), "the string keys");
            foreach (var name in CommonTechniques)
                AssertWhollyOnScreen(pane, body, Named<Control>(sound, name), $"the common technique '{name}'");
            AssertWhollyOnScreen(pane, body, apply, "the pinned 'Apply to' row over the techniques");
            Assert.Equal(ChordApplyFamily.Technique, pane.Vm.ChordTemplateEditor.ApplyFamily);

            // #604: a chord's bend follows the rung, so the picker pins over Bends too, whole on screen,
            // and the ladder counts and previews what a bend reaches (the technique family).
            Open(pane, SoundSection.Bends);
            body = OpenCard(sound);
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
                var nav = Named<Border>(sound, "NoteFooter");
                foreach (var section in SoundSectionsViewModel.For(SoundSelectionKind.Chord))
                {
                    Open(pane, section);
                    var pinned = section is SoundSection.Shape or SoundSection.Technique or SoundSection.Bends;
                    Assert.True(apply.IsEffectivelyVisible == pinned, $"the Apply-to row is {(pinned ? "hidden" : "shown")} over {section}");
                    Assert.True(nav.IsEffectivelyVisible, $"the pinned footer left {section}");
                }

                Open(pane, SoundSection.Technique);
                pane.Vm.ChordTemplateEditor.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);
                Settle(pane.Window);
                Named<ColumnFlowPanel>(sound, "NoteCards").ShowPage(Named<ColumnFlowCard>(sound, "NoteChordWholeCard"));
                Settle(pane.Window);
                var whole = Named<StackPanel>(sound, "ChordWholeChordGroup");
                Assert.True(whole.IsEffectivelyVisible && whole.Bounds.Height > 0, "the Whole chord group is not on screen");
                var ignore = Named<ToggleButton>(sound, "ChordIgnoreChip");
                var applyButton = Named<Button>(sound, "NoteFooterApplyButton");
                var before = chord.Ignore;
                ignore.IsChecked = !(ignore.IsChecked ?? false);
                Settle(pane.Window);
                Assert.True(chord.Ignore == before, "a staged toggle wrote before Apply");
                ApplyStaged(pane, applyButton);
                Assert.True(chord.Ignore != before, "toggling Ignore in the Whole chord group and Apply did not reach the chord");
                ignore.IsChecked = !(ignore.IsChecked ?? false);
                Settle(pane.Window);
                ApplyStaged(pane, applyButton);
                Assert.Equal(before, chord.Ignore);

                Open(pane, SoundSection.Bends);
                Assert.True(Named<Grid>(sound, "ChordBendsBody").IsEffectivelyVisible, "Bends: a chord's string picker is hidden");
                Assert.True(Named<UniformGrid>(sound, "SoundBendsPresets").IsEffectivelyVisible, "Bends: the presets are hidden");
                var plot = Named<ColumnFlowBendPlot>(sound, "SoundBendsPlot");
                var curve = Named<Grid>(sound, "SoundBendsCurve");
                Assert.True(plot.IsEffectivelyVisible && plot.Bounds.Width > curve.Bounds.Width * 0.9 && plot.Bounds.Height > 0,
                    $"Bends: the plot is {plot.Bounds.Width:0} of {curve.Bounds.Width:0} px wide");
                Assert.Same(pane.Vm.ChordBendEditor, plot.Editor);

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
                var position = Named<TextBlock>(sound, "NoteFooterPosition");
                Assert.StartsWith("2 /", position.Text);
                var chord = pane.Level.Chords.First(c => c.TemplateId >= 0 && c.ChordNotes.Count >= 2 && c.ChordNotes.All(n => !n.Vibrato));
                Select(pane, chord);
                var expected = vm.Queue.Matches(SoundQueueFilter.AnySound).ToList().IndexOf(chord) + 1;
                Assert.StartsWith($"{expected} /", position.Text);
                Named<Button>(sound, "NoteFooterNext").Command!.Execute(null);
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

                // One key per string of the tuning (string 1 = the highest); a key picks the edited string and
                // the Mask key puts it in the string-edit mask.
                var keys = Named<ItemsControl>(sound, "ChordStringKeys").GetVisualDescendants().OfType<ToggleButton>().ToList();
                Assert.Equal(vm.Staged.ChordTechnique.Strings.Count, keys.Count);
                var target = chord.ChordNotes.Min(n => n.StringIndex);
                var key = keys.Single(c => (int)c.CommandParameter! == target);
                var gutterName = GuitarStringLabel.Format(target, vm.NoteStringCount, vm.NoteTuningNames,
                    LocalizationService.Instance["Inspector_StringLabelFmt"]);
                Assert.Contains(gutterName, AutomationProperties.GetName(key), StringComparison.Ordinal);
                key.Command!.Execute(key.CommandParameter);
                Settle(pane.Window);
                Assert.True(key.IsChecked, "the key for the edited string is not lit");
                Named<Button>(sound, "ChordStringMask").Command!.Execute(null);
                Settle(pane.Window);
                Assert.True(vm.ProjectState.StringEditMask.IsExplicit(target), "the Mask key did not set the string-edit mask");
                Assert.Contains(gutterName, Named<TextBlock>(sound, "ChordStringHeader").Text, StringComparison.Ordinal);

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
                var applyButton = Named<Button>(sound, "NoteFooterApplyButton");
                vibrato.IsChecked = true;
                Settle(pane.Window);
                Assert.DoesNotContain(chord.ChordNotes, n => n.Vibrato);
                ApplyStaged(pane, applyButton);
                Assert.Equal([target], chord.ChordNotes.Where(n => n.Vibrato).Select(n => n.StringIndex));
                vibrato.IsChecked = false;
                Settle(pane.Window);
                ApplyStaged(pane, applyButton);
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
            var technique = Named<ToggleButton>(sound, "NoteSectionTechniqueEntry");
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

                PalmMuteEveryString(pane);

                Assert.All(uses, c => Assert.All(c.ChordNotes, n => Assert.Equal(MuteType.Palm, n.Mute)));
                Assert.Equal(others, Snapshot(pane.Level.Chords.Where(c => c.TemplateId != chord.TemplateId)));
                Assert.Equal(templates, pane.Vm.ProjectState.ActiveArrangement!.ChordTemplates.ToList());
                Assert.Equal(steps + 1, history.GetUndoSteps().Count);
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

            PalmMuteEveryString(pane);

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
                Assert.Equal(usesBefore, BendSnapshot(uses));
                ApplyStaged(pane, Named<Button>(Sound(pane), "NoteFooterApplyButton"));

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
            Assert.All(chord.ChordNotes, n => Assert.Empty(n.BendValues));
            ApplyStaged(pane, Named<Button>(Sound(pane), "NoteFooterApplyButton"));

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
    internal static Chord MostUsedShapeChord(Pane pane) =>
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
        Console.WriteLine($"[note-section-strip] {what}: {top:0}..{bottom:0} px, limit {limit:0} px ({(inBody ? "body viewport" : "pinned")}).");
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

    internal sealed record Pane(
        Window Window, InspectorView View, InspectorViewModel Vm, ISelectionService Selection, ChartLevel Level);

    internal static void WithPane(double width, double height, Action<Pane> body)
    {
        var services = App.Services
                       ?? throw new InvalidOperationException("the headless app session did not boot.");
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();
        var vm = services.GetRequiredService<InspectorViewModel>();

        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        var previousTab = vm.SelectedTabIndex;
        var applyStep = state.InspectorApplyStep;
        Window? window = null;
        try
        {
            // These cases pin the staged pages (#751): Settings ▸ Editor's Apply step ON (#805 made it off by default).
            state.InspectorApplyStep = true;
            var level = Seed(state);
            vm.SelectedTabIndex = 0;
            vm.Issues.RefreshIssues();
            HeadlessIssuePass.Settle(vm.Issues);

            var view = new InspectorView { DataContext = vm };
            window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            Settle(window);

            body(new Pane(window, view, vm, selection, level));
        }
        finally
        {
            selection.ClearSelections();
            state.InspectorApplyStep = applyStep;
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
    internal static Chord FindingChord(Pane pane) =>
        pane.Level.Chords.First(c => c.TemplateId >= 0 && pane.Vm.Issues.Items.Any(i => ReferenceEquals(i.Chord, c)));

    internal static void Select(Pane pane, object sound)
    {
        pane.Selection.ClearSelections();
        if (sound is GuitarNote note) pane.Selection.SelectedNotes.Add(note);
        else if (sound is Chord chord) pane.Selection.SelectedChords.Add(chord);
        Settle(pane.Window);
    }

    /// <summary>A click on the section's strip entry — the toggle's own command.</summary>
    internal static void Open(Pane pane, SoundSection section)
    {
        var entry = Named<ToggleButton>(Sound(pane), $"NoteSection{section}Entry");
        Assert.True(entry.IsEffectivelyVisible, $"the strip offers no {section} entry for {pane.Vm.SelectionKind}");
        entry.Command!.Execute(entry.CommandParameter);
        Settle(pane.Window);
        Assert.Equal(section, pane.Vm.Sections.Section);
    }

    /// <summary>Palm mute on the edited string, "Copy to strings", then the footer's Apply: every played string of
    /// every chord the rung reaches, in one undo step.</summary>
    private static void PalmMuteEveryString(Pane pane)
    {
        var sound = Sound(pane);
        Named<ComboBox>(sound, "ChordMutePicker").SelectedIndex = (int)MuteType.Palm;
        Settle(pane.Window);
        Named<Button>(sound, "ChordCopyToStrings").Command!.Execute(null);
        Settle(pane.Window);
        ApplyStaged(pane, Named<Button>(sound, "NoteFooterApplyButton"));
    }

    /// <summary>The footer's Apply, through its review (Confirm) when it asks one.</summary>
    internal static void ApplyStaged(Pane pane, Button apply)
    {
        Assert.True(apply.IsEffectivelyVisible && apply.IsEnabled, "the footer's Apply is not on screen or not enabled");
        apply.Command!.Execute(null);
        Settle(pane.Window);
        if (pane.Vm.Staged.Footer.IsReviewing) pane.Vm.Staged.Footer.ConfirmCommand.Execute(null);
        Settle(pane.Window);
    }

    /// <summary>The scroller of the section's leading card (a P4 page can be several cards; the first leads).</summary>
    internal static ScrollViewer OpenCard(SoundInspectorView sound) =>
        Named<ColumnFlowPanel>(sound, "NoteCards").ShownCards.OfType<ColumnFlowCard>().First().Scroller
        ?? throw new Xunit.Sdk.XunitException("the open card has no scroller");

    internal static SoundInspectorView Sound(Pane pane) =>
        pane.View.GetVisualDescendants().OfType<SoundInspectorView>().Single();

    internal static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} named '{name}' in {root.GetType().Name} — repoint this test.");

    private static double Y(Visual v, Visual root) => v.TranslatePoint(default, root)?.Y ?? double.NaN;

    internal static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    internal static void Capture(Pane pane, string name)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        Directory.CreateDirectory(ArtifactDirectory);
        using var frame = pane.Window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ArtifactDirectory, name + ".png"), new PngBitmapEncoderOptions());
    }
}
