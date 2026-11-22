using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.Localization;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #751 (inspector design D, phase 6): the Shape page in the REAL <see cref="InspectorView" />, for a hand shape
///     over a chord.
///     <list type="bullet">
///         <item>At 530 x 370 (primary), 480 x 370, 640 x 450 and 1100 x 450, for a 6-, 7- and 8-string grip and a
///         12-19 stretch, in English, German and Japanese, the grip rows and the column-flow fretboard measure non-zero, every string's board lane
///         sits on its row (28 px lanes), and no text in the page is cut without a plan for it — on every page and
///         with Full board on.</item>
///         <item>At 530 x 370 all six rows of a 6-string grip are on screen with nothing scrolled, in every language.</item>
///         <item>At 400, 480 and 530 px in German and Japanese, for 6 and 8 strings, the header's fret pager, page toggles
///         and Full board are all visible, apart, hit by a click at their centre and their text is not cut.</item>
///         <item>The owner's trap: with a chord and its hand shape selected, no grip input of the page (Grip frets,
///         ghost boxes, finger cells, board clicks and keys, row keys, template, name, flags, double-stop) changes
///         the chord's notes, and a Note edit re-frets without adding or dropping one. ONE grid, ONE board, ONE
///         template list (virtualized), ONE Pin, ONE Save: the old chord-shape editor is gone from the page, and its
///         tools (suggested names, Generate all fingerings, issue pips on the board) are on it.</item>
///         <item>The arrow keys reach the grip through the view: Up/Down walk the key row, Left/Right step its
///         finger.</item>
///         <item>The ghost box of a row and the Pin chip are staged: Save writes them through the history and one
///         undo puts the grip back (the old ghost-gem block's behaviour, now on the grid).</item>
///     </list>
///     Width-shaped, so this class is in the release's de-DE / ja-JP layout filter (<c>$HeadlessLayoutFilter</c>).
/// </summary>
[Collection("HeadlessUI")]
public sealed class SoundShapeViewUiPerfSmokeTests
{
    [Theory]
    [InlineData(530, 370, 6, false, "en-US")]
    [InlineData(530, 370, 7, false, "en-US")]
    [InlineData(530, 370, 8, false, "en-US")]
    [InlineData(530, 370, 6, true, "en-US")]
    [InlineData(530, 370, 6, false, "de-DE")]
    [InlineData(530, 370, 7, false, "ja-JP")]
    [InlineData(530, 370, 8, false, "ja-JP")]
    [InlineData(480, 370, 6, false, "en-US")]
    [InlineData(480, 370, 7, false, "de-DE")]
    [InlineData(480, 370, 8, true, "en-US")]
    [InlineData(640, 450, 6, false, "en-US")]
    [InlineData(1100, 450, 8, true, "en-US")]
    public async Task TheShapePage_LaysOut_WithTheBoardOnTheRows(int width, int height, int strings, bool stretch, string culture) =>
        await HeadlessSessionFixture.Dispatch(() => InCulture(culture, () => WithShape(width, height, strings, stretch, (window, view, vm) =>
        {
            var at = $"at {width}x{height}, {strings} strings{(stretch ? ", 12-19" : "")} ({culture}/{HeadlessSessionFixture.SweptCulture})";
            var grip = vm.GripInspector;
            for (var page = 0; page <= 4; page++)
            {
                foreach (var full in new[] { false, true })
                {
                    grip.IsFullBoard = full;
                    ShowPage(view, page);
                    Settle(window);
                    var where = $"{at}, page {page}{(full ? ", full board" : "")}";
                    var rows = Named(view, "SoundShapeRows");
                    Assert.True(rows.Bounds is { Width: > 1, Height: > 28 }, $"{where}: the rows laid out as {rows.Bounds}");
                    Assert.Equal(strings, grip.Rows.Count);
                    if (grip.ShowsBoard) AssertBoardOnRows(view, grip.Rows, where);
                    if (page > 0)
                    {
                        var frame = Named(view, "SoundShapePageFrame");
                        Assert.True(frame.Bounds is { Width: > 1, Height: > 1 }, $"{where}: the page laid out as {frame.Bounds}");
                    }

                    AssertNothingSpills(Named(view, "SoundShapeBody"), where);
                }
            }

            if (stretch)
            {
                ShowPage(view, 0);
                Settle(window);
                var board = (ColumnFlowFretboard)Named(view, "SoundShapeBoard");
                Assert.True(board.Lanes.MinFret <= 19 && board.Lanes.MaxFret >= 12,
                    $"{at}: the 12-19 window shows {board.WindowLabel}");
                Assert.False(string.IsNullOrEmpty(board.WindowLabel));
            }
        })), CancellationToken.None);

