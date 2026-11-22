using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
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
///     The Chord shapes tab (inspector design D), built for real: the app's own inspector view model (so the screen is
///     proven REACHED through the composition root) over a messy seeded arrangement (duplicates, unused and unfingered
///     shapes, long and missing names), attached and laid out at 530 x 370 (the design's primary size), 480 x 370 and
///     the default 600 x 524 inspector.
///     <list type="bullet">
///         <item>The cards on screen measure non-zero; the list keeps its own scroller under a pinned head; a line
///         pressed becomes the current shape and the Shape page shows it.</item>
///         <item>Every verb by real clicks: the problem's Merge fix waits in the Checked page's bar (the page comes on
///         screen) and one yes merges; the attention chip filters; Clean up opens its dialog; the Shape page's diagram
///         places the shape at the playhead; on the Uses page a tick selects that use and Split uses gives the ticked
///         uses their own copy.</item>
///         <item>Over every fake variant and page at 320-600 px: no text needs more height than it has and nothing
///         runs past its card; the Shape page's diagram stays readable.</item>
///     </list>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapesPanelConstructionUiPerfSmokeTests
{
    private const double MinNameWidth = 48;

    private static readonly string[] Pages = ["ShapesListCard", "ShapesShapeCard", "ShapesCheckedCard", "ShapesUsesCard"];

    public static TheoryData<string, double, double> AcceptanceCases
    {
        get
        {
            var data = new TheoryData<string, double, double>();
            foreach (var variant in new[] { "Default", "NeedsAttention", "LongText", "Full", "Empty", "ConfirmMerge", "MultiSelect", "ExtendedRange" })
            {
                foreach (var (width, height) in new[] { (320.0, 370.0), (480.0, 370.0), (530.0, 370.0), (600.0, 470.0) })
                    data.Add(variant, width, height);
            }

            return data;
        }
    }

    /// <summary>The shipped translations at the design's two narrow sizes, over the widest patterns and the longest names.</summary>
    public static TheoryData<string, string, double> CultureCases
    {
        get
        {
            var data = new TheoryData<string, string, double>();
            foreach (var culture in new[] { "de-DE", "ja-JP" })
            {
                foreach (var variant in new[] { "ExtendedRange", "LongText", "NeedsAttention" })
                {
                    foreach (var width in new[] { 480.0, 530.0 })
                        data.Add(variant, culture, width);
                }
            }

            return data;
        }
    }

    [Theory]
    [InlineData(530, 370, 6)]
    [InlineData(480, 370, 6)]
    [InlineData(600, 524, 6)]
    [InlineData(530, 370, 8)]
    [InlineData(480, 370, 7)]
    public Task TheInspectorsShapesTab_OverARealArrangement_LaysOutItsCards_AndEveryVerbWorks(double width, double height, int strings) =>
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
                var arrangement = Seed(state, templates: 60, strings);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Assert.Equal(arrangement.ChordTemplates.Count, screen.Rows.Count);

                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(width, height, view);
                window.Show();
                Settle(window);
                var at = $"{width:0}x{height:0}-{strings}";
                NarrowWidthLayout.Capture(window, $"shapes-panel-real-{at}");
                var tab = view.GetVisualDescendants().OfType<ShapesPanelView>().Single();
                Assert.True(tab.IsEffectivelyVisible, "the Shapes tab is not the visible panel");
                AssertCardsLaidOut(tab, at);
                AssertListIsTheOnlyScrollRegion(tab, minLines: 3);

                // A line pressed becomes the current shape; the Shape page shows it.
                var copy = screen.AllRows.First(r => r.Issue.Kind == ChordShapeIssueKind.SameFrets);
                ShapesPanelCards.Card(tab, copy).BringIntoView();
                Settle(window);
                Click(window, Named<TextBlock>(ShapesPanelCards.Card(tab, copy), "ShapeFrets"));
                Settle(window);
                Assert.Same(copy, screen.CurrentRow);
                Assert.True(copy.IsCurrent, "the pressed line is not the current one");
                ShowCard(tab, "ShapesShapeCard");
                Settle(window);
                Assert.Same(copy, tab.FindControl<StackPanel>("ShapeDetail")!.DataContext);
                AssertNothingOverflows(tab, $"{at}: the Shape page");

                // An unused shape's Delete is its next step, so it stands out; a used shape's does not.
                Assert.False(Named<Button>(tab, "ShapeDelete").Classes.Contains("ColumnFlowPrimary"), "a used shape's Delete is highlighted");
                screen.CurrentRow = screen.AllRows.First(r => r.IsUnused);
                Settle(window);
                Assert.True(Named<Button>(tab, "ShapeDelete").Classes.Contains("ColumnFlowPrimary"), "an unused shape's Delete is not highlighted");
                screen.CurrentRow = copy;
                Settle(window);

                // The problem's fix, by real clicks: Merge waits in the Checked page's bar, one yes merges.
                var fix = Named<Button>(tab, "ShapeFix");
                Assert.True(fix.IsEffectivelyVisible && fix.Bounds is { Width: > 1, Height: > 1 }, $"the Merge fix laid out {fix.Bounds}");
                Click(window, fix);
                Settle(window);
                AssertShown(tab, "ShapesCheckedCard", $"{at}: a verb waiting for its yes");
                var bar = tab.FindControl<StackPanel>("ShapesPendingBar")!;
                Assert.True(bar.IsEffectivelyVisible && bar.Bounds.Height > 1, "the confirmation bar did not lay out");
                var shapesBeforeMerge = screen.Rows.Count;
                AssertOnScreen(tab, "ShapesCheckedCard", tab.FindControl<Button>("ShapesConfirm")!, $"{at}: the merge's yes");
                Click(window, tab.FindControl<Button>("ShapesConfirm")!);
                Settle(window);
                Assert.DoesNotContain(copy, screen.AllRows);
                Assert.Equal(shapesBeforeMerge - 1, screen.Rows.Count);
                Assert.False(bar.IsEffectivelyVisible, "the bar stayed after the merge");

                // The filter: the chip narrows the list to the shapes with a problem.
                ShowCard(tab, "ShapesListCard");
                Settle(window);
                var all = screen.Rows.Count;
                var needing = screen.AllRows.Count(r => r.HasIssue);
                Assert.InRange(needing, 1, all - 1);
                var toggle = tab.FindControl<ToggleButton>("ShapesAttentionOnly")!;
                Click(window, toggle);
                Settle(window);
                Assert.Equal(needing, screen.Rows.Count);
                Click(window, toggle);
                Settle(window);
                Assert.Equal(all, screen.Rows.Count);

                // Clean up opens its dialog; applying it takes every unused shape and every identical copy in one step.
                var cleanUp = tab.FindControl<Button>("ShapesCleanUp")!;
                var found = ShapeCleanupFinder.Find(state.ActiveArrangement!);
                var gone = found.Unused.Count + found.Merges.Sum(m => m.Copies.Count);
                Assert.True(gone > 0 && cleanUp.IsEnabled, "the chart has nothing for Clean up to remove");
                var slot = services.GetRequiredService<DialogsSlot>();
                using (var dialogScope = services.CreateScope())
                {
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

                    Settle(window);
                    Assert.IsAssignableFrom<IShapeCleanupScreen>(Assert.Single(dialogs.Opened));
                }

                Assert.Equal(all - gone, screen.Rows.Count);

                // The palette: the Shape page's diagram places the current shape at the playhead.
                var level = arrangement.Levels[0];
                var place = new FractionalPosition(61);
                Assert.DoesNotContain(level.Chords, c => c.Position == place);
                state.CurrentTimeMs = place.GetPositionMs(state.Beats);
                selection.ClearSelections();
                var current = (ShapePanelRowViewModel)screen.Rows[0];
                screen.CurrentRow = current;
                ShowCard(tab, "ShapesShapeCard");
                Settle(window);
                var diagram = Named<Button>(tab, "ShapePlace");
                Assert.True(diagram.Bounds is { Width: > 40, Height: > 40 }, $"the diagram laid out {diagram.Bounds}");
                Click(window, diagram);
                Settle(window);
                Assert.Equal(current.Template.Id, Assert.Single(level.Chords, c => c.Position == place).TemplateId);

                // Uses: a tick is the selection, Split uses gives the ticked ones their own copy.
                selection.ClearSelections();
                screen.CurrentRow = screen.AllRows.Single(r => r.Template.Id == 2);
                ShowCard(tab, "ShapesUsesCard");
                Settle(window);
                NarrowWidthLayout.Capture(window, $"shapes-panel-uses-{at}");
                Assert.True(screen.Uses.Count >= 3, $"{screen.Uses.Count} uses of the shape on the level");
                var ticks = Named<ItemsControl>(tab, "ShapesUsesList").GetVisualDescendants().OfType<CheckBox>().ToList();
                Assert.Equal(screen.Uses.Count, ticks.Count);
                Click(window, ticks[0]);
                Settle(window);
                Assert.Single(selection.SelectedChords);
                Assert.True(screen.Uses[0].IsChecked, "the ticked use is not ticked");
                var split = Named<Button>(tab, "ShapesSplitUses");
                Assert.True(split.IsEffectivelyEnabled, "Split uses is off with one of several uses ticked");
                var templates = state.ActiveArrangement!.ChordTemplates.Count;
                Click(window, split);
                Settle(window);
                Assert.Equal(templates + 1, state.ActiveArrangement!.ChordTemplates.Count);
            }
            finally
            {
                window?.Close();
                selection.ClearSelections();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.CurrentTimeMs = previous.CurrentTimeMs;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
            }
        }, CancellationToken.None);

    /// <summary>
    ///     A verb that waits for its yes scrolls the yes on screen, and the yes merges: in German the question above
    ///     the buttons wraps past the Checked card's fold in the inspector at 530x370, where a click hit the card and
    ///     nothing merged (#795). Runs in every shipped culture whatever the headless culture is; captures
    ///     <c>shapes-pending</c> in the headless culture.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    public Task AVerbWaitingForItsYes_ScrollsTheYesOnScreen_AndTheYesMerges(string culture) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var screen = Assert.IsType<ShapesPanelViewModel>(inspector.ChordShapes);
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex);
            var restore = LocalizationService.Instance.ConfiguredCulture;
            Window? window = null;
            try
            {
                LocalizationService.Instance.SetConfiguredCulture(culture);
                Assert.Equal(culture, LocalizationService.Instance.Culture.Name);
                Seed(state, templates: 60);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(530, 370, view);
                window.Show();
                Settle(window);
                var tab = view.GetVisualDescendants().OfType<ShapesPanelView>().Single();

                var copy = screen.AllRows.First(r => r.Issue.Kind == ChordShapeIssueKind.SameFrets);
                screen.CurrentRow = copy;
                ShowCard(tab, "ShapesShapeCard");
                Settle(window);
                Click(window, Named<Button>(tab, "ShapeFix"));
                Settle(window);
                AssertShown(tab, "ShapesCheckedCard", $"{culture}: a merge waiting for its yes");
                if (culture == HeadlessSessionFixture.SweptCulture) NarrowWidthLayout.Capture(window, "shapes-pending");
                AssertOnScreen(tab, "ShapesCheckedCard", tab.FindControl<Button>("ShapesConfirm")!, $"{culture}: the merge's yes");
                AssertOnScreen(tab, "ShapesCheckedCard", tab.FindControl<Button>("ShapesCancelPending")!, $"{culture}: the merge's cancel");
                Assert.False(tab.FindControl<ColumnFlowCard>("ShapesCheckedCard")!.HasMoreBelow, $"{culture}: the bar closes the card, yet more is below");
                AssertNoCaptionIsTrimmed(tab, $"{culture}: the Checked page with a merge waiting");
                Click(window, tab.FindControl<Button>("ShapesConfirm")!);
                Settle(window);
                Assert.DoesNotContain(copy, screen.AllRows);
            }
            finally
            {
                window?.Close();
                selection.ClearSelections();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The current shape offers Split off selection once the selection holds one of its chords, and Merge here (on
    ///     its line, with the hint over the list) once the merge of its duplicate is being chosen; nothing runs past a
    ///     card, in every shipped culture whatever the headless culture is (German's Merge here ran past its card,
    ///     #795). Captures <c>shapes-extras</c> in the headless culture.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public Task TheCurrentShape_WithSplitAndMergeHere_OffersBoth_AndNothingRunsPastACard(string culture) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var screen = Assert.IsType<ShapesPanelViewModel>(inspector.ChordShapes);
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex);
            var restore = LocalizationService.Instance.ConfiguredCulture;
            Window? window = null;
            try
            {
                LocalizationService.Instance.SetConfiguredCulture(culture);
                Assert.Equal(culture, LocalizationService.Instance.Culture.Name);
                var arrangement = Seed(state, templates: 60);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(530, 370, view);
                window.Show();
                Settle(window);
                var tab = view.GetVisualDescendants().OfType<ShapesPanelView>().Single();

                // Template 3 repeats template 2's frets: choosing a merge for 3 lets 2 take it.
                var target = screen.AllRows.Single(r => r.Template.Id == 2);
                var source = screen.AllRows.Single(r => r.Template.Id == 3);
                var chord = arrangement.Levels[0].Chords.First(c => c.TemplateId == 2);
                selection.SelectedChords.Add(chord);
                selection.SelectedChord = chord;
                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                screen.CurrentRow = target;
                ShowCard(tab, "ShapesShapeCard");
                Settle(window);
                Assert.True(Named<Button>(tab, "ShapeSplit").IsEffectivelyEnabled, "the current shape does not offer Split off selection");
                AssertNothingOverflows(tab, $"{culture}: a shape with a selection to split off");

                source.BeginMergeCommand.Execute(null);
                Settle(window);
                AssertShown(tab, "ShapesListCard", "choosing a merge target");
                Assert.True(target.CanMergeHere, "the shape does not offer Merge here");
                Assert.True(tab.FindControl<Grid>("ShapesMergeHint")!.IsEffectivelyVisible, "no merge hint over the list");
                var line = ShapesPanelCards.Card(tab, target);
                Assert.True(Named<Button>(line, "ShapeMergeHere").IsEffectivelyVisible, "the line does not offer Merge here");
                if (culture == HeadlessSessionFixture.SweptCulture) NarrowWidthLayout.Capture(window, "shapes-extras");
                AssertNothingOverflows(tab, $"{culture}: a list choosing a merge target");
            }
            finally
            {
                window?.Close();
                selection.ClearSelections();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }
        }, CancellationToken.None);

    /// <summary>
    ///     A name typed in the Shape page's box applies to the shape it was typed for, even when another line becomes
    ///     the current shape (a press on it) before the box loses focus; the other shape keeps its own name.
    /// </summary>
    [Fact]
    public Task ANameTyped_AppliesToItsShape_WhenAnotherLineIsPressedBeforeTheBoxLosesFocus() =>
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
                Seed(state, templates: 12);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(600, 524, view);
                window.Show();
                Settle(window);
                var tab = view.GetVisualDescendants().OfType<ShapesPanelView>().Single();
                var typedFor = screen.AllRows.Single(r => r.Template.Id == 1);
                var pressed = screen.AllRows.Single(r => r.Template.Id == 2);
                var pressedName = pressed.Template.Name;
                screen.CurrentRow = typedFor;
                ShowCard(tab, "ShapesShapeCard");
                Settle(window);

                var box = Named<TextBox>(tab, "ShapeName");
                Assert.True(box.Focus(), "the name box took no focus");
                Settle(window);
                box.Text = "Typed for its own shape";
                Settle(window);
                // What a press on another line does first (ShapesPanelView.MakeCurrent), while the box still has focus.
                screen.CurrentRow = pressed;
                Settle(window);
                Assert.Same(pressed, box.DataContext);
                tab.FindControl<TextBox>("ShapesSearch")!.Focus();
                Settle(window);

                Assert.Equal("Typed for its own shape", typedFor.Template.Name);
                Assert.Equal(pressedName, pressed.Template.Name);
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
    ///     Per fake variant and size, every page in turn: each card on screen measures non-zero, no text needs more
    ///     height than it has and nothing runs past its card; the Shape page's diagram is readable. Captures
    ///     <c>shapes-&lt;variant&gt;-&lt;size&gt;-&lt;page&gt;</c> with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptanceCases))]
    public Task EveryPage_LaysOut_AndNothingOverflows(string variant, double width, double height) =>
        HeadlessSessionFixture.Dispatch(() => SweepPages(variant, width, height, ""), CancellationToken.None);

    /// <summary>
    ///     The same sweep in German and Japanese at 480 and 530 px (the buttons' captions are the longest there),
    ///     over the seven- and eight-string patterns and the longest names. Captures
    ///     <c>shapes-&lt;variant&gt;-&lt;size&gt;-&lt;page&gt;-&lt;culture&gt;</c> with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(CultureCases))]
    public Task EveryPage_LaysOut_AndNothingOverflows_InEveryShippedCulture(string variant, string culture, double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var restore = LocalizationService.Instance.ConfiguredCulture;
            try
            {
                LocalizationService.Instance.SetConfiguredCulture(culture);
                Assert.Equal(culture, LocalizationService.Instance.Culture.Name);
                SweepPages(variant, width, 370, culture);
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }
        }, CancellationToken.None);

    private static void SweepPages(string variant, double width, double height, string culture)
    {
        var screen = FakeShapePanelScreen.Variants.Single(v => v.Name == variant).Create();
        var view = new ShapesPanelView { DataContext = screen };
        var window = NarrowWidthLayout.NewWindow(width, height, view);
        window.Show();
        try
        {
            Settle(window);
            var at = $"{variant} @ {width:0}x{height:0} {culture}";
            foreach (var page in Pages)
            {
                ShowCard(view, page);
                Settle(window);
                NarrowWidthLayout.Capture(window, $"shapes-{variant}-{width:0}x{height:0}-{page}{(culture.Length > 0 ? "-" + culture : "")}");
                AssertShown(view, page, at);
                foreach (var card in view.Cards.ShownCards)
                    Assert.True(card.Bounds is { Width: > 40, Height: > 40 }, $"{at}: {card.Name} laid out {card.Bounds}");
                AssertNothingOverflows(view, $"{at}, {page}");
                AssertNamesKeepTheirRoom(view, $"{at}, {page}");
                AssertNoCaptionIsTrimmed(view, $"{at}, {page}");
            }

            if (!screen.HasRows)
            {
                ShowCard(view, "ShapesListCard");
                Settle(window);
                Assert.True(view.FindControl<TextBlock>("ShapesEmpty")!.Bounds.Height > 1, $"{variant}: no empty text");
                return;
            }

            ShowCard(view, "ShapesShapeCard");
            Settle(window);
            AssertDiagramsReadable(view.FindControl<Control>("ShapesShapeCard")!, at);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>No button caption on screen is ellipsized: the German "Vorherigen Akkord wiederholen" wraps to a
    ///     second line at 480 px rather than losing its end.</summary>
    private static void AssertNoCaptionIsTrimmed(ShapesPanelView view, string what)
    {
        var trimmed = view.Cards.ShownCards.SelectMany(c => c.GetVisualDescendants().OfType<Button>())
            .Where(b => b.IsEffectivelyVisible)
            .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
            .Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 }
                        && (t.TextLayout.TextLines.Any(l => l.HasCollapsed) || t.TextLayout.Width > t.Bounds.Width + 0.5))
            .Select(t => $"'{t.Text}' ({t.TextLayout.Width:0.#} px) at {t.Bounds.Width:0.#} px")
            .ToList();
        Assert.True(trimmed.Count == 0, $"{what}: ellipsized or cut captions {string.Join("; ", trimmed)}");
    }

    // Laid out is not enough: a control under the card's fold is clipped away and a click there hits the card.
    private static void AssertOnScreen(ShapesPanelView view, string card, Control control, string what)
    {
        var scroller = view.FindControl<ColumnFlowCard>(card)!.Scroller!;
        var top = control.TranslatePoint(default, scroller)!.Value.Y;
        var bottom = top + control.Bounds.Height;
        Assert.True(top >= -0.5 && bottom <= scroller.Viewport.Height + 0.5,
            $"{what} lies at {top:0}-{bottom:0}, outside its card's {scroller.Viewport.Height:0} px viewport");
    }

    private static void ShowCard(ShapesPanelView view, string name) => view.Cards.ShowPage(view.FindControl<Control>(name)!);

    private static void AssertShown(ShapesPanelView view, string name, string what) =>
        Assert.True(view.Cards.ShownCards.Contains(view.FindControl<Control>(name)!), $"{what}: {name} is not on screen");

    /// <summary>Every card on screen measures non-zero; the realized lines in the list are laid out.</summary>
    private static void AssertCardsLaidOut(ShapesPanelView view, string at)
    {
        AssertShown(view, "ShapesListCard", at);
        foreach (var card in view.Cards.ShownCards)
            Assert.True(card.Bounds is { Width: > 40, Height: > 40 }, $"{at}: {card.Name} laid out {card.Bounds}");
        var lines = Cards(view).ToList();
        Assert.NotEmpty(lines);
        foreach (var line in lines)
            Assert.True(line.Bounds is { Width: > 40, Height: > 1 }, $"{at}: a line laid out {line.Bounds}");
        Assert.True(Named<TextBlock>(view, "ShapesSummary").Bounds.Height > 1, $"{at}: no summary");
        AssertNamesKeepTheirRoom(view, at);
    }

    /// <summary>
    ///     The lines scroll and the list card's head does not; at least <paramref name="minLines" /> whole lines are in
    ///     view; the list's scrollbar is drawn (not auto-hidden) while there is more.
    /// </summary>
    private static void AssertListIsTheOnlyScrollRegion(ShapesPanelView view, int minLines)
    {
        var scroll = view.Scroll;
        var header = view.FindControl<TextBlock>("ShapesHeader")!;
        var search = view.FindControl<TextBox>("ShapesSearch")!;
        Assert.False(scroll.AllowAutoHide, "the list's scrollbar auto-hides: nothing says the list goes on");
        Assert.DoesNotContain(scroll, header.GetVisualAncestors());
        Assert.DoesNotContain(scroll, search.GetVisualAncestors());

        var viewportTop = scroll.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        var viewportBottom = viewportTop + scroll.Viewport.Height;
        var whole = Cards(view).Count(r =>
        {
            var top = r.TranslatePoint(new Point(0, 0), view)!.Value.Y;
            return top >= viewportTop - 0.5 && top + r.Bounds.Height <= viewportBottom + 0.5;
        });
        NarrowWidthLayout.Report($"shapes {view.Bounds.Width:0}x{view.Bounds.Height:0}: viewport {scroll.Viewport.Height:0} px, {whole} whole lines");
        Assert.True(whole >= minLines, $"{whole} whole lines in view at {view.Bounds.Width:0} x {view.Bounds.Height:0}; the acceptance is {minLines}");
        if (scroll.Extent.Height <= scroll.Viewport.Height + 1) return;
        var searchTop = search.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        scroll.Offset = new Vector(0, 20);
        view.UpdateLayout();
        Assert.Equal(searchTop, search.TranslatePoint(new Point(0, 0), view)!.Value.Y, 1);
        scroll.Offset = default;
        view.UpdateLayout();
    }

    /// <summary>
    ///     No visible text on a card on screen needs more height than it has (a clipped line), and no text, button,
    ///     box or tick runs past the card that holds it (a card's own scrollbar parts are the theme's, not content). A
    ///     list line may trim its name: the Shape page shows it whole.
    /// </summary>
    private static void AssertNothingOverflows(ShapesPanelView view, string what)
    {
        var problems = new List<string>();
        foreach (var card in view.Cards.ShownCards)
        {
            foreach (var text in card.GetVisualDescendants().OfType<TextBlock>()
                         .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Bounds.Width > 1))
            {
                if (text.TextLayout.Height > text.Bounds.Height - text.Padding.Top - text.Padding.Bottom + 1)
                    problems.Add($"'{text.Text}' needs {text.TextLayout.Height:0.#} px of height in {text.Bounds.Height:0.#}");
            }

            foreach (var part in card.GetVisualDescendants().OfType<Control>()
                         .Where(c => c is TextBlock or Button or CheckBox or TextBox && c.IsEffectivelyVisible && c.Bounds.Width > 0)
                         .Where(c => !c.GetVisualAncestors().OfType<ScrollBar>().Any()))
            {
                var right = part.TranslatePoint(new Point(part.Bounds.Width, 0), card)?.X ?? 0;
                if (right > card.Bounds.Width + 0.5)
                    problems.Add($"{part.GetType().Name} {part.Name} '{(part as TextBlock)?.Text}' ends at {right:0.#} in a {card.Bounds.Width:0.#} px card");
            }
        }

        Assert.True(problems.Count == 0, $"{what}: overflowing text: {string.Join("; ", problems.Take(8))}");
    }

    /// <summary>
    ///     A list line's fret pattern never squeezes its name to nothing: every realized line with a name gives it at
    ///     least <see cref="MinNameWidth" /> px (a seven- or eight-string pattern trims first).
    /// </summary>
    private static void AssertNamesKeepTheirRoom(ShapesPanelView view, string what)
    {
        var squeezed = Cards(view).Where(c => c.IsEffectivelyVisible)
            .Select(c => Named<TextBlock>(c, "ShapeTitle"))
            .Where(t => !string.IsNullOrEmpty(t.Text) && t.Bounds.Width < MinNameWidth)
            .Select(t => $"'{t.Text}' gets {t.Bounds.Width:0.#} px").ToList();
        Assert.True(squeezed.Count == 0, $"{what}: names squeezed by the fret pattern: {string.Join("; ", squeezed.Take(6))}");
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

    private static IEnumerable<Border> Cards(Control view) => ShapesPanelCards.Realized(view);

    private static T Named<T>(Visual root, string name) where T : Control => NarrowWidthLayout.Named<T>(root, name);

    private static void Click(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    // Seven or eight strings: every shape also frets the extra strings at frets 10-18, the widest patterns.
    private static Arrangement Seed(ProjectState state, int templates, int strings = 6)
    {
        var project = new ChartProject { Chart = { SongName = "Shapes panel fixture" } };
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead", StringCount = strings };
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
            for (var s = 6; s < strings; s++) template.Frets[s] = 10 + shift;
            if (i % 5 != 0) ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
            if (i % 7 == 0) template.Arpeggio = true;
            arrangement.ChordTemplates.Add(template);

            if (i % 6 == 5) continue;
            var chord = new Chord { Position = new FractionalPosition(i % 60), TemplateId = i };
            foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            level.Chords.Add(chord);
        }

        // Template 2 is used three times: the Uses page has something to tick and split.
        foreach (var extra in new[] { 62, 63 })
        {
            var chord = new Chord { Position = new FractionalPosition(extra), TemplateId = 2 };
            foreach (var (s, f) in arrangement.ChordTemplates[2].Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
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
