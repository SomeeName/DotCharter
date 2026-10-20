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
using DotCharter.UI.Views.Panels;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds the chord-template editor for real, attaches it to a window and
///     lays it out.
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
public sealed class ChordTemplateEditorConstructionUiPerfSmokeTests
{
    [Fact]
    public async Task TheEditor_BuildsAttachesAndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new ChordTemplateEditorView();
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
            var view = new ChordTemplateEditorView();
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
            var view = new ChordTemplateEditorView { DataContext = vm };
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
            var view = new ChordTemplateEditorView();
            AttachAndLayout(view, () =>
            {
                var segments = view.GetVisualDescendants().OfType<ToggleButton>()
                    .Where(t => t.Classes.Contains("Seg"))
                    .ToList();

                // The ladder's rungs carry Seg too; the ItemsControl has no
                // items with a null DataContext, so the finger strips are not
                // realized here. What must hold is that SOME Seg laid out and
                // that the class is dressed at all.
                Assert.NotEmpty(segments);

                var probe = segments[0];
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
            var view = new ChordTemplateEditorView();
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
                    .Where(g => g.Classes.Contains("FieldRow"))
                    .ToList();
                Assert.Equal(2, realized.Count);

                // Column 3 is the pitch cell — index, tuning letter, fret box,
                // PITCH, finger strip.
                var cells = realized
                    .Select(row => row.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 3))
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
    ///     The "Apply to" ladder on a REAL view model (#484): with nothing
    ///     selected the two selection rungs are disabled and "every use" is lit,
    ///     and a refused reshape shows its hint; with a chord selected every rung
    ///     but "+ same grip" (no other grip in this fixture) is enabled and
    ///     "selected" is lit. Every rung has to MEASURE, carry the view model's
    ///     words and an accessible name — a row whose bindings or styles failed
    ///     still arranges, with no size.
    /// </summary>
    [Fact]
    public async Task TheApplyToLadder_ShowsEveryRung_AndLightsTheOneTheEditTakes()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chord) = ScopeFixture();
            var view = new ChordTemplateEditorView { DataContext = vm };

