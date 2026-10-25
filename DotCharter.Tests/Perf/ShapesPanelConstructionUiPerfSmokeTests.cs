using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Shapes panel, built for real: the app's own inspector view model (so the screen is
///     proven REACHED through the composition root, not just registered) over a messy seeded
///     arrangement (duplicates, unused and unfingered shapes, long and missing names), attached,
///     laid out at the inspector's default size, every card realized and measured.
///     <para>
///         The declutter, pinned: a card shows its diagram, name and use count; the actions, the
///         problem and its fix are in reserved strips that show on the hovered, focused or
///         current card alone, and the card's height never changes when they do. Nothing on a
///         card is truncated or runs past it, in German and Japanese too, at the pane's widths.
///     </para>
///     <para>
///         And the palette: a real pointer click on a card's chord diagram places that chord at
///         the playhead.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapesPanelConstructionUiPerfSmokeTests
{
    /// <summary>
    ///     The Inspector pane at the app's default split on a 1920 x 1080 window: a third of what
    ///     the 76 px activity rail and the 6 px splitter leave, about 612 px, rounded down.
    /// </summary>
    private const double DefaultInspectorWidth = 600;

    /// <summary>The inspector's height there, less the dock's own tab header.</summary>
    private const double DefaultInspectorHeight = 524;

    /// <summary>The Shapes tab's own height inside that pane, the inspector's tab strip taking the rest.</summary>
    private const double DefaultTabHeight = 470;

    /// <summary>Three whole cards in view at the default size (the decluttered card is shorter than the old row).</summary>
    private const int MinVisibleCards = 3;

    public static TheoryData<string, double> AcceptanceCases =>
    [
        .. from variant in new[] { "Default", "NeedsAttention", "LongText", "Full", "Empty" }
        from width in new[] { 320.0, 420.0, 600.0 }
        select (variant, width)
    ];

    [Fact]
    public Task TheInspectorsShapesTab_OverARealArrangement_ShowsPlainCardsAndTheirActionsOnDemand() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var screen = Assert.IsType<ShapesPanelViewModel>(inspector.ChordShapes);
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex,
                state.CurrentTimeMs);
            Window? window = null;
            try
            {
                var arrangement = Seed(state, templates: 60);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Assert.Equal(arrangement.ChordTemplates.Count, screen.Rows.Count);

                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(DefaultInspectorWidth, DefaultInspectorHeight, view);
                window.Show();
                Settle(window);

                NarrowWidthLayout.Capture(window, "shapes-panel-real");
                var tab = view.GetVisualDescendants().OfType<ShapesPanelView>().Single();
                Assert.True(tab.IsEffectivelyVisible, "the Shapes tab is not the visible panel");
                NarrowWidthLayout.Report($"shapes tab in the default inspector: {tab.Bounds.Width:0} x {tab.Bounds.Height:0}");
                Assert.True(tab.Bounds.Height >= DefaultTabHeight - 0.5,
                    $"the tab is {tab.Bounds.Height:0.#} px tall in the default inspector; the variant cases assume {DefaultTabHeight}");
                AssertCardsLaidOut(tab, arrangement.ChordTemplates.Count, DefaultInspectorWidth);
                AssertListIsTheOnlyScrollRegion(tab, MinVisibleCards);
                AssertNothingTruncated(tab, "the real tab");
                AssertActionsRevealOnDemand(window, tab);

                // The strips open on the cards in view, in the culture the run is in: nothing trimmed or overflowing.
                var open = Cards(tab).Take(6).ToList();
                foreach (var card in open) card.Classes.Add("current");
                Settle(window);
                NarrowWidthLayout.Capture(window, "shapes-panel-real-open");
                AssertNothingTruncated(tab, "the real tab, strips open");
                foreach (var card in open) card.Classes.Remove("current");
                Settle(window);

                // A problem's fix, by real clicks: hover the card, press its Merge, read the one bar, say yes.
                var copy = screen.AllRows.First(r => r.Issue.Kind == ChordShapeIssueKind.SameFrets);
                var copyCard = CardOf(tab, copy);
                Hover(window, copyCard);
                var fix = ButtonIn(copyCard, "ShapeFix");
                Assert.True(fix.IsEffectivelyVisible && fix.Bounds is { Width: > 1, Height: > 1 }, $"the Merge fix laid out {fix.Bounds}");
                Click(window, fix);
                Settle(window);
                var bar = tab.FindControl<Border>("ShapesPendingBar")!;
                Assert.True(bar.IsEffectivelyVisible && bar.Bounds.Height > 1, "the confirmation bar did not lay out");
                Assert.Equal(arrangement.ChordTemplates.Count, screen.AllRows.Count);
                var shapesBeforeMerge = screen.Rows.Count;
                Click(window, tab.FindControl<Button>("ShapesConfirm")!);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Settle(window);
                Assert.DoesNotContain(copy, screen.AllRows);
                Assert.Equal(shapesBeforeMerge - 1, screen.Rows.Count);
                Assert.False(bar.IsEffectivelyVisible, "the bar stayed after the merge");
                Assert.StartsWith(Loc("ChordShapes_Status_Merged").Split('{')[0], state.MainDisplayMessage, StringComparison.Ordinal);

                // Clean up opens its dialog; applying it takes every unused card and every identical copy in one step.
                var cleanUp = tab.FindControl<Button>("ShapesCleanUp")!;
                Assert.True(cleanUp.IsEffectivelyVisible && cleanUp.Bounds is { Width: > 1, Height: > 1 }, $"Clean up laid out {cleanUp.Bounds}");
                var found = ShapeCleanupFinder.Find(state.ActiveArrangement!);
                var gone = found.Unused.Count + found.Merges.Sum(m => m.Copies.Count);
                Assert.True(gone > 0, "the chart has nothing for Clean up to remove");
                var cardCount = screen.Rows.Count;
                var slot = services.GetRequiredService<DialogsSlot>();
                using var dialogScope = services.CreateScope();
                var dialogs = new ScriptedDialogs(_ => dialogScope.ServiceProvider.GetRequiredService<IShapeCleanupScreen>(),
                    dialog => ((IShapeCleanupScreen)dialog).ApplyCommand.Execute(null));
                slot.Attach(dialogs);
                try
                {
                    Click(window, cleanUp);
                }
                finally
                {
                    slot.Detach(dialogs);
                }

                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Settle(window);
                Assert.IsAssignableFrom<IShapeCleanupScreen>(Assert.Single(dialogs.Opened));
                Assert.Equal(cardCount - gone, screen.Rows.Count);
                Assert.Equal(!ShapeCleanupFinder.Find(state.ActiveArrangement!).IsTidy, cleanUp.IsEffectivelyVisible);

                // The action strip: Place and Next use sit in it on a used shape's hovered card.
                var used = screen.AllRows.First(r => !r.IsUnused);
                var usedCard = CardOf(tab, used);
                Hover(window, usedCard);
                foreach (var name in new[] { "ShapePlaceAction", "ShapeNextUse" })
                {
                    var action = ButtonIn(usedCard, name);
                    Assert.True(action.IsEffectivelyVisible && action.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out {action.Bounds}");
                }

                Assert.False(ButtonIn(usedCard, "ShapeSplit").IsVisible, "Split off selection is offered with nothing selected");

                // The filter: the toggle next to the summary narrows the list to the cards with a problem.
                var all = screen.Rows.Count;
                var needing = screen.AllRows.Count(r => r.HasIssue);
                Assert.InRange(needing, 1, all - 1);
                var toggle = tab.FindControl<ToggleButton>("ShapesAttentionOnly")!;
                Assert.True(toggle.IsEffectivelyVisible && toggle.Bounds is { Width: > 1, Height: > 1 }, $"the filter laid out {toggle.Bounds}");
                Click(window, toggle);
                Settle(window);
                Assert.Equal(needing, screen.Rows.Count);
                Assert.All(screen.Rows, r => Assert.True(r.HasIssue));
                Click(window, toggle);
                Settle(window);
                Assert.Equal(all, screen.Rows.Count);

                // The palette: CLICK the first card's chord diagram, it must not swallow the press.
                var level = arrangement.Levels[0];
                var at = new FractionalPosition(61);
                Assert.DoesNotContain(level.Chords, c => c.Position == at);
                state.CurrentTimeMs = at.GetPositionMs(state.Beats);
                selection.ClearSelections();
                var first = (ShapePanelRowViewModel)screen.Rows[0];
                tab.FindControl<ScrollViewer>("ShapesScroll")!.Offset = default;
                Settle(window);
                var box = tab.FindControl<ItemsControl>("ShapesRows")!.ContainerFromIndex(0)!
                    .GetVisualDescendants().OfType<ChordDiagramControl>().First();
                var point = box.TranslatePoint(new Point(box.Bounds.Width * 0.7, box.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Settle(window);

                var placed = Assert.Single(level.Chords, c => c.Position == at);
                Assert.Equal(first.Template.Id, placed.TemplateId);
                AssertListIsTheOnlyScrollRegion(tab, MinVisibleCards);
            }
            finally
            {
                window?.Close();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.CurrentTimeMs = previous.CurrentTimeMs;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
            }
        }, CancellationToken.None);

    /// <summary>
    ///     A card that offers Split off selection and Merge here (the selection and the merge being
    ///     chosen offer them, not hover) reserves room for both: hovering it still changes no height
    ///     and no label runs past the card. Captures <c>shapes-hover-extras</c>.
    /// </summary>
    [Fact]
    public Task AHoveredCard_WithSplitAndMergeHere_KeepsItsHeight_AndItsStripFitsTheReserve() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var screen = Assert.IsType<ShapesPanelViewModel>(inspector.ChordShapes);
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex);
            Window? window = null;
            try
            {
                var arrangement = Seed(state, templates: 60);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(DefaultInspectorWidth, DefaultInspectorHeight, view);
                window.Show();
                Settle(window);
                var tab = view.GetVisualDescendants().OfType<ShapesPanelView>().Single();

                // Template 3 repeats template 2's frets: choosing a merge for 3 lets 2 take it, and a selected
                // chord of template 2 offers Split there too.
                var target = screen.AllRows.Single(r => r.Template.Id == 2);
                var source = screen.AllRows.Single(r => r.Template.Id == 3);
                var chord = arrangement.Levels[0].Chords.First(c => c.TemplateId == 2);
                var away = tab.FindControl<TextBox>("ShapesSearch")!.TranslatePoint(new Point(4, 4), window)!.Value;

                void HoverAndCheck(string what, string[] offered, string capture)
                {
                    inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex; // selecting a chord moves the inspector to its own tab
                    Settle(window);
                    var card = CardOf(tab, target);
                    window.MouseMove(away);
                    Settle(window);
                    var plain = card.Bounds.Height;
                    Hover(window, card);
                    var scroll = tab.FindControl<ScrollViewer>("ShapesScroll")!;
                    scroll.Offset = new Vector(0, Math.Max(0, card.TranslatePoint(default, (Visual)scroll.Content!)!.Value.Y - 4)); // the card at the top, for the capture
                    Settle(window);
                    Hover(window, card);
                    NarrowWidthLayout.Capture(window, capture);
                    Assert.True(Revealed(card).Count > 0, $"{what}: nothing revealed");
                    Assert.Equal(plain, card.Bounds.Height);
                    foreach (var name in offered) Assert.True(ButtonIn(card, name).IsEffectivelyVisible, $"{what}: {name} is not offered");
                    var strip = card.GetVisualDescendants().OfType<WrapPanel>().Single(w => w.Classes.Contains("ShapeReveal"));
                    var reserve = ((Control)strip.Parent!).Bounds.Height;
                    Assert.True(strip.Bounds.Height <= reserve + 0.5, $"{what}: the strip is {strip.Bounds.Height:0.#} px tall in a {reserve:0.#} px reserve");
                    AssertNothingTruncated(tab, what);
                }

                // Split off selection alone appears with the selection, not the hover.
                selection.SelectedChords.Add(chord);
                selection.SelectedChord = chord;
                Assert.True(target.CanSplitSelection, "the card does not offer Split off selection");
                HoverAndCheck("a hovered card with Split", ["ShapeSplit", "ShapeMerge"], "shapes-hover-split");

                // And Merge here joins it once the merge of the duplicate is being chosen.
                source.BeginMergeCommand.Execute(null);
                Assert.True(target.CanMergeHere, "the card does not offer Merge here");
                HoverAndCheck("a hovered card with Split and Merge here", ["ShapeMergeHere", "ShapeSplit", "ShapeMerge", "ShapeDelete"], "shapes-hover-extras");
            }
            finally
            {
                window?.Close();
                selection.ClearSelections();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The owner's acceptance per variant and width, at the default tab height, with every
    ///     card's reveal strips forced open: the header is pinned, the list shows whole cards,
    ///     nothing is trimmed or runs past its card, the diagrams stay readable. Captures
    ///     <c>shapes-&lt;variant&gt;-&lt;width&gt;</c> with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptanceCases))]
    public Task AtTheDefaultSize_TheHeaderIsPinned_AndNothingOnACardIsTruncated(string variant, double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeShapePanelScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new ShapesPanelView { DataContext = screen };
            var window = NarrowWidthLayout.NewWindow(width, DefaultTabHeight, view);
            window.Show();
            try
            {
                Settle(window);
                NarrowWidthLayout.Capture(window, $"shapes-{variant}-{width:0}");
                if (!screen.HasRows)
                {
                    Assert.True(view.FindControl<TextBlock>("ShapesEmpty")!.Bounds.Height > 1, $"{variant}: no empty text");
                    return;
                }

                AssertNothingTruncated(view, $"{variant} @ {width:0}");
                AssertDiagramsReadable(view, $"{variant} @ {width:0}");
                var stress = variant is "LongText" or "NeedsAttention" ? 1 : 0;
                var minCards = width < 420 ? 1 : Math.Max(1, MinVisibleCards - stress - (width < 600 ? 1 : 0));
                AssertListIsTheOnlyScrollRegion(view, Math.Min(minCards, screen.Rows.Count));

                // Every card with its strips open, which is the widest a card ever is.
                foreach (var card in Cards(view)) card.Classes.Add("current");
                Settle(window);
                NarrowWidthLayout.Capture(window, $"shapes-{variant}-{width:0}-open");
                AssertNothingTruncated(view, $"{variant} @ {width:0}, every card open");

                var scroll = view.FindControl<ScrollViewer>("ShapesScroll")!;
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                Settle(window);
                NarrowWidthLayout.Capture(window, $"shapes-{variant}-{width:0}-end");
                AssertNothingTruncated(view, $"{variant} @ {width:0}, scrolled to the end");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     Hovered, focused or current: the strips show on that card alone, the card keeps its
    ///     height, and a pointer that leaves takes a hover back but not a current card.
    /// </summary>
    private static void AssertActionsRevealOnDemand(Window window, ShapesPanelView tab)
    {
        var cards = Cards(tab).Take(4).ToList();
        Assert.True(cards.Count >= 4, "fewer than four cards to test the reveal on");
        var away = tab.FindControl<TextBox>("ShapesSearch")!.TranslatePoint(new Point(4, 4), window)!.Value;
        window.MouseMove(away);
        Settle(window);
        Assert.All(cards, c => Assert.Empty(Revealed(c)));
        var heights = cards.Select(c => c.Bounds.Height).ToList();

        Hover(window, cards[1]);
        Assert.NotEmpty(Revealed(cards[1]));
        Assert.Empty(Revealed(cards[0]));
        Assert.Empty(Revealed(cards[2]));
        Assert.Equal(heights, cards.Select(c => c.Bounds.Height).ToList());
        Assert.True(ButtonIn(cards[1], "ShapeSelectUses").Bounds is { Width: > 1, Height: > 1 }, "Select uses did not lay out on the hovered card");

        window.MouseMove(away);
        Settle(window);
        Assert.Empty(Revealed(cards[1]));

        // A press on the card's own padding makes it the current card: it keeps its strips after the pointer leaves.
        Press(window, cards[2]);
        window.MouseMove(away);
        Settle(window);
        Assert.NotEmpty(Revealed(cards[2]));
        Assert.Equal(heights, cards.Select(c => c.Bounds.Height).ToList());

        Press(window, cards[3]);
        window.MouseMove(away);
        Settle(window);
        Assert.Empty(Revealed(cards[2]));
        Assert.NotEmpty(Revealed(cards[3]));
        Assert.Equal(heights, cards.Select(c => c.Bounds.Height).ToList());

        // The keyboard gets there too: focusing the name box makes its card current.
        cards[0].GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "ShapeName").Focus();
        Settle(window);
        Assert.NotEmpty(Revealed(cards[0]));
        Assert.Empty(Revealed(cards[3]));
        Assert.Equal(heights, cards.Select(c => c.Bounds.Height).ToList());
    }

    private static IEnumerable<Border> Cards(Control view) =>
        view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ShapeCard");

    private static Border CardOf(ShapesPanelView tab, ShapePanelRowViewModel row)
    {
        var list = tab.FindControl<ItemsControl>("ShapesRows")!;
        var index = list.Items.Cast<object>().ToList().IndexOf(row);
        var card = list.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ShapeCard");
        card.BringIntoView();
        return card;
    }

    private static Button ButtonIn(Control card, string name) =>
        card.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);

    /// <summary>The reveal strips of a card that are on screen.</summary>
    private static List<Control> Revealed(Control card) =>
        [.. card.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("ShapeReveal") && c.IsEffectivelyVisible)];

    private static Point CentreOf(Control control, Visual root) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    private static void Hover(Window window, Control card)
    {
        // The card's top-right padding, which is no control's own.
        card.BringIntoView();
        Settle(window);
        window.MouseMove(card.TranslatePoint(new Point(card.Bounds.Width - 6, 6), window)!.Value);
        Settle(window);
    }

    /// <summary>A press on the card's right padding: no control of its own, so it reaches the card.</summary>
    private static void Press(Window window, Control card)
    {
        card.BringIntoView();
        Settle(window);
        var at = card.TranslatePoint(new Point(card.Bounds.Width - 3, 3), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle(window);
    }

    private static void Click(Window window, Control control)
    {
        var at = CentreOf(control, window);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    ///     The list scrolls and the header does not; at least <paramref name="minCards" /> whole
    ///     cards are in view; the list's scrollbar is drawn (not auto-hidden) while there is more.
    /// </summary>
    private static void AssertListIsTheOnlyScrollRegion(ShapesPanelView view, int minCards)
    {
        var scroll = view.FindControl<ScrollViewer>("ShapesScroll")!;
        var list = view.FindControl<ItemsControl>("ShapesRows")!;
        var header = view.FindControl<Border>("ShapesHeader")!;
        var summary = view.FindControl<TextBlock>("ShapesSummary")!;
        Assert.False(scroll.AllowAutoHide, "the list's scrollbar auto-hides: nothing says the list goes on");
        Assert.DoesNotContain(header.GetVisualAncestors(), a => a is ScrollViewer);

        var viewportTop = scroll.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        var viewportBottom = viewportTop + scroll.Viewport.Height;
        var cards = list.GetRealizedContainers().ToList();
        var whole = cards.Count(r =>
        {
            var top = r.TranslatePoint(new Point(0, 0), view)!.Value.Y;
            return top >= viewportTop - 0.5 && top + r.Bounds.Height <= viewportBottom + 0.5;
        });
        var heights = string.Join(", ", cards.Take(4).Select(r => r.Bounds.Height.ToString("0")));
        NarrowWidthLayout.Report($"shapes {view.Bounds.Width:0}x{view.Bounds.Height:0}: header {header.Bounds.Height:0} px, " +
                                 $"viewport {scroll.Viewport.Height:0} px, {whole} whole cards in view (card heights {heights})");
        Assert.True(whole >= minCards,
            $"{whole} whole cards in view at {view.Bounds.Width:0} x {view.Bounds.Height:0} (header {header.Bounds.Height:0} px, " +
            $"cards {heights} px); the acceptance is {minCards}");

        if (scroll.Extent.Height <= scroll.Viewport.Height + 1) return;
        var summaryTop = summary.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        var firstTop = cards[0].TranslatePoint(new Point(0, 0), view)!.Value.Y;
        scroll.Offset = new Vector(0, Math.Min(150, scroll.Extent.Height - scroll.Viewport.Height));
        view.UpdateLayout();
        Assert.Equal(summaryTop, summary.TranslatePoint(new Point(0, 0), view)!.Value.Y, 1);
        Assert.True(cards[0].TranslatePoint(new Point(0, 0), view)!.Value.Y < firstTop - 10, "the list did not scroll under the header");
        scroll.Offset = default;
        view.UpdateLayout();
    }

    /// <summary>
    ///     Nothing on screen is trimmed (no trimming, no line cap), every text box and block
    ///     shows all of its text, and nothing runs past the card that holds it.
    /// </summary>
    private static void AssertNothingTruncated(Control root, string what)
    {
        var problems = new List<string>();
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
        {
            if (text.TextTrimming != TextTrimming.None) problems.Add($"'{text.Text}' is trimmed ({text.TextTrimming})");
            if (text.MaxLines > 0) problems.Add($"'{text.Text}' is capped at {text.MaxLines} lines");
            if (string.IsNullOrEmpty(text.Text) || text.Bounds.Width <= 1) continue;
            var layout = text.TextLayout;
            if (layout.Height > text.Bounds.Height - text.Padding.Top - text.Padding.Bottom + 1)
                problems.Add($"'{text.Text}' needs {layout.Height:0.#} px of height in {text.Bounds.Height:0.#}");
            if (layout.Width > text.Bounds.Width - text.Padding.Left - text.Padding.Right + 1)
                problems.Add($"'{text.Text}' needs {layout.Width:0.#} px of width in {text.Bounds.Width:0.#}");
        }

        foreach (var box in root.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Name == "ShapeName"))
        {
            if (box.TextWrapping != TextWrapping.Wrap) problems.Add($"name box '{box.Text}' does not wrap");
            var presenter = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
            if (presenter is null || string.IsNullOrEmpty(box.Text)) continue;
            if (presenter.TextLayout.Height > presenter.Bounds.Height + 1)
                problems.Add($"name '{box.Text}' needs {presenter.TextLayout.Height:0.#} px of height in {presenter.Bounds.Height:0.#}");
            if (presenter.TextLayout.Width > presenter.Bounds.Width + 1)
                problems.Add($"name '{box.Text}' needs {presenter.TextLayout.Width:0.#} px of width in {presenter.Bounds.Width:0.#}");
        }

        foreach (var card in Cards(root).Where(c => c.IsEffectivelyVisible))
        {
            var cardWidth = card.Bounds.Width;
            foreach (var part in card.GetVisualDescendants().OfType<Control>()
                         .Where(c => c is TextBlock or Button or CheckBox or TextBox && c.IsEffectivelyVisible))
            {
                var right = part.TranslatePoint(new Point(part.Bounds.Width, 0), card)?.X ?? 0;
                if (right > cardWidth + 0.5) problems.Add($"{part.GetType().Name} '{(part as TextBlock)?.Text}' ends at {right:0.#} in a {cardWidth:0.#} px card");
            }
        }

        Assert.True(problems.Count == 0, $"{what}: truncated or overflowing text: {string.Join("; ", problems.Take(8))}");
    }

    /// <summary>Every drawn diagram: the fret label left of the grid, o / x above it, dots apart.</summary>
    private static void AssertDiagramsReadable(Control root, string what)
    {
        var diagrams = root.GetVisualDescendants().OfType<ChordDiagramControl>().Where(d => d.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(diagrams);
        foreach (var diagram in diagrams)
        {
            var layout = diagram.CurrentLayout();
            var name = $"{what}: diagram {diagram.Bounds.Width:0}x{diagram.Bounds.Height:0} from fret {layout.StartFret}";
            Assert.True(layout.DotRadius * 2 < Math.Min(layout.StringSpacing, layout.FretSpacing),
                $"{name}: dots {layout.DotRadius * 2:0.#} px wide on a {layout.StringSpacing:0.#} x {layout.FretSpacing:0.#} grid touch");
            Assert.True(layout.GridLeft - layout.DotRadius >= -0.5 && layout.GridRight + layout.DotRadius <= diagram.Bounds.Width + 0.5,
                $"{name}: the outer strings' dots run off the diagram");
            Assert.True(layout.MarkerY + layout.FontSize * 0.3 <= layout.GridTop, $"{name}: the o / x row sits on the nut");
            if (layout.StartFret <= 1) continue;
            Assert.True(layout.LabelRect.Right <= layout.GridLeft - layout.DotRadius,
                $"{name}: the fret label ends at {layout.LabelRect.Right:0.#}, into the first string's dots at {layout.GridLeft - layout.DotRadius:0.#}");
            Assert.True(layout.LabelRect.Left >= 0, $"{name}: the fret label starts outside the diagram");
        }
    }

    private static string Loc(string key) => DotCharter.Localization.LocalizationService.Instance[key];

    private static void AssertCardsLaidOut(ShapesPanelView view, int expectedCards, double width)
    {
        var summary = view.FindControl<TextBlock>("ShapesSummary")!;
        Assert.True(summary.Bounds.Height > 1, $"the summary laid out as {summary.Bounds}");

        var list = view.FindControl<ItemsControl>("ShapesRows")!;
        var cards = list.GetRealizedContainers().ToList();
        Assert.Equal(expectedCards, cards.Count);
        Assert.All(cards, r => Assert.True(r.Bounds is { Width: > 1, Height: > 40 }, $"a card laid out as {r.Bounds}"));

        var boxes = list.GetVisualDescendants().OfType<ChordDiagramControl>().ToList();
        Assert.Equal(expectedCards, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.Bounds is { Width: > 60, Height: > 60 }, $"a chord diagram laid out as {b.Bounds}"));

        // The name box and the use count are what a card shows: they laid out on every card.
        Assert.All(Cards(list).Take(12), c =>
        {
            Assert.True(c.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "ShapeName").Bounds.Height > 1, "a name box did not lay out");
            Assert.True(c.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "ShapeUses").Bounds.Height > 1, "a use count did not lay out");
        });

        foreach (var card in cards.Take(12))
        {
            foreach (var part in card.GetVisualDescendants().OfType<Control>().Where(c => c is TextBlock or Button && c.IsEffectivelyVisible))
            {
                var right = part.TranslatePoint(new Point(part.Bounds.Width, 0), view)?.X ?? 0;
                Assert.True(right <= width + 0.5, $"{part.GetType().Name} '{(part as TextBlock)?.Text}' ends at {right:0.#} px in a {width} px pane");
            }
        }
    }

    private static Arrangement Seed(ProjectState state, int templates)
    {
        var project = new ChartProject { Chart = { SongName = "Shapes panel fixture" } };
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        string[] names =
        [
            "Am", "C", "G", "Cmaj7#11 (from the import, check the voicing before export)",
            "Gesperrter Septakkord mit Barré im neunten Bund", "ドロップDのパワーコード(ブリッジミュート)", ""
        ];
        for (var i = 0; i < templates; i++)
        {
            // Every fourth template repeats the previous one's frets: a duplicate pair.
            var shift = i % 4 == 3 ? (i - 1) % 9 : i % 9;
            var template = new ChordTemplate { Id = i, Name = names[i % names.Length] };
            template.Frets[1] = 1 + shift;
            template.Frets[2] = 3 + shift;
            template.Frets[3] = 3 + shift;
            if (i % 5 != 0) ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
            if (i % 7 == 0) template.Arpeggio = true;
            arrangement.ChordTemplates.Add(template);

            if (i % 6 == 5) continue;
            var chord = new Chord { Position = new FractionalPosition(i % 60), TemplateId = i };
            foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            level.Chords.Add(chord);
        }

        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = level;
        return arrangement;
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
