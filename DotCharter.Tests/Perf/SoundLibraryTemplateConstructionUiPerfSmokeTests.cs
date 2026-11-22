using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds the Note tab's Library TEMPLATE card (<c>SoundLibraryTemplateView</c>, #751 P5) and the footer's
///     "Apply to" picker (<c>SoundApplyBarView</c>) for real, attaches them to a window and lays them out — the
///     old chord-template panel's checks, moved with its verbs.
///     <para>
///         The finger column stopped being a <c>ComboBox</c> and became a strip
///         of six <c>ToggleButton.Seg</c>s, which introduces exactly the fault
///         classes markup cannot show: a <c>Classes="Seg"</c> that matches no
///         style still ARRANGES — it just renders as a bare grey button — and a
///         <c>ConverterParameter=''</c> that does not parse leaves the "no
///         finger" segment permanently dark with nothing failing.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session, which cannot share a testhost with the gate and is excluded from the
///         default gate. Run it by hand after touching this view. The pure half
///         — that every segment exists, binds the same property, and that the
///         converter round-trips — is in the gate as
///         <c>UI/ChordTemplateFingerStripTests</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SoundLibraryTemplateConstructionUiPerfSmokeTests
{
    [Fact]
    public Task SaveAndNext_BuildsAttachesAndMeasures_WithTheExistingVerbsStillPresent() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, _, _) = CaptureFixture();
            var view = new SoundLibraryTemplateView { DataContext = vm };
            AttachAndLayout(view, () =>
            {
                var verbs = view.FindControl<WrapPanel>("FingeringVerbs")!;
                var buttons = verbs.Children.OfType<Button>().ToList();
                Assert.Equal(4, buttons.Count);
                Assert.All(buttons, AssertMeasured);
                var saveAndNext = buttons.Single(b => b.Name == "SaveAndNextFingeringButton");
                Assert.Equal(LocalizationService.Instance["Keybind_SaveAndNextFingering"], saveAndNext.Content);
                Assert.False(saveAndNext.IsEffectivelyEnabled);
                vm.StringInputs[0].FingerText = "1";
                Relayout(view);
                Assert.True(saveAndNext.IsEffectivelyEnabled);
            });
        }, CancellationToken.None);

    [Fact]
    public async Task TheEditor_BuildsAttachesAndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new SoundLibraryTemplateView();
            AttachAndLayout(view, () =>
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 },
                    $"the chord-template editor laid out as {view.Bounds} — it rendered nothing"));
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The Nop toggle (#489) constructs, lays out with a size, and carries its
    ///     accessible name and tooltip resolved from the resx — a missing key
    ///     renders its fallback, not the label.
    /// </summary>
    [Fact]
    public async Task TheNopToggle_LaysOutWithItsAccessibleName()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new SoundLibraryTemplateView();
            AttachAndLayout(view, () =>
            {
                var toggle = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "NopToggle");
                Assert.True(toggle.Bounds is { Width: > 1, Height: > 1 },
                    $"the Nop toggle laid out as {toggle.Bounds} — it rendered nothing");
                var label = LocalizationService.Instance["ChordTemplate_Nop"];
                Assert.Equal(label, Avalonia.Automation.AutomationProperties.GetName(toggle));
                Assert.Equal(label, toggle.Content as string);
                Assert.Equal(LocalizationService.Instance["ChordTemplate_NopTip"], ToolTip.GetTip(toggle) as string);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The pin box (#795) constructs, lays out with a size, carries its accessible name and
    ///     tooltip, shows a chord's pinned grip ticked and enabled, and unticking it through the
    ///     bound view unpins that grip.
    /// </summary>
    [Fact]
    public async Task ThePinToggle_LaysOut_ShowsTheGripsPin_AndUntickingUnpinsIt()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chord) = ScopeFixture();
            var view = new SoundLibraryTemplateView { DataContext = vm };
            AttachAndLayout(view, () =>
            {
                selection.SelectedChords.Add(chord);
                Dispatcher.UIThread.RunJobs();
                var grip = vm.Template!;
                grip.Pinned = true;
                vm.ReloadForSelectedChord();
                Relayout(view);

                var toggle = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "PinnedToggle");
                AssertMeasured(toggle);
                var label = LocalizationService.Instance["Inspector_GripPinned"];
                Assert.Equal(label, Avalonia.Automation.AutomationProperties.GetName(toggle));
                Assert.Equal(label, toggle.Content as string);
                Assert.Equal(LocalizationService.Instance["Inspector_GripPinned_Tip"], ToolTip.GetTip(toggle) as string);
                Assert.True(toggle.IsChecked);
                Assert.True(toggle.IsEnabled);

                toggle.IsChecked = false;
                Dispatcher.UIThread.RunJobs();

                Assert.False(grip.Pinned);
            });
        }, CancellationToken.None);
    }

    /// <summary>The No name box lays out with its accessible name; ticking it clears the grip's
    /// name and locks the name box.</summary>
    [Fact]
    public async Task TheNoNameToggle_LaysOut_AndTickingItClearsAndLocksTheName()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chord) = ScopeFixture();
            var view = new SoundLibraryTemplateView { DataContext = vm };
            AttachAndLayout(view, () =>
            {
                selection.SelectedChords.Add(chord);
                Dispatcher.UIThread.RunJobs();
                Relayout(view);

                var toggle = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "NoNameToggle");
                AssertMeasured(toggle);
                var label = LocalizationService.Instance["ChordTemplate_NoName"];
                Assert.Equal(label, Avalonia.Automation.AutomationProperties.GetName(toggle));
                Assert.Equal(label, toggle.Content as string);
                Assert.Equal(LocalizationService.Instance["ChordTemplate_NoNameTip"], ToolTip.GetTip(toggle) as string);
                var nameBox = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ChordNameBox");
                Assert.True(nameBox.IsEnabled);

                toggle.IsChecked = true;
                Dispatcher.UIThread.RunJobs();

                Assert.True(vm.Template!.Unnamed);
                Assert.Equal("", vm.Template.Name);
                Assert.False(nameBox.IsEnabled);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The finger strip is DRESSED and takes a checked state.
    ///     <para>
    ///         With no DataContext the <c>IsChecked</c> bindings produce no value
    ///         at all, so the segments start unchecked and a local write is what
    ///         the real view model's value would do — the same idiom the inspector
    ///         tab strip's guard uses. What is proven is that the
    ///         <c>Seg</c> grammar reached them: an undressed ToggleButton shows no
    ///         difference between idle and checked, which is precisely how a
    ///         segmented control silently stops saying which option is current.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheFingerStrip_IsDressedAndShowsWhichSegmentIsCurrent()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, _, _) = CaptureFixture();
            var view = new SoundLibraryTemplateView { DataContext = vm };
            AttachAndLayout(view, () =>
            {
                var segments = view.GetVisualDescendants().OfType<ToggleButton>()
                    .Where(t => t.Classes.Contains("Seg"))
                    .ToList();

                // Every per-string row carries a strip of six Segs; what must hold is that they laid
                // out and that the class is dressed at all.
                Assert.NotEmpty(segments);

                var probe = segments.First(t => t.IsChecked != true);
                Assert.True(probe.Bounds is { Width: > 1, Height: > 1 },
                    $"a Seg laid out as {probe.Bounds} — the class matched nothing");

                // Read the PRESENTER: Fluent paints a ToggleButton's states there,
                // so the button's own Background is not what the user sees.
                var painted = probe.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .First(p => p.Name == "PART_ContentPresenter");
                var idle = painted.Background?.ToString();
                probe.IsChecked = true;
                view.UpdateLayout();
                Assert.NotEqual(idle, painted.Background?.ToString());
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A per-string ROW, realized for real, names the pitch its fret sounds —
    ///     and a muted string names nothing.
    ///     <para>
    ///         The row template is only exercised when the <c>ItemsControl</c>
    ///         has items, and with a null DataContext it has none, which is why
    ///         the sibling guards above never realize one. Writing
    ///         <c>ItemsSource</c> locally is what puts a row through
    ///         <c>ApplyTemplate</c> + <c>Measure</c>: the pitch column asks for
    ///         <c>FireFontMono</c> and <c>TextMuted</c> by
    ///         <c>DynamicResource</c>, and a column that resolved neither still
    ///         arranges — it just has no size.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task APerStringRow_ShowsThePitchAndLeavesAMutedStringBlank()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new SoundLibraryTemplateView();
            var rows = view.GetLogicalDescendants().OfType<ItemsControl>()
                .Single(c => c.Name == "StringRows");

            var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            rows.ItemsSource = new[]
            {
                new StringFingeringInput(0) { StringName = "E", Arrangement = arrangement, FretText = "5" },
                new StringFingeringInput(1) { StringName = "A", Arrangement = arrangement, FretText = "x" }
            };

            AttachAndLayout(view, () =>
            {
                var realized = rows.GetVisualDescendants().OfType<Grid>()
                    .Where(g => g.ColumnDefinitions.Count == 4)
                    .ToList();
                Assert.Equal(2, realized.Count);

                // Column 2 is the pitch cell — string name, fret, PITCH, finger strip.
                var cells = realized
                    .Select(row => row.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 2))
                    .ToList();

                Assert.Equal("A2", cells[0].Text);
                Assert.True(cells[0].Bounds is { Width: > 1, Height: > 1 },
                    $"the pitch column laid out as {cells[0].Bounds} — it rendered nothing");

                // The muted string keeps its row and shows NO pitch.
                Assert.True(string.IsNullOrEmpty(cells[1].Text),
                    $"a muted string named the pitch '{cells[1].Text}' — it does not sound one");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The "Apply to" ladder is the Note footer's picker (<see cref="SoundApplyBarView" />, #601): it holds every rung,
    ///     shows the one the next edit takes, and a rung chosen through the ladder's verb moves it. Ported from the old
    ///     editor's ladder smoke (#484): every rung row measures and carries its own accessible name; with nothing selected
    ///     "this chord" and "selected" are disabled, with a chord "+ same grip" is (no twin template); the reach beside
    ///     the picker is the lit rung's. The preview strip's steppers and the reshape hint are in
    ///     <c>NoteFooterUiPerfSmokeTests.ThePreviewStrip_StepsAndIsNamed_AndARefusedReshape_ShowsItsHint</c>.
    /// </summary>
    [Fact]
    public async Task TheApplyToPicker_HoldsEveryRung_AndShowsTheOneTheEditTakes()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chord) = ScopeFixture();
            var view = new SoundApplyBarView { DataContext = vm };

            AttachAndLayout(view, () =>
            {
                var picker = view.FindControl<ComboBox>("SoundApplyPicker")!;
                AssertMeasured(picker);
                Assert.Equal(vm.Ladder.Rows.Count, picker.ItemCount);
                Assert.Same(vm.Ladder.Rows.Single(r => r.Rung == vm.Ladder.Effective), picker.SelectedItem);
                // Nothing selected: there is no "this chord" or selection to edit, and an edit reaches every use.
                var rungs = Rungs(view, picker);
                Assert.False(rungs[ChordApplyRung.ThisChord].IsEffectivelyEnabled, "nothing selected: there is no 'this chord' to edit");
                Assert.False(rungs[ChordApplyRung.Selected].IsEffectivelyEnabled, "nothing selected: there is no selection to edit");
                Assert.Equal(ChordApplyRung.EveryUse, vm.Ladder.Effective);
                AssertReach(view, vm);

                selection.SelectedChords.Add(chord);
                Dispatcher.UIThread.RunJobs();
                Relayout(view);
                Assert.Equal(ChordApplyRung.Selected, vm.Ladder.Effective);
                Assert.Same(vm.Ladder.Rows.Single(r => r.Rung == ChordApplyRung.Selected), picker.SelectedItem);
                rungs = Rungs(view, picker);
                Assert.True(rungs[ChordApplyRung.ThisChord].IsEffectivelyEnabled);
                Assert.True(rungs[ChordApplyRung.Selected].IsEffectivelyEnabled);
                Assert.False(rungs[ChordApplyRung.SameGrip].IsEffectivelyEnabled, "no other template holds this grip");
                AssertReach(view, vm);

                vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
                Relayout(view);
                Assert.Same(vm.Ladder.Rows.Single(r => r.Rung == ChordApplyRung.EveryUse), picker.SelectedItem);
            });
        }, CancellationToken.None);
    }

    /// <summary>The picker's rung rows, opened so their containers exist: each measured and named by its row
    /// (<see cref="ChordApplyRungRow.AccessibleName" />, a per-rung name, not the picker's).</summary>
    private static Dictionary<ChordApplyRung, ComboBoxItem> Rungs(Control view, ComboBox picker)
    {
        picker.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        Relayout(view);
        Dispatcher.UIThread.RunJobs();
        try
        {
            var rows = picker.Items.Cast<ChordApplyRungRow>().ToList();
            var rungs = new Dictionary<ChordApplyRung, ComboBoxItem>();
            for (var i = 0; i < rows.Count; i++)
            {
                var item = Assert.IsType<ComboBoxItem>(picker.ContainerFromIndex(i));
                AssertMeasured(item);
                Assert.Equal(rows[i].AccessibleName, Avalonia.Automation.AutomationProperties.GetName(item));
                Assert.False(string.IsNullOrWhiteSpace(rows[i].AccessibleName), $"rung {rows[i].Rung} has no name");
                rungs[rows[i].Rung] = item;
            }

            return rungs;
        }
        finally
        {
            picker.IsDropDownOpen = false;
            Relayout(view);
        }
    }

    /// <summary>The reach beside the picker is the lit rung's: its short words on the bar, its whole detail as the tip.</summary>
    private static void AssertReach(Control view, ChordTemplateEditorViewModel vm)
    {
        var reach = view.FindControl<TextBlock>("SoundApplyReach")!;
        var lit = vm.Ladder.Rows.Single(r => r.Rung == vm.Ladder.Effective);
        Assert.False(string.IsNullOrEmpty(lit.ShortDetail), $"the lit rung {lit.Rung} states no reach");
        Assert.Equal(lit.ShortDetail, reach.Text);
        Assert.Equal(lit.Detail, ToolTip.GetTip(reach));
    }

    /// <summary>
    ///     The picker hosts the bulk-edit preview (#485): while it is on screen a rung that reaches past the selection
    ///     marks the chart; a closed footer stops marking it.
    /// </summary>
    [Fact]
    public async Task TheBulkEditPreview_ShowsWhileThePickerReachesPastTheSelection()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chords) = CaptureFixture();
            var view = new SoundApplyBarView { DataContext = vm };

            AttachAndLayout(view, () =>
            {
                selection.SelectedChords.Add(chords[0]);
                Dispatcher.UIThread.RunJobs();
                Relayout(view);
                Assert.False(vm.Preview.IsActive, "the selection rung reaches only the selection");

                vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
                Relayout(view);
                Assert.True(vm.Preview.IsActive);
                Assert.False(string.IsNullOrEmpty(vm.Preview.Summary));
            });

            Assert.False(vm.Preview.IsActive, "a closed footer must stop marking the chart");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     #515: selecting a chord showed an EMPTY Chord name box. The box is a
    ///     ComboBox whose SelectedItem is bound to the name, and the name list was
    ///     rebuilt after the name was set — the rebuild cleared the selection and
    ///     the binding wrote that null back. Only a real, bound view shows it.
    /// </summary>
    [Fact]
    public async Task SelectingAChord_ShowsItsTemplatesNameInTheNameBox()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chord) = ScopeFixture();
            var view = new SoundLibraryTemplateView { DataContext = vm };

            AttachAndLayout(view, () =>
            {
                var pickers = view.FindControl<Grid>("ChordTemplatePickers")!;
                var nameBox = pickers.Children.OfType<ComboBox>().Single(c => Grid.GetRow(c) == 1);

                selection.SelectedChords.Add(chord);
                Dispatcher.UIThread.RunJobs();
                Relayout(view);

                Assert.Equal("A5", vm.ChordName);
                Assert.Equal("A5", nameBox.SelectedItem);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The panel in its selection states, hosted with the inspector grammar
    ///     it inherits in the product, and — with
    ///     <c>DOTCHARTER_UI_SWEEP_DUMP=1 DOTCHARTER_RIG_CAPTURE_DIR=&lt;dir&gt;</c> —
    ///     written to <c>chord-panel-&lt;state&gt;.png</c> for a before/after review.
    ///     Without the variables it is a plain layout smoke test.
    /// </summary>
    [Fact]
    public async Task ThePanel_InEachSelectionState_LaysOutAndCanBeCaptured()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chords) = CaptureFixture();
            var view = new SoundLibraryTemplateView { DataContext = vm };
            var host = new Border
            {
                Padding = new Thickness(12),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E0B15")),
                Child = view
            };
            var window = new Window { Width = 440, Height = 1000, Content = host };
            foreach (var source in UiSweepCatalog.InheritedStyleIncludes(typeof(SoundLibraryTemplateView)))
                window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/")) { Source = new Uri(source) });
            window.Show();
            try
            {
                Shoot(window, view, "1-nothing-selected");

                selection.SelectedChords.Add(chords[0]);
                Dispatcher.UIThread.RunJobs();
                Shoot(window, view, "2-one-chord-selected");

                selection.SelectedChords.Add(chords[1]);
                selection.SelectedChords.Add(chords[2]);
                Dispatcher.UIThread.RunJobs();
                Shoot(window, view, "3-three-chords-selected");

                vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
                Dispatcher.UIThread.RunJobs();
                Shoot(window, view, "4-same-grip-chosen");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The Fingerless toggle (#622) constructs, MEASURES, resolves its icon and carries
    ///     its accessible name and tooltip from the resx — a mistyped icon key draws a blank
    ///     glyph and fails nothing else.
    /// </summary>
    [Fact]
    public async Task TheFingerlessToggle_LaysOutWithItsCaptionAndAccessibleName()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new SoundLibraryTemplateView();
            AttachAndLayout(view, () =>
            {
                var toggle = view.FindControl<ToggleButton>("FingerlessToggle")!;
                AssertMeasured(toggle);
                Assert.Equal(LocalizationService.Instance["ChordTemplate_Fingerless"],
                    Avalonia.Automation.AutomationProperties.GetName(toggle));
                Assert.Equal(LocalizationService.Instance["ChordTemplate_FingerlessTip"], ToolTip.GetTip(toggle) as string);
                Assert.Equal(LocalizationService.Instance["ChordTemplate_Fingerless"], toggle.Content as string);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     On a REAL view model, in the Sound tab's order (fingering first, #572) and with the
    ///     inspector grammar: the toggle lights, every realized finger strip is disabled and
    ///     says why, and switching it off gives the strips back. With
    ///     <c>DOTCHARTER_UI_SWEEP_DUMP=1 DOTCHARTER_RIG_CAPTURE_DIR=&lt;dir&gt;</c> it writes
    ///     <c>chord-panel-fingerless-*.png</c>.
    /// </summary>
    [Fact]
    public async Task TheFingerlessToggle_DisablesTheFingerStrips_AndCanBeCaptured()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chords) = CaptureFixture();
            var view = new SoundLibraryTemplateView { DataContext = vm };
            var host = new Border
            {
                Padding = new Thickness(12),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E0B15")),
                Child = view
            };
            var window = new Window { Width = 380, Height = 900, Content = host };
            foreach (var source in UiSweepCatalog.InheritedStyleIncludes(typeof(SoundLibraryTemplateView)))
                window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/")) { Source = new Uri(source) });
            window.Show();
            try
            {
                selection.SelectedChords.Add(chords[0]);
                Dispatcher.UIThread.RunJobs();
                vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
                Assert.True(vm.SetFingerForStrings(1, [0]));
                Assert.True(vm.SetFingerForStrings(3, [1]));
                Assert.True(vm.SetFingerForStrings(4, [2]));
                Dispatcher.UIThread.RunJobs();
                Shoot(window, view, "fingerless-1-off");

                var toggle = view.FindControl<ToggleButton>("FingerlessToggle")!;
                Assert.False(toggle.IsChecked);
                Assert.All(FingerStrips(view), s => Assert.True(s.IsEffectivelyEnabled));

                toggle.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Shoot(window, view, "fingerless-2-on");

                Assert.True(vm.Template!.Fingerless);
                Assert.Empty(vm.Template.Fingers);
                Assert.All(FingerStrips(view), s =>
                {
                    Assert.False(s.IsEffectivelyEnabled, "a Fingerless shape's finger strip must be disabled");
                    Assert.True(ToolTip.GetShowOnDisabled(s));
                    Assert.Equal(LocalizationService.Instance["ChordTemplate_FingerlessStripTip"], ToolTip.GetTip(s) as string);
                    Assert.All(s.Children.OfType<ToggleButton>(), t =>
                        Assert.True(t.Opacity < 1, "a switched-off finger segment must read as off, not as one more option"));
                });

                toggle.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Shoot(window, view, "fingerless-3-off-again");
                Assert.False(vm.Template!.Fingerless);
                Assert.All(FingerStrips(view), s => Assert.True(s.IsEffectivelyEnabled));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    /// <summary>Every realized per-string finger strip; never empty, or the asserts over
    /// them would pass on nothing.</summary>
    private static List<StackPanel> FingerStrips(Control view)
    {
        var strips = view.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Name == "FingerStrip").ToList();
        Assert.NotEmpty(strips);
        return strips;
    }

    private static void Shoot(Window window, Control view, string state)
    {
        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
        AssertMeasured(view);
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"chord-panel-{state}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>One A5 grip on 12 chords and 4 hand shapes across two levels,
    /// a separate template with the same frets, and the grip two frets up.</summary>
    private static (ChordTemplateEditorViewModel Vm, SelectionService Selection, List<Chord> Chords) CaptureFixture()
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var selection = new SelectionService();
        var state = new ProjectState();
        var config = new FakeConfigurationService();
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        ChordTemplate Template(int id, string name, int root)
        {
            var t = new ChordTemplate { Id = id, Name = name };
            t.Frets[0] = root;
            t.Frets[1] = root + 2;
            t.Frets[2] = root + 2;
            arrangement.ChordTemplates.Add(t);
            return t;
        }

        var a5 = Template(0, "A5", 5);
        Template(1, "A5 (alt)", 5);
        Template(2, "B5", 7);
        var chords = new List<Chord>();
        void Add(ChordTemplate t, int level, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var chord = new Chord { TemplateId = t.Id, Position = new FractionalPosition(arrangement.Levels[level].Chords.Count * 4) };
                foreach (var (stringIndex, fret) in t.Frets)
                    chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
                arrangement.Levels[level].Chords.Add(chord);
                if (level == 0 && t == a5) chords.Add(chord);
            }
        }

        Add(a5, 0, 8);
        Add(a5, 1, 4);
        Add(arrangement.ChordTemplates[1], 0, 2);
        Add(arrangement.ChordTemplates[2], 0, 3);
        for (var level = 0; level < 2; level++)
        {
            // Level 0's first bar covers chords[0], so "this chord" has a hand shape.
            var start = level == 0 ? 0 : 200;
            arrangement.Levels[level].HandShapes.Add(new HandShape { TemplateId = 0, Position = new FractionalPosition(start), EndPosition = new FractionalPosition(start + 2) });
            arrangement.Levels[level].HandShapes.Add(new HandShape { TemplateId = 0, Position = new FractionalPosition(300), EndPosition = new FractionalPosition(308) });
        }

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger),
            new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());
        return (new ChordTemplateEditorViewModel(editor, history, new FingeringGenerationService(), LocalizationService.Instance), selection, chords);
    }

    private static (ChordTemplateEditorViewModel Vm, SelectionService Selection, Chord Chord) ScopeFixture()
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var selection = new SelectionService();
        var state = new ProjectState();
        var config = new FakeConfigurationService();
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        var template = new ChordTemplate { Id = 0, Name = "A5" };
        template.Frets[0] = 5;
        template.Frets[1] = 7;
        template.Frets[2] = 7;
        arrangement.ChordTemplates.Add(template);
        var chords = new List<Chord>();
        for (var i = 0; i < 3; i++)
        {
            var chord = new Chord { TemplateId = 0, Position = new FractionalPosition(i * 4) };
            foreach (var (stringIndex, fret) in template.Frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            arrangement.Levels[0].Chords.Add(chord);
            chords.Add(chord);
        }

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger),
            new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());
        return (new ChordTemplateEditorViewModel(editor, history, new FingeringGenerationService(), LocalizationService.Instance), selection, chords[0]);
    }

    private static void AssertMeasured(Control control) =>
        Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
            $"{control.Name ?? control.GetType().Name} laid out as {control.Bounds} — it rendered nothing");

    private static void Relayout(Control view) => view.UpdateLayout();

    private static void AttachAndLayout(Control view, Action assert)
    {
        var window = new Window { Width = 460, Height = 900, Content = view };
        // Show() roots the tree and raises OnAttachedToLogicalTree, which is
        // where a Button calls CanExecute; the layout pass is driven through the
        // VIEW because a headless Window measured directly reports
        // "Invalid size returned for Measure".
        window.Show();
        try
        {
            view.Measure(new Size(460, 900));
            view.Arrange(new Rect(0, 0, 460, 900));
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}
