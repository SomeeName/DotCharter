using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #751 (inspector design D, phases 1 and 4): the Note tab's new shell in the REAL <see cref="InspectorView" /> and the
///     shared column-flow controls on their own.
///     <list type="bullet">
///         <item>At the design's sizes — 530 x 370 (primary), 480 x 370 (tiny), 640 x 450 and 1100 x 450 — and for a
///         6- and an 8-string arrangement, every section of a note and a chord lays out with non-zero cards (P4: a page can be several cards; Small
///         shows the first and pages the rest), the
///         footer is pinned (no scroller above it, its bottom on the pane's), the section strip and the main tab
///         strip are one row each, every main tab stands in the strip (labelled, or icons when the captions do not
///         fit) and in English all eight are labelled at 530 px.</item>
///         <item><see cref="ColumnFlowPanel" /> with three cards: one column and a pager over every card when Tiny,
///         the first card and a pager beside it when Small, all three side by side when Wide; a page button brings its
///         card on screen without rebuilding anything, and a paged-away card takes no input.</item>
///         <item>P4 review: the Selection page's Values card shows its "Which sounds" picker uncut, and a staged draft
///         left on another sound shows the "draft on another sound" strip over the pinned footer, measured, inside the
///         pane.</item>
///         <item><see cref="FooterRail" /> keeps one row while both halves fit, drops the leading half's droppable
///         parts first (lowest order first, shown again once they fit) and stacks only when the trailing half still does
///         not fit whole.</item>
///     </list>
///     Width-shaped, so this class is in the release's de-DE / ja-JP layout filter (<c>$HeadlessLayoutFilter</c>).
/// </summary>
[Collection("HeadlessUI")]
public sealed class NoteShellUiPerfSmokeTests
{
    [Theory]
    [InlineData(530, 370, 6)]
    [InlineData(530, 370, 8)]
    [InlineData(480, 370, 6)]
    [InlineData(640, 450, 6)]
    [InlineData(1100, 450, 8)]
    public async Task EverySection_LaysOutInTheShell_WithThePinnedFooter(int width, int height, int strings) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, height, strings, (window, view, vm, selection, level) =>
        {
            var sound = view.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            var at = $"at {width}x{height}, {strings} strings ({HeadlessSessionFixture.SweptCulture})";
            var several = new object[] { level.Notes[0], level.Notes[1], level.Chords.First() };
            foreach (var pick in new object?[] { null, level.Notes.First(n => n.BendValues.Count == 0 && n.StringIndex > 0), level.Chords.First(), several })
            {
                selection.ClearSelections();
                foreach (var one in pick as object?[] ?? [pick])
                {
                    if (one is GuitarNote note) selection.SelectedNotes.Add(note);
                    else if (one is Chord chord) selection.SelectedChords.Add(chord);
                }

                Settle(window);
                foreach (var section in vm.Sections.Sections)
                {
                    vm.Sections.ChooseCommand.Execute(section);
                    Settle(window);
                    AssertShell(window, sound, $"{at}, {vm.SelectionKind} {section}");
                    // Showing a page stages nothing: Apply has nothing to write until the user edits.
                    Assert.True(vm.Staged.Footer.Count == 0,
                        $"{at}, {vm.SelectionKind} {section}: showing the page staged an edit ({vm.Staged.Engine.Placement}, {vm.Staged.Engine.Bulk})");
                }
            }

            var row = view.GetVisualDescendants().OfType<OverflowTabStrip>().Single(s => s.Name == "InspectorTabRow");
            AssertOneRow(row, $"{at}: the main tab strip");
            Assert.True(row.OverflowTabs.Count == 0, $"{at} tabs overflow the main strip: {string.Join(", ", row.OverflowTabs.Select(t => t.Tag))}");
            if (width >= 530 && HeadlessSessionFixture.SweptCulture == "en-US") Assert.Equal(TabStripMode.Labelled, row.Mode);
        }), CancellationToken.None);

    /// <summary>
    ///     #751 P5: the Task, Library, Bends and Findings pages' own parts at the dock's floor. The bend plot spans its
    ///     card, the template list is a fixed-height VIRTUALIZING list (fewer rows realized than the arrangement has
    ///     templates), and the Shape page's Save set sits in the pinned footer, not in a card that scrolls.
    /// </summary>
    [Theory]
    [InlineData(530, 370, 6)]
    [InlineData(530, 370, 8)]
    public async Task ThePageParts_LayOut(int width, int height, int strings) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, height, strings, (window, view, vm, selection, level) =>
        {
            var sound = view.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            var at = $"at {width}x{height}, {strings} strings ({HeadlessSessionFixture.SweptCulture})";
            var cards = (ColumnFlowPanel)Named(sound, "NoteCards");

            void Part(SoundSection section, string name)
            {
                vm.Sections.ChooseCommand.Execute(section);
                Settle(window);
                var part = Named(sound, name);
                var card = part.GetVisualAncestors().OfType<ColumnFlowCard>().First();
                cards.ShowPage(card);
                Settle(window);
                Assert.True(part.IsEffectivelyVisible && part.Bounds is { Width: > 1, Height: > 1 }, $"{at}: {section} '{name}' laid out as {part.Bounds}");
            }

            var arrangement = vm.ProjectState.ActiveArrangement!;
            for (var i = 0; i < 40; i++)
                arrangement.ChordTemplates.Add(new ChordTemplate { Id = 9000 + i, Name = $"T{i}", Frets = { [0] = i % 12 } });
            selection.ClearSelections();
            Settle(window);
            foreach (var name in new[] { "SoundTaskCard", "SoundTaskReadiness", "SoundTaskPlayhead" }) Part(SoundSection.Task, name);
            foreach (var name in new[] { "SoundLibraryList", "SoundLibraryGrip", "SoundLibraryUses", "SoundLibraryTemplate" })
                Part(SoundSection.Library, name);
            var list = (ListBox)Named(sound, "SoundLibraryList");
            var realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.True(vm.Library.Entries.Count >= 40 && realized > 0 && realized < vm.Library.Entries.Count,
                $"{at}: the template list realized {realized} of {vm.Library.Entries.Count} rows — it does not virtualize");

            // #751 P5 review M2: Edit template opens the Edit shape dialog on the picked template.
            var dialogs = new RecordingDialogs();
            vm.ChordTemplateEditor.ShapeEditor.Dialogs = dialogs;
            var picked = vm.Library.Entries[^1];
            vm.Library.Selected = picked;
            Settle(window);
            var edit = (Button)Named(sound, "SoundLibraryEdit");
            Assert.True(edit.IsEffectivelyEnabled && edit.Command?.CanExecute(null) == true, $"{at}: Edit template cannot be pressed");
            edit.Command!.Execute(null);
            Settle(window);
            Assert.Equal([typeof(DotCharter.Presentation.Contracts.Editor.IShapeEditScreen)], dialogs.Opened);
            Assert.Same(picked.Template, vm.ChordTemplateEditor.Template);

            // #751 P5 review L1: the picked template's read-only grip grows with the string count (18 px a string).
            Part(SoundSection.Library, "SoundLibraryGrip");
            var board = (DotCharter.UI.Controls.ColumnFlowFretboard)Named(sound, "SoundLibraryBoard");
            var pitch = (board.Bounds.Height - DotCharter.UI.Controls.ColumnFlowFretboard.ScaleHeight -
                         DotCharter.UI.Controls.ColumnFlowFretboard.LaneHeight) / Math.Max(1, board.StringCount - 1);
            Assert.True(board.StringCount == strings && pitch >= 18 - 0.5,
                $"{at}: the library grip draws {board.StringCount} strings {pitch:0.#} px apart in {board.Bounds.Height:0} px");

            selection.SelectedChords.Add(level.Chords.First());
            Settle(window);
            foreach (var name in new[] { "SoundBendsPlot", "ChordBendsBody", "SoundBendsPresets", "SoundBendsScope", "SoundBendsScopePicker" })
                Part(SoundSection.Bends, name);
            // #751 P5 review M10: the in-card "Bend scope" shows the same rung as the footer's "Apply to".
            Assert.Same(((ComboBox)Named(sound, "SoundApplyPicker")).SelectedItem, ((ComboBox)Named(sound, "SoundBendsScopePicker")).SelectedItem);
            Assert.NotNull(((ComboBox)Named(sound, "SoundBendsScopePicker")).SelectedItem);
            var plot = Named(sound, "SoundBendsPlot");
            var curve = Named(sound, "SoundBendsCurve");
            Assert.True(plot.Bounds.Width > curve.Bounds.Width * 0.9, $"{at}: the bend plot is {plot.Bounds.Width:0} of {curve.Bounds.Width:0} px");
            // #751 P5 review M8: every preset caption is whole (two lines at most, nothing trimmed or cut).
            foreach (var caption in Named(sound, "SoundBendsPresets").GetVisualDescendants().OfType<TextBlock>())
            {
                var lines = caption.TextLayout.TextLines;
                Assert.True(lines.Count <= 2 && !lines.Any(l => l.HasCollapsed) && lines.Sum(l => l.Length) >= (caption.Text?.Length ?? 0),
                    $"{at}: the preset caption '{caption.Text}' does not fit ({lines.Count} lines)");
            }

            foreach (var name in new[] { "SoundFindingsOpenIssues", "SoundFindingsDetail", "SoundFindingsReview" }) Part(SoundSection.Findings, name);

            vm.Sections.ChooseCommand.Execute(SoundSection.Shape);
            Settle(window);
            // The whole Save set, each a visible button in the pinned footer (#751 P5 review H4: none behind a menu).
            foreach (var name in new[] { "SoundShapeUndoSave", "SoundShapeSave", "SoundShapeSaveNext", "SoundShapeSaveNextMissing" })
            {
                var save = Named(sound, name);
                Assert.True(save.IsEffectivelyVisible && save.Bounds is { Width: > 1, Height: > 1 }, $"{at}: {name} over Shape laid out as {save.Bounds}");
                Assert.Contains(save.GetVisualAncestors(), a => a.Name == "NoteFooterShapeSave");
                Assert.DoesNotContain(save.GetVisualAncestors(), a => a is ScrollViewer);
            }

            Assert.DoesNotContain(sound.GetVisualDescendants().OfType<Button>(), b => b.Flyout != null && b.Name is { } n && n.StartsWith("SoundShape", StringComparison.Ordinal));
        }), CancellationToken.None);

    /// <summary>#751 P5 review M1: a part of the TRAILING half marked droppable (the ladder's short reach) goes after the
    /// leading half's, before the rail takes a second row; stacked, it comes back (the trailing half has the row).</summary>
    [Fact]
    public async Task TheFooterRail_DropsTheTrailingHalfsPartsBeforeItStacks() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var place = new Border { Width = 50, Height = 20, [FooterRail.DropOrderProperty] = 1 };
            var lead = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4, Children = { new Border { Width = 106, Height = 28 }, place } };
            var reach = new Border { Width = 40, Height = 20, [FooterRail.DropOrderProperty] = 2 };
            var trail = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4, Children = { new Border { Width = 200, Height = 28 }, reach } };
            var rail = new FooterRail { Spacing = 6, Children = { lead, trail } };
            var window = new Window { Width = 500, Height = 100, Content = new Border { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = rail } };
            window.Show();
            try
            {
                // 160 lead + 6 + 244 trail = 410: everything fits.
                Settle(window);
                Assert.Equal((0, 2, false), (rail.Dropped, rail.MaxDroppable, rail.IsStacked));

                // 340: the place goes (356), then the reach (312): one row still.
                window.Width = 340;
                Settle(window);
                Assert.Equal((2, false), (rail.Dropped, rail.IsStacked));
                Assert.False(reach.IsVisible);

                // 280: nothing left to drop and still too wide: two rows, and only then; the reach is back.
                window.Width = 280;
                Settle(window);
                Assert.True(rail.IsStacked);
                Assert.Equal(rail.MaxDroppable, rail.DropsBeforeStacking);
                Assert.True(reach.IsVisible, "stacked, the trailing half's own part stayed dropped");

                window.Width = 500;
                Settle(window);
                Assert.Equal((0, false), (rail.Dropped, rail.IsStacked));
                Assert.True(place.IsVisible && reach.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task TheColumnFlow_PagesByTier_AndAPageButtonShowsItsCard() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var cards = Enumerable.Range(0, 3).Select(i => new ColumnFlowCard
            {
                Content = new Border { Height = 120 + (i * 20), Child = new TextBlock { Text = $"Card {i}" } },
                [ColumnFlowPanel.TitleProperty] = $"Page {i}"
            }).ToArray();
            var panel = new ColumnFlowPanel();
            foreach (var card in cards) panel.Children.Add(card);
            var host = new UserControl { Content = panel };
            host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });
            var window = new Window { Width = 514, Height = 300, Content = host };
            window.Show();
            try
            {
                Settle(window);
                Assert.Equal(ColumnFlowTier.Small, panel.Tier);
                Assert.Equal([cards[0], cards[1]], panel.ShownCards);
                var pages = panel.Pager.Buttons;
                Assert.Equal(["Page 1", "Page 2"], pages.Select(b => Avalonia.Automation.AutomationProperties.GetName(b)));
                Assert.True(pages[0].IsChecked == true && pages[1].IsChecked != true, "the card on screen is not the lit page");
                Assert.All(panel.ShownCards, c => Assert.True(c.Bounds is { Width: > 1, Height: > 1 }, $"a card laid out as {c.Bounds}"));
                Assert.True(cards[0].Bounds.Right <= cards[1].Bounds.Left, "the two columns overlap");
                Assert.False(cards[2].IsHitTestVisible, "the paged-away card still takes the pointer");
                Assert.Equal(KeyboardNavigationMode.None, KeyboardNavigation.GetTabNavigation(cards[2]));
                Assert.All(cards, c => Assert.NotNull(c.Scroller));

                pages[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                Assert.Equal([cards[0], cards[2]], panel.ShownCards);
                Assert.True(cards[2].IsHitTestVisible && cards[2].Bounds.Width > 1, "the picked page did not come on screen");
                Assert.Same(pages[1], panel.Pager.Buttons[1]);

                window.Width = 400;
                Settle(window);
                Assert.Equal(ColumnFlowTier.Tiny, panel.Tier);
                Assert.Equal([cards[2]], panel.ShownCards);
                Assert.Equal(3, panel.Pager.Buttons.Count);

                // Tiny: the pager row stands in for the card's title row and carries its "More below" cue, so the
                // card's scroller keeps the card's whole height (#751 P4 review); Small: the card draws its own.
                ((Border)cards[2].Content!).Height = 900;
                Settle(window);
                var cue = panel.PagerCueControl;
                Assert.True(cards[2].HasMoreBelow, "the tall card has nothing below the fold");
                Assert.True(cue.IsVisible && cue.Bounds is { Width: > 1, Height: > 1 }, $"the pager row's cue is {cue.Bounds}");
                Assert.True(cue.Bounds.Left >= panel.Pager.Buttons[^1].TranslatePoint(default, panel)!.Value.X + panel.Pager.Buttons[^1].Bounds.Width,
                    "the pager row's cue covers a page button");
                Assert.False(cards[2].ShowsCue, "the card draws a second cue under the pager row's");
                Assert.Equal(0, cards[2].Scroller!.Margin.Bottom);
                window.Width = 514;
                Settle(window);
                Assert.False(cue.IsVisible, "the pager row's cue stays at the Small tier");
                Assert.True(cards[2].ShowsCue, "the card lost its own cue at the Small tier");
                ((Border)cards[2].Content!).Height = 160;

                window.Width = 1000;
                window.Height = 400; // a short body pages even when Wide
                Settle(window);
                Assert.Equal(ColumnFlowTier.Wide, panel.Tier);
                Assert.Equal(cards, panel.ShownCards);
                Assert.False(panel.Pager.IsVisible, "a pager shows where every card has its own column");
                Assert.All(cards, c => Assert.True(c.IsHitTestVisible && c.Bounds is { Width: > 1, Height: > 1 }));

                // A card whose binding hides it is not a card: the other two stand side by side.
                cards[1].IsVisible = false;
                Settle(window);
                Assert.Equal([cards[0], cards[2]], panel.ShownCards);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task TheFooterRail_DropsInOrder_ThenStacks() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var second = new Border { Width = 40, Height = 20, [FooterRail.DropOrderProperty] = 2 };
            var first = new Border { Width = 50, Height = 20, [FooterRail.DropOrderProperty] = 1 };
            var lead = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4, Children = { new Border { Width = 106, Height = 28 }, second, first } };
            var trail = new Border { Width = 260, Height = 28 };
            var rail = new FooterRail { Spacing = 6, Children = { lead, trail } };
            var window = new Window { Width = 500, Height = 100, Content = new Border { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = rail } };
            window.Show();
            try
            {
                // 106 + 4 + 40 + 4 + 50 = 204 lead, + 6 + 260 = 470: everything fits at 500.
                Settle(window);
                Assert.False(rail.IsStacked);
                Assert.Equal(0, rail.Dropped);
                Assert.Equal(500, trail.Bounds.Right, 0.5);
                Assert.True(lead.Bounds.Right <= trail.Bounds.Left, "the two halves overlap");

                // 440: the first droppable goes (204 - 54 + 266 = 416), the second stays.
                window.Width = 440;
                Settle(window);
                Assert.Equal(1, rail.Dropped);
                Assert.False(first.IsVisible);
                Assert.True(second.IsVisible, "the second droppable went before the first");
                Assert.False(rail.IsStacked);
                Assert.True(lead.Bounds.Right <= trail.Bounds.Left && trail.Bounds.Width >= 260 - 0.5, "the trailing half was squeezed");

                // 300: both gone and the trailing half still does not fit whole beside the rest: two rows, and the
                // leading half takes back what fits the full width (204 - 54 = 150 <= 300).
                window.Width = 300;
                Settle(window);
                Assert.True(rail.IsStacked, "a trailing half that does not fit was squeezed into the row");
                Assert.True(lead.Bounds.Top >= trail.Bounds.Bottom, "stacked, the leading half is not under the trailing one");
                Assert.True(second.IsVisible, "stacked, the leading half dropped what fits the full width");

                window.Width = 500;
                Settle(window);
                Assert.Equal(0, rail.Dropped);
                Assert.True(first.IsVisible && second.IsVisible, "a dropped part did not come back once it fits");

                trail.IsVisible = false;
                Settle(window);
                Assert.False(rail.IsStacked);
                Assert.Equal(28, rail.Bounds.Height, 0.5);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private static void AssertShell(Window window, SoundInspectorView sound, string at)
    {
        var footer = Named(sound, "NoteFooter");
        Assert.True(footer.Bounds is { Width: > 1, Height: > 1 }, $"{at} the footer laid out as {footer.Bounds}");
        Assert.DoesNotContain(footer.GetVisualAncestors(), a => a is ScrollViewer);
        var footerBottom = footer.TranslatePoint(new Point(0, footer.Bounds.Height), window)!.Value.Y;
        Assert.True(Math.Abs(footerBottom - window.Bounds.Height) <= 1, $"{at} the footer ends at {footerBottom:0.#}, not at the pane's foot");
        AssertNothingSpills(footer, $"{at}: the footer");

        var cards = (ColumnFlowPanel)Named(sound, "NoteCards");
        var shown = cards.ShownCards;
        Assert.True(shown.Count >= 1, $"{at} shows no card");
        // Every card of the page, the paged-away ones brought on screen one at a time (P4: a page is several cards).
        var cardsOfPage = cards.Children.OfType<ColumnFlowCard>().Where(c => c.IsVisible).ToList();
        foreach (var card in cardsOfPage)
        {
            if (!cards.ShownCards.Contains(card))
            {
                cards.ShowPage(card);
                Settle(window);
            }

            Assert.True(card.Bounds is { Width: > 1, Height: > 40 }, $"{at} the card '{card.Name}' laid out as {card.Bounds}");
            Assert.True(card.GetVisualDescendants().OfType<Control>().Any(c => c.IsEffectivelyVisible && c.Bounds is { Width: > 1, Height: > 1 }),
                $"{at} the card '{card.Name}' body measured nothing");
        }

        if (cardsOfPage.Count > 0)
        {
            cards.ShowPage(cardsOfPage[0]);
            Settle(window);
        }

        AssertOneRow((OverflowTabStrip)Named(sound, "NoteSectionEntries"), $"{at}: the section strip");
    }

    [Theory]
    [InlineData(530, 370)]
    [InlineData(480, 370)]
    public async Task TheValuesPicker_IsUncut_AndADraftElsewhere_ShowsItsStrip(int width, int height) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, height, 6, (window, view, vm, selection, level) =>
        {
            var sound = view.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            var at = $"at {width}x{height} ({HeadlessSessionFixture.SweptCulture})";
            selection.ClearSelections();
            selection.SelectedNotes.Add(level.Notes[0]);
            selection.SelectedChords.Add(level.Chords.First());
            Settle(window);
            vm.Sections.ChooseCommand.Execute(SoundSection.Selection);
            Settle(window);
            var panel = (ColumnFlowPanel)Named(sound, "NoteCards");
            panel.ShowPage(Named(sound, "NoteSelectionValuesCard"));
            Settle(window);
            var row = Named(sound, "BulkSourceRow");
            Assert.True(row.IsEffectivelyVisible && row.Bounds is { Width: > 1, Height: > 1 }, $"{at} the Values card's first row is {row.Bounds}");
            AssertNothingSpills(Named(sound, "BulkSourcePicker"), $"{at} the Values card's source picker");
            Assert.True(Named(sound, "BulkSustainField").Bounds.Width >= 56, $"{at} the sustain box is under 56 px");

            // A draft on one chord, then a click on another: the strip names it over the footer.
            var chords = level.Chords.Take(2).ToArray();
            selection.ClearSelections();
            selection.SelectedChords.Add(chords[0]);
            selection.SelectedChord = chords[0];
            Settle(window);
            vm.Sections.ChooseCommand.Execute(SoundSection.Technique);
            vm.Staged.ChordTechnique.Accent = !chords[0].Accent;
            selection.ClearSelections();
            selection.SelectedChords.Add(chords[1]);
            selection.SelectedChord = chords[1];
            Settle(window);
            var strip = Named(sound, "NoteDraftsElsewhere");
            var button = Named(sound, "NoteDraftsElsewhereButton");
            Assert.True(strip.IsEffectivelyVisible && button.Bounds is { Width: > 1, Height: > 1 }, $"{at} the draft-elsewhere strip is {strip.Bounds}");
            var footer = Named(sound, "NoteFooter");
            Assert.True(NarrowWidthLayout.TopIn(strip, window) + strip.Bounds.Height <= NarrowWidthLayout.TopIn(footer, window) + 0.5,
                $"{at} the draft-elsewhere strip runs under the footer");
            AssertNothingSpills(strip, $"{at} the draft-elsewhere strip");
            vm.Staged.Engine.DiscardAll();
            vm.Staged.Refresh();
        }), CancellationToken.None);

    private static void AssertOneRow(OverflowTabStrip row, string at)
    {
        Assert.True(row.Bounds.Height is > 1 and <= 34, $"{at} is {row.Bounds.Height:0.#} px tall");
        foreach (var tab in row.ShownTabs)
        {
            var left = tab.TranslatePoint(default, row)!.Value.X;
            Assert.True(left >= -0.5 && left + tab.Bounds.Width <= row.Bounds.Width + 0.5,
                $"{at}: '{tab.Name ?? tab.Tag}' spans {left:0.#}..{left + tab.Bounds.Width:0.#} in {row.Bounds.Width:0.#} px");
        }
    }

    /// <summary>No text in the footer is cut without a plan for it (wrap or ellipsis).</summary>
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

    private static Control Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no control named '{name}' — repoint this test.");

    private static void WithPane(double width, double height, int strings,
        Action<Window, InspectorView, InspectorViewModel, ISelectionService, ChartLevel> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();
        var vm = services.GetRequiredService<InspectorViewModel>();
        var (project, arrangement, tab) = (state.ActiveProject, state.ActiveArrangement, vm.SelectedTabIndex);
        var applyStep = state.InspectorApplyStep;
        Window? window = null;
        try
        {
            // These cases pin the staged pages (#751): Settings ▸ Editor's Apply step ON (#805 made it off by default).
            state.InspectorApplyStep = true;
            var chart = StressChartFactory.Chart(soundCount: 60, lengthMinutes: 1);
            var level = new ChartLevel { DifficultyIndex = 0 };
            foreach (var note in chart.Notes) level.Notes.Add(note);
            foreach (var chord in chart.Chords) level.Chords.Add(chord);
            var lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = strings };
            lead.Levels.Add(level);
            var song = new ChartProject();
            foreach (var beat in chart.Beats) song.Beats.Add(beat);
            song.Arrangements.Add(lead);
            state.ActiveProject = song;
            state.ActiveArrangement = lead;
            vm.SelectedTabIndex = 0;

            var view = new InspectorView { DataContext = vm };
            window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            Settle(window);
            body(window, view, vm, selection, level);
        }
        finally
        {
            state.InspectorApplyStep = applyStep;
            selection.ClearSelections();
            vm.SelectedTabIndex = tab;
            window?.Close();
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
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

    /// <summary>An <see cref="DotCharter.Presentation.Dialogs.IDialogs" /> that records which screen was asked for.</summary>
    private sealed class RecordingDialogs : DotCharter.Presentation.Dialogs.IDialogs
    {
        public List<Type> Opened { get; } = [];

        public bool HasOpenDialog => false;

        public event EventHandler<DotCharter.Presentation.Dialogs.DialogChange>? Changed
        {
            add { }
            remove { }
        }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, DotCharter.Presentation.Dialogs.IDialogScreen<TResult>
        {
            Opened.Add(typeof(TContract));
            return Task.FromResult(default(TResult));
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

        public bool CloseTop() => false;
    }
}