    /// <summary>Owner point 4 (less scrolling): at the primary size the six rows need no scroll, in every language.</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public async Task AtThePrimarySize_AllSixRows_AreOnScreen_WithNothingScrolled(string culture) =>
        await HeadlessSessionFixture.Dispatch(() => InCulture(culture, () => WithShape(530, 370, 6, false, (window, view, vm) =>
        {
            var card = view.GetVisualDescendants().OfType<ColumnFlowCard>().Single(c => c.Name == "NoteShapeCard");
            var scroller = card.Scroller ?? throw new Xunit.Sdk.XunitException("the Shape card has no scroller");
            Assert.Equal(0, scroller.Offset.Y);
            var list = (ItemsControl)Named(view, "SoundShapeRowList");
            for (var i = 0; i < 6; i++)
            {
                var row = list.ContainerFromIndex(i)!;
                var bottom = row.TranslatePoint(new Point(0, row.Bounds.Height), scroller)!.Value.Y;
                Assert.True(bottom <= scroller.Viewport.Height + 0.5,
                    $"{culture}: row {i + 1} ends at {bottom:0.#} px, below the card's {scroller.Viewport.Height:0.#} px viewport " +
                    $"(header {Named(view, "SoundShapeHeader").Bounds.Height:0.#} px: {HeaderWidths(view)}, body at {Named(view, "SoundShapeBody").TranslatePoint(default, scroller)!.Value.Y:0.#} px)");
            }
        })), CancellationToken.None);