            AttachAndLayout(view, () =>
            {
                var ladder = InScopeBar<Border>(view, "ApplyToLadder");
                var rungs = new[] { "ApplyToThisChord", "ApplyToSelected", "ApplyToEveryUse", "ApplyToSameGrip" }
                    .Select(name => InScopeBar<ToggleButton>(view, name))
                    .ToList();
                var rows = vm.Ladder.Rows;
                var hint = InScopeBar<TextBlock>(view, "ReshapeNeedsSelectionHint");

                Assert.True(ladder.IsVisible);
                AssertMeasured(ladder);
                for (var i = 0; i < rungs.Count; i++)
                {
                    AssertMeasured(rungs[i]);
                    Assert.Equal(rows[i].AccessibleName, Avalonia.Automation.AutomationProperties.GetName(rungs[i]));
                    Assert.Contains(rungs[i].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == rows[i].Label);
                }

                Assert.False(rungs[0].IsEffectivelyEnabled, "nothing selected: there is no 'this chord' to edit");
                Assert.False(rungs[1].IsEffectivelyEnabled);
                Assert.True(rungs[2].IsChecked, "nothing selected: an edit reaches every use");
                Assert.False(hint.IsVisible);

                vm.StringInputs[2].FretText = "9";
                Relayout(view);
                Assert.True(hint.IsVisible, "a refused reshape must show its hint");
                AssertMeasured(hint);

                selection.SelectedChords.Add(chord);
                Dispatcher.UIThread.RunJobs();
                Relayout(view);

                Assert.False(hint.IsVisible);
                Assert.True(rungs[0].IsEffectivelyEnabled);
                Assert.True(rungs[1].IsChecked, "the default rung is the selection");
                // #572: the compact bar prints the lit rung's count under the rungs.
                var reach = InScopeBar<TextBlock>(view, "ApplyToReach");
                Assert.True(reach.IsVisible, "the lit rung's reach is not printed");
                AssertMeasured(reach);
                Assert.Equal(rows[1].Detail, reach.Text);
                Assert.False(rungs[2].IsChecked);
                Assert.False(rungs[3].IsEffectivelyEnabled, "no other template holds this grip");

                // Clicking a rung lights it; clicking the lit one keeps it lit.
                rungs[2].Command!.Execute(rungs[2].CommandParameter);
                Relayout(view);
                Assert.True(rungs[2].IsChecked);
                Assert.False(rungs[1].IsChecked);
                rungs[2].IsChecked = false;
                rungs[2].Command!.Execute(rungs[2].CommandParameter);
                Relayout(view);
                Assert.True(rungs[2].IsChecked, "clicking the lit rung must leave it lit");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The bulk-edit preview row (#485) stays hidden while the lit rung reaches
    ///     only the selection, and shows — measured, with its count and both steppers
    ///     named — once "every use" reaches chords off the selection and on another
    ///     level. The view reports its visibility to the preview, so a closed panel
    ///     stops marking the chart.
    /// </summary>
    [Fact]
    public async Task TheBulkEditPreview_ShowsWhileTheRungReachesPastTheSelection()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (vm, selection, chords) = CaptureFixture();
            var view = new ChordTemplateEditorView { DataContext = vm };

            AttachAndLayout(view, () =>
            {
                var row = InScopeBar<Grid>(view, "ApplyToPreview");
                selection.SelectedChords.Add(chords[0]);
                Dispatcher.UIThread.RunJobs();
                Relayout(view);
                Assert.False(row.IsVisible, "the selection rung reaches only the selection (and its bar)");

                vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
                Relayout(view);

                Assert.True(vm.Preview.IsActive);
                Assert.True(row.IsVisible);
                AssertMeasured(row);
                Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.Preview.Summary);
                foreach (var (name, key) in new[]
                         {
                             ("PreviewPreviousTarget", "ChordTemplate_Preview_Previous"),
                             ("PreviewNextTarget", "ChordTemplate_Preview_Next")
                         })
                {
                    var button = InScopeBar<Button>(view, name);
                    AssertMeasured(button);
                    Assert.True(button.IsEffectivelyEnabled, $"{name}: the level on screen has marks to step to");
                    Assert.Equal(LocalizationService.Instance[key], Avalonia.Automation.AutomationProperties.GetName(button));
                }
            });

            Assert.False(vm.Preview.IsActive, "a closed panel must stop marking the chart");
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
            var view = new ChordTemplateEditorView { DataContext = vm };

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
            var view = new ChordTemplateEditorView { DataContext = vm };
            var host = new Border
            {
                Padding = new Thickness(12),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E0B15")),
                Child = view
            };
            var window = new Window { Width = 440, Height = 1000, Content = host };
            foreach (var source in UiSweepCatalog.InheritedStyleIncludes(typeof(ChordTemplateEditorView)))
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
    public async Task TheFingerlessToggle_LaysOutWithItsIconAndAccessibleName()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new ChordTemplateEditorView();
            AttachAndLayout(view, () =>
            {
                var toggle = view.FindControl<ToggleButton>("FingerlessToggle")!;
                AssertMeasured(toggle);
                Assert.Equal(LocalizationService.Instance["ChordTemplate_Fingerless"],
                    Avalonia.Automation.AutomationProperties.GetName(toggle));
                Assert.Equal(LocalizationService.Instance["ChordTemplate_FingerlessTip"], ToolTip.GetTip(toggle) as string);
                var icon = toggle.GetVisualDescendants().OfType<PathIcon>().Single();
                Assert.NotNull(icon.Data);
                AssertMeasured(icon);
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
            var view = new ChordTemplateEditorView { DataContext = vm, FingeringFirst = true };
            var host = new Border
            {
                Padding = new Thickness(12),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E0B15")),
                Child = view
            };
            var window = new Window { Width = 380, Height = 900, Content = host };
            foreach (var source in UiSweepCatalog.InheritedStyleIncludes(typeof(ChordTemplateEditorView)))
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
    /// <summary>A part of the "Apply to" ladder, which is its own view since #572
    /// (<see cref="ChordApplyScopeBar" />) and so its own name scope.</summary>
    private static T InScopeBar<T>(ChordTemplateEditorView view, string name) where T : Control =>
        view.FindControl<ChordApplyScopeBar>("ApplyScopeBar")?.FindControl<T>(name)
        ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} '{name}' in the editor's ChordApplyScopeBar — repoint this test.");

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