    /// <summary>
    ///     The header in German and Japanese at the narrow widths: the fret pager (‹ "Frets 1–5" ›), every page toggle
    ///     and Full board are on screen, laid out with width, overlap nothing else of the header, and are what a click
    ///     at their centre hits — on the Board page (pager shown) and on the Name page (title shown), with Full board
    ///     off and on.
    /// </summary>
    [Theory]
    [InlineData(400, 6, "de-DE")]
    [InlineData(400, 8, "ja-JP")]
    [InlineData(480, 6, "ja-JP")]
    [InlineData(480, 8, "de-DE")]
    [InlineData(530, 6, "de-DE")]
    [InlineData(530, 6, "ja-JP")]
    [InlineData(530, 8, "de-DE")]
    [InlineData(530, 8, "ja-JP")]
    [InlineData(400, 6, "ja-JP")]
    [InlineData(480, 6, "de-DE")]
    public async Task TheHeader_KeepsThePagerAndEveryToggle_VisibleApartAndClickable(int width, int strings, string culture) =>
        await HeadlessSessionFixture.Dispatch(() => InCulture(culture, () => WithShape(width, 370, strings, false, (window, view, vm) =>
        {
            var card = view.GetVisualDescendants().OfType<ColumnFlowCard>().Single(c => c.Name == "NoteShapeCard");
            foreach (var page in new[] { 0, 1 })
            {
                foreach (var full in new[] { false, true })
                {
                    vm.GripInspector.IsFullBoard = full;
                    ShowPage(view, page);
                    Settle(window);
                    var at = $"{culture} at {width} px, {strings} strings, page {page}{(full ? ", full board" : "")}";
                    var parts = HeaderParts.Where(n => vm.GripInspector.ShowsBoard || !PagerParts.Contains(n))
                        .Select(n => Named(view, n)).ToList();
                    var rects = new List<(string Name, Rect Rect)>();
                    foreach (var part in parts)
                    {
                        Assert.True(part.IsEffectivelyVisible, $"{at}: {part.Name} is hidden");
                        Assert.True(part.Bounds is { Width: > 1, Height: > 1 }, $"{at}: {part.Name} laid out as {part.Bounds}");
                        var rect = new Rect(part.TranslatePoint(default, card)!.Value, part.Bounds.Size);
                        Assert.True(rect.X >= -0.5 && rect.Right <= card.Bounds.Width + 0.5,
                            $"{at}: {part.Name} spans {rect.X:0.#}–{rect.Right:0.#} px of the card's {card.Bounds.Width:0.#}");
                        var centre = part.TranslatePoint(new Point(part.Bounds.Width / 2, part.Bounds.Height / 2), window)!.Value;
                        var hit = window.InputHitTest(centre) as Visual;
                        Assert.True(hit != null && (hit == part || part.IsVisualAncestorOf(hit)),
                            $"{at}: a click on {part.Name}'s centre hits {(hit as Control)?.Name ?? hit?.GetType().Name ?? "nothing"}");
                        // A TextBlock clips to its bounds: the fret window's padding once left it 10 of 16 px.
                        foreach (var text in part.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Bounds.Width > 1))
                        {
                            Assert.True(text.Bounds.Height + 0.5 >= text.TextLayout.Height,
                                $"{at}: {part.Name}'s text is cut to {text.Bounds.Height:0.#} of {text.TextLayout.Height:0.#} px");
                        }

                        rects.Add((part.Name!, rect));
                    }

                    var overlaps = rects.SelectMany((a, i) => rects.Skip(i + 1)
                            .Where(b => a.Rect.Deflate(0.5).Intersects(b.Rect.Deflate(0.5)))
                            .Select(b => $"{a.Name} {a.Rect} / {b.Name} {b.Rect}"))
                        .ToList();
                    Assert.True(overlaps.Count == 0, $"{at}: header parts overlap: {string.Join("; ", overlaps)}");
                }
            }
        })), CancellationToken.None);

    // The header's lead (pager or title) and trail (page strip) widths against its own, for a failure message.
    private static string HeaderWidths(Control view)
    {
        var header = (Panel)Named(view, "SoundShapeHeader");
        return $"lead {header.Children[0].DesiredSize.Width:0.#} + trail {header.Children[1].DesiredSize.Width:0.#} in {header.Bounds.Width:0.#} px";
    }

    private static readonly string[] PagerParts = ["SoundShapeLowerFrets", "SoundShapeWindow", "SoundShapeHigherFrets"];

    private static readonly string[] HeaderParts =
    [
        .. PagerParts, "SoundShapePageBoard", "SoundShapePageName", "SoundShapePageScope", "SoundShapePageFlags",
        "SoundShapePageDoubleStop", "SoundShapeFullBoard"
    ];

    /// <summary>The owner's trap, through every input path of the page: a grip edit never changes the chord's notes.</summary>
    [Fact]
    public async Task WithAChordAndItsHandShapeSelected_NoGripInputOfThePage_ChangesTheChordsNotes() =>
        await HeadlessSessionFixture.Dispatch(() => WithShape(530, 370, 6, false, (window, view, vm) =>
        {
            var state = App.Services!.GetRequiredService<ProjectState>();
            var chord = state.ActiveLevel!.Chords[0];
            var grip = vm.GripInspector;
            var notes = Notes(chord);
            void Unchanged(string step)
            {
                Settle(window);
                Assert.True(notes == Notes(chord), $"{step} changed the chord's notes: {notes} -> {Notes(chord)}");
            }

            var rows = Named(view, "SoundShapeRows");
            foreach (var box in GripBoxes(view))
            {
                box.Focus();
                box.Text = "5";
                rows.Focus();
            }
            Unchanged("typing Grip frets");
            Assert.True(grip.HasDraft, "the Grip boxes drafted nothing");
            foreach (var ghost in view.GetVisualDescendants().OfType<CheckBox>().Where(b => b.IsEffectivelyVisible && b.IsEnabled))
                ghost.IsChecked = true;
            Unchanged("the ghost boxes");
            foreach (var choice in grip.Rows.SelectMany(r => r.FretboardFingerChoices).Where(c => c.Value == 2))
                choice.ToggleCommand.Execute(null);
            Unchanged("the finger cells");

            var board = (ColumnFlowFretboard)Named(view, "SoundShapeBoard");
            board.ToggleNoteCommand!.Execute((4, 7));
            board.SetFretCommand!.Execute(new ChordBoxFretEdit(3, 9, false));
            board.SetFingerCommand!.Execute(new ChordBoxFingerEdit(3, 4));
            board.ClearStringCommand!.Execute(5);
            Unchanged("clicking and typing on the board");
            board.Focus();
            Press(window, Key.Right, PhysicalKey.ArrowRight);
            Press(window, Key.Enter, PhysicalKey.Enter);
            Unchanged("the board's keys");
            rows.Focus();
            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Press(window, Key.Right, PhysicalKey.ArrowRight);
            Unchanged("the rows' keys");

            ShowPage(view, 1);
            Settle(window);
            grip.UseTemplateCommand.Execute(grip.TemplateOptions.First(o => o.IsChoice));
            grip.ChordNameText = "G5 test";
            ShowPage(view, 3);
            Settle(window);
            grip.Arpeggio = true;
            grip.IsPinned = true;
            ShowPage(view, 4);
            Settle(window);
            if (grip.UseDoubleStopPairCommand.CanExecute(null)) grip.UseDoubleStopPairCommand.Execute(null);
            Unchanged("the Name, Flags and Double-stop pages");

            grip.SaveFingeringCommand.Execute(null);
            Unchanged("Save");

            // A Note edit re-frets a string the chord already sounds, and adds or drops none.
            var strings = string.Join(",", chord.ChordNotes.Select(n => n.StringIndex).Order());
            var noteBox = view.GetVisualDescendants().OfType<TextBox>()
                .First(b => Grid.GetColumn(b) == 1 && b.IsEnabled && b.DataContext is GripRow { StringIndex: 0 });
            noteBox.Focus();
            noteBox.Text = "4";
            rows.Focus();
            grip.SaveFingeringCommand.Execute(null);
            Settle(window);
            Assert.Equal(strings, string.Join(",", chord.ChordNotes.Select(n => n.StringIndex).Order()));
            Assert.Equal(4, chord.ChordNotes.Single(n => n.StringIndex == 0).Fret);
        }, selectChord: true), CancellationToken.None);

    /// <summary>
    ///     Owner point 4: one place. The page holds ONE grid, ONE board, ONE template list, ONE Pin and ONE Save, never
    ///     the old chord-shape editor; the old editor's tools are here: suggested names on Name, Generate all fingerings
    ///     on Scope, the selection's issue pips on the board. The template list is virtualized.
    /// </summary>
    [Fact]
    public async Task ThePageHoldsOneOfEach_AndTheOldEditorsTools() =>
        await HeadlessSessionFixture.Dispatch(() => WithShape(530, 370, 6, false, (window, view, vm) =>
        {
            var state = App.Services!.GetRequiredService<ProjectState>();
            var arrangement = state.ActiveArrangement!;
            arrangement.ChordTemplates[0].Name = "";
            for (var i = 1; i <= 200; i++)
                arrangement.ChordTemplates.Add(new ChordTemplate { Id = i, Name = $"T{i}", Frets = { [0] = i % 20 + 1, [1] = i % 17 + 2 } });
            vm.GripInspector.Sync(chartChanged: true);
            var shape = view.GetVisualDescendants().OfType<SoundShapeView>().Single();

            Assert.Empty(shape.GetVisualDescendants().OfType<SoundLibraryTemplateView>());
            Assert.Single(shape.GetVisualDescendants().OfType<ChordBoxControl>());
            Assert.Single(shape.GetLogicalDescendants().OfType<ItemsControl>(), c => c.ItemsSource == vm.GripInspector.Rows);
            Assert.Single(shape.GetLogicalDescendants().OfType<ToggleButton>(), t => t.Name == "SoundShapePin");
            // ONE Save, pinned in the tab's footer (not in the card that scrolls).
            var sound = view.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            Assert.DoesNotContain(shape.GetLogicalDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, vm.GripInspector.SaveFingeringCommand));
            var save = Assert.Single(sound.GetLogicalDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, vm.GripInspector.SaveFingeringCommand));
            Assert.Contains(save.GetLogicalAncestors(), a => a is Control { Name: "NoteFooterShapeSave" });
            Assert.Same(vm.SelectionIssuePips, ((ColumnFlowFretboard)Named(view, "SoundShapeBoard")).IssueStrings);

            ShowPage(view, 1);
            Settle(window);
            var templates = (ItemsControl)Named(view, "SoundShapeTemplates");
            Assert.True(templates.Bounds is { Width: > 1, Height: > 1 }, $"the template list laid out as {templates.Bounds}");
            var realized = templates.GetRealizedContainers().Count();
            Assert.True(realized is > 0 and < 100, $"the template list realized {realized} of {vm.GripInspector.TemplateOptions.Count} rows at {templates.Bounds}, panel {templates.ItemsPanelRoot?.GetType().Name} {templates.ItemsPanelRoot?.Bounds}");
            var suggested = shape.GetLogicalDescendants().OfType<Button>()
                .Where(b => ReferenceEquals(b.Command, vm.GripInspector.UseNameCommand) && b.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(suggested);
            Assert.All(suggested, b => Assert.True(b.Bounds is { Width: > 1, Height: > 1 }));

            ShowPage(view, 2);
            Settle(window);
            var generate = (Button)Named(view, "SoundShapeGenerateAll");
            Assert.True(generate.IsEffectivelyVisible && generate.Bounds is { Width: > 1, Height: > 1 }, $"Generate all laid out as {generate.Bounds}");
            Assert.Same(vm.ChordTemplateEditor.GenerateAllFingeringsCommand, generate.Command);
        }), CancellationToken.None);

    [Fact]
    public async Task TheArrowKeys_WalkTheRows_AndStepTheFinger() =>
        await HeadlessSessionFixture.Dispatch(() => WithShape(530, 370, 6, false, (window, view, vm) =>
        {
            var grip = vm.GripInspector;
            var rows = Named(view, "SoundShapeRows");
            Assert.True(rows.Focus(), "the grip rows take focus");
            Settle(window);
            var start = grip.FocusedRow;

            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Assert.Equal(start + 1, grip.FocusedRow);
            Assert.True(grip.Rows[grip.FocusedRow].IsKeyRow);
            var finger = grip.Rows[grip.FocusedRow].Finger;
            Press(window, Key.Right, PhysicalKey.ArrowRight);
            Assert.NotEqual(finger, grip.Rows[grip.FocusedRow].Finger);
            Press(window, Key.Left, PhysicalKey.ArrowLeft);
            Assert.Equal(finger, grip.Rows[grip.FocusedRow].Finger);
            Press(window, Key.Up, PhysicalKey.ArrowUp);
            Assert.Equal(start, grip.FocusedRow);
        }), CancellationToken.None);

    /// <summary>The old editor's fret rows walked with Up / Down; the grid's fret boxes do too, column kept.</summary>
    [Fact]
    public async Task UpAndDown_InAFretBox_MoveToTheSameColumnsBoxOnTheNextRow() =>
        await HeadlessSessionFixture.Dispatch(() => WithShape(530, 370, 6, false, (window, view, vm) =>
        {
            var boxes = GripBoxes(view).OrderBy(b => b.TranslatePoint(default, view)!.Value.Y).ToArray();
            Assert.True(boxes.Length >= 3, $"only {boxes.Length} Grip boxes are enabled");
            Assert.True(boxes[0].Focus(), "the first Grip box takes focus");
            Settle(window);

            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Assert.Same(boxes[1], window.FocusManager?.GetFocusedElement());
            Press(window, Key.Down, PhysicalKey.ArrowDown);
            Assert.Same(boxes[2], window.FocusManager?.GetFocusedElement());
            Press(window, Key.Up, PhysicalKey.ArrowUp);
            Assert.Same(boxes[1], window.FocusManager?.GetFocusedElement());
        }), CancellationToken.None);

    [Fact]
    public async Task TheGhostBox_AndPin_AreStaged_SavedThroughTheHistory_AndUndone() =>
        await HeadlessSessionFixture.Dispatch(() => WithShape(530, 370, 6, false, (window, view, vm) =>
        {
            var history = App.Services!.GetRequiredService<IHistoryService>();
            var state = App.Services!.GetRequiredService<ProjectState>();
            var arrangement = state.ActiveArrangement!;
            var shape = state.ActiveLevel!.HandShapes[0];
            var before = shape.TemplateId;
            var grip = vm.GripInspector;

            var row = grip.Rows.Single(r => r.StringIndex == 1);
            var box = view.GetVisualDescendants().OfType<CheckBox>()
                .Single(b => b.DataContext == row && b.IsEffectivelyVisible);
            Assert.True(box.Bounds is { Width: > 1, Height: > 1 }, $"the ghost box laid out as {box.Bounds}");
            box.IsChecked = true;
            ShowPage(view, 3);
            Settle(window);
            var pin = (ToggleButton)Named(view, "SoundShapePin");
            Assert.True(pin.IsEffectivelyVisible && pin.Bounds is { Width: > 1, Height: > 1 }, $"the Pin chip laid out as {pin.Bounds}");
            Assert.Equal(before, shape.TemplateId); // staged: nothing written yet

            grip.SaveFingeringCommand.Execute(null);
            Settle(window);
            var saved = arrangement.FindChordTemplate(shape.TemplateId)!;
            Assert.Equal(ChordTemplate.GhostBit(1), shape.GetGhostMask(saved) & ChordTemplate.GhostBit(1));
            Assert.Equal(5, saved.GetFret(1)); // a ghost string keeps its fret

            history.Undo();
            Settle(window);
            Assert.Equal(before, shape.TemplateId);
            Assert.Equal(0, shape.GetGhostMask(arrangement.FindChordTemplate(before)!));
        }), CancellationToken.None);

    /// <summary>
    ///     Opt-in captures for design review (<c>DOTCHARTER_INSPECTOR_CAPTURE=1</c> + <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>):
    ///     every page, Full board and an 8-string grip at the primary 530 x 370, as <c>shape-*.png</c>.
    /// </summary>
    [Fact]
    public async Task Capture_EveryPage_AtThePrimarySize()
    {
        if (!InspectorCapture.InspectorCaptureFixture.Enabled) return;
        await HeadlessSessionFixture.Dispatch(() =>
        {
            foreach (var strings in new[] { 6, 7, 8 })
            {
                WithShape(530, 370, strings, false, (window, view, vm) =>
                {
                    for (var page = 0; page < PageToggles.Length; page++)
                    {
                        if (strings > 6 && page > 0) break;
                        ShowPage(view, page);
                        InspectorCapture.InspectorCaptureFixture.Settle(window);
                        InspectorCapture.InspectorCaptureFixture.Save(window, "", $"shape-{strings}-{PageToggles[page]}");
                    }

                    vm.GripInspector.IsFullBoard = true;
                    ShowPage(view, 2);
                    InspectorCapture.InspectorCaptureFixture.Settle(window);
                    InspectorCapture.InspectorCaptureFixture.Save(window, "", $"shape-{strings}-full-board");
                });
            }
        }, CancellationToken.None);
    }

    private static void AssertBoardOnRows(Control view, IReadOnlyList<GripRow> rows, string at)
    {
        var board = (ColumnFlowFretboard)Named(view, "SoundShapeBoard");
        Assert.True(board.Bounds is { Width: > 40, Height: > 28 }, $"{at}: the board laid out as {board.Bounds}");
        var list = (ItemsControl)Named(view, "SoundShapeRowList");
        for (var i = 0; i < rows.Count; i++)
        {
            var container = list.ContainerFromIndex(i) ?? throw new Xunit.Sdk.XunitException($"{at}: row {i} has no container");
            var row = rows[i];
            var mid = container.TranslatePoint(new Point(0, container.Bounds.Height / 2), board)!.Value.Y;
            var lane = board.Lanes.StringY(row.StringIndex);
            Assert.True(container.Bounds.Height is > 27 and < 29, $"{at}: string {row.StringIndex}'s row is {container.Bounds.Height:0.#} px tall");
            Assert.True(Math.Abs(mid - lane) <= 1.5,
                $"{at}: string {row.StringIndex}'s row centre is at {mid:0.#}, its board lane at {lane:0.#}");
        }
    }

    /// <summary>No text in the page is cut without a plan for it (wrap or ellipsis).</summary>
    private static void AssertNothingSpills(Control root, string at)
    {
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (string.IsNullOrEmpty(text.Text) || !text.IsEffectivelyVisible || text.Bounds.Width <= 1) continue;
            if (text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None) continue;
            var needed = text.TextLayout.WidthIncludingTrailingWhitespace;
            Assert.True(needed <= text.Bounds.Width + 1, $"{at} cuts \"{text.Text}\": {needed:0.#} px in {text.Bounds.Width:0.#}");
        }
    }

    private static readonly string[] PageToggles =
        ["SoundShapePageBoard", "SoundShapePageName", "SoundShapePageScope", "SoundShapePageFlags", "SoundShapePageDoubleStop"];

    // Through the page toggle's own command and parameter, as a click sends them.
    private static string Notes(Chord chord) =>
        string.Join(",", chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => $"{n.StringIndex}={n.Fret}"));

    private static IEnumerable<TextBox> GripBoxes(Control view) =>
        view.GetVisualDescendants().OfType<TextBox>().Where(b => Grid.GetColumn(b) == 2 && b.IsEnabled && b.DataContext is GripRow).ToArray();

    private static void InCulture(string culture, Action body)
    {
        var restore = LocalizationService.Instance.ConfiguredCulture;
        try
        {
            LocalizationService.Instance.SetConfiguredCulture(culture);
            body();
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture(restore);
            System.Globalization.CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
        }
    }

    private static void ShowPage(Control view, int page)
    {
        var toggle = (ToggleButton)Named(view, PageToggles[page]);
        Assert.True(toggle.Bounds is { Width: > 1, Height: > 1 }, $"the {PageToggles[page]} toggle laid out as {toggle.Bounds}");
        toggle.Command!.Execute(toggle.CommandParameter);
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Settle(window);
    }

    private static Control Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no control named '{name}' — repoint this test.");

    // A hand shape over a chord of the same grip: frets 3-5-5 (6 strings), plus 7 and 9 on strings 6 and 7 for
    // an 8-string; the stretch holds 12 on the low string and 19 on the next.
    private static void WithShape(double width, double height, int strings, bool stretch,
        Action<Window, InspectorView, InspectorViewModel> body, bool selectChord = false)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();
        var vm = services.GetRequiredService<InspectorViewModel>();
        var (project, arrangement, level, tab) = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, vm.SelectedTabIndex);
        Window? window = null;
        try
        {
            var lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = strings };
            var chart = new ChartLevel();
            lead.Levels.Add(chart);
            var frets = stretch
                ? new Dictionary<int, int> { [0] = 12, [1] = 19 }
                : new Dictionary<int, int> { [0] = 3, [1] = 5, [2] = 5 };
            if (strings > 6 && !stretch) frets[6] = 7;
            if (strings > 7 && !stretch) frets[7] = 9;
            var template = new ChordTemplate { Id = 0, Name = "G5", Frets = frets };
            lead.ChordTemplates.Add(template);
            var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
            foreach (var (s, f) in frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Sustain = new FractionalPosition(1) });
            chart.Chords.Add(chord);
            var shape = new HandShape { TemplateId = 0, Position = new(2), EndPosition = new(4) };
            chart.HandShapes.Add(shape);
            var song = new ChartProject();
            for (var i = 0; i < 16; i++) song.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
            song.Arrangements.Add(lead);
            state.ActiveProject = song;
            state.ActiveArrangement = lead;
            state.ActiveLevel = chart;
            vm.SelectedTabIndex = 0;

            var view = new InspectorView { DataContext = vm };
            window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            Settle(window);
            selection.ClearSelections();
            selection.SelectedHandShapes.Add(shape);
            if (selectChord) selection.SelectedChords.Add(chord);
            Settle(window);
            vm.Sections.ChooseCommand.Execute(SoundSection.Shape);
            Settle(window);
            Assert.True(vm.GripInspector.IsActive && vm.GripInspector.IsAvailable, "the Shape page is live for a hand shape");
            body(window, view, vm);
        }
        finally
        {
            selection.ClearSelections();
            vm.SelectedTabIndex = tab;
            window?.Close();
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;
            Dispatcher.UIThread.RunJobs();
        }
    }

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
}
