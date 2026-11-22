using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Parsing.Eof;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Inspector design D: the Issues tab is four cards on one <see cref="ColumnFlowPanel" /> (Findings, Fixes,
///     Whole project and Review, the Editor on Fire round trip), as many side by side as the pane is wide, the rest
///     behind the card pager that replaced #511's side rail.
///     <para>
///         The real <see cref="InspectorView" /> over the app's own view models, with REAL content (the sweep's
///         arrangements, fourteen findings, a round trip with four groups), at 480x370 and 530x370 (paged) and
///         1100x450 (every card): per section its card is on screen with a size, nothing on screen is arranged
///         outside the pane, smaller than it asked or over a sibling, and the findings list scrolls. Width-shaped,
///         so this class is in the release's de-DE / ja-JP layout filter, and it lays the paged sizes out in German
///         and Japanese itself. The doors into the tab are <see cref="EveryDoor_OpensItsSection" />.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class IssuesTabCardsUiPerfSmokeTests
{
    [Theory]
    [InlineData(480, 370, IssuesSection.Findings)]
    [InlineData(480, 370, IssuesSection.Project)]
    [InlineData(480, 370, IssuesSection.EditorOnFire)]
    [InlineData(530, 370, IssuesSection.Findings)]
    [InlineData(530, 370, IssuesSection.Project)]
    [InlineData(530, 370, IssuesSection.EditorOnFire)]
    [InlineData(1100, 450, IssuesSection.Findings)]
    [InlineData(1100, 450, IssuesSection.Project)]
    [InlineData(1100, 450, IssuesSection.EditorOnFire)]
    public async Task EverySection_FitsThePane_WithRealContent(int width, int height, IssuesSection section) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, height, (window, vm, issues) =>
        {
            vm.IssuesSections.Open(section);
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, $"issues-{section}-{width}x{height}");
            AssertSectionFits(issues, section, $"{section} @{width}x{height}");

            if (section != IssuesSection.Findings) return;
            var rows = Named<ScrollViewer>(issues, "IssuesRowsScroll");
            Assert.True(rows.Extent.Height > rows.Viewport.Height + 1,
                $"@{width}x{height}: extent {rows.Extent.Height:0.#} fits the viewport {rows.Viewport.Height:0.#}; the fixture is too small to prove the scroll");
            rows.Offset = new Vector(0, rows.Extent.Height);
            NarrowWidthLayout.Settle(window);
            Assert.True(rows.Offset.Y > 0, "the findings list does not scroll");
            Assert.True(Math.Abs(rows.Offset.Y + rows.Viewport.Height - rows.Extent.Height) < 1, "the findings list cannot scroll to its end");
        }), CancellationToken.None);

    /// <summary>German and Japanese words are where a card clips: every section at both paged sizes, in both.</summary>
    [Theory]
    [InlineData("de-DE", 480)]
    [InlineData("de-DE", 530)]
    [InlineData("ja-JP", 480)]
    [InlineData("ja-JP", 530)]
    public async Task EverySection_FitsThePane_InGermanAndJapanese(string culture, int width) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var localization = LocalizationService.Instance;
            var restore = localization.ConfiguredCulture;
            try
            {
                localization.SetConfiguredCulture(culture);
                WithPane(width, 370, (window, vm, issues) =>
                {
                    foreach (var section in Enum.GetValues<IssuesSection>())
                    {
                        vm.IssuesSections.Open(section);
                        NarrowWidthLayout.Settle(window);
                        NarrowWidthLayout.Capture(window, $"issues-{section}-{width}x370-{culture}");
                        AssertSectionFits(issues, section, $"{section} @{width}x370 {culture}");
                    }
                });
            }
            finally
            {
                localization.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = localization.Culture;
            }
        }, CancellationToken.None);

    /// <summary>The review captures: every section with the real content at a pane a charter would actually have (only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>).</summary>
    [Fact]
    public async Task EverySection_IsCaptured_AtARoomierPane() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 600, (window, vm, _) =>
        {
            foreach (var section in Enum.GetValues<IssuesSection>())
            {
                vm.IssuesSections.Open(section);
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"issues-review-{section}");
            }
        }), CancellationToken.None);

    [Fact]
    public async Task TheSongWideRow_IsLaidOutAndOpensTheFindings() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 900, (window, vm, issues) =>
        {
            vm.IssuesSections.Open(IssuesSection.Project);
            NarrowWidthLayout.Settle(window);
            var row = Named<ItemsControl>(issues, "ArrangementDifficultyList").GetVisualDescendants().OfType<Button>()
                .Single(b => b.DataContext is ProjectArrangementRow { IsSongWide: true });
            Assert.True(row.IsEffectivelyVisible && row.Bounds is { Width: > 1, Height: > 1 }, $"the song-wide row laid out as {row.Bounds}");
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(row) as string), "the song-wide row has no tooltip");

            row.Command!.Execute(row.CommandParameter);
            NarrowWidthLayout.Settle(window);
            Assert.Equal(IssuesSection.Findings, vm.IssuesSections.Section);
            Assert.Contains(issues.CardFor(IssuesSection.Findings), Cards(issues).ShownCards);
        }), CancellationToken.None);

    [Fact]
    public async Task ListSpacing_RowsAndGroupsSitFurtherApartThanTheLinesInsideThem() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 900, (window, vm, issues) =>
        {
            vm.IssuesSections.Open(IssuesSection.Project);
            NarrowWidthLayout.Settle(window);
            var rows = Named<ItemsControl>(issues, "ArrangementDifficultyList").GetVisualDescendants().OfType<Button>().ToList();
            Assert.True(rows.Count >= 2, "the seed has four arrangements");
            var rowTexts = rows.Select(r => r.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0)
                .OrderBy(t => Top(window, t)).ToList()).ToList();
            var lineGap = Top(window, rowTexts[0][^1]) - Bottom(window, rowTexts[0][0]);
            var rowGap = Top(window, rowTexts[1][0]) - Bottom(window, rowTexts[0][^1]);
            Console.WriteLine($"SPACING arrangement line gap={lineGap} row gap={rowGap}");
            Assert.True(rowGap > lineGap, $"arrangement rows ({rowGap}px apart) must sit further apart than a row's two lines ({lineGap}px)");

            vm.IssuesSections.Open(IssuesSection.EditorOnFire);
            NarrowWidthLayout.Settle(window);
            var texts = Named<ItemsControl>(issues, "EofGroupList").GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0).OrderBy(t => Top(window, t)).ToList();
            var titles = texts.Where(t => t.Classes.Contains("EofGroupTitle")).ToList();
            var subjects = texts.Where(t => t.Inlines?.Count > 0).ToList();
            var details = texts.Where(t => t.Classes.Contains("ColumnFlowBody") && !t.Classes.Contains("EofGroupTitle")).ToList();
            Assert.True(subjects.Count >= 2 && details.Count >= 2 && titles.Count >= 2, "the seed has four groups of several places");
            var placeInner = Top(window, details[0]) - Bottom(window, subjects[0]);
            var placeGap = Top(window, subjects[1]) - Bottom(window, details[0]);
            var lastDetail = details.Last(d => Top(window, d) < Top(window, titles[1]));
            var groupGap = Top(window, titles[1]) - Bottom(window, lastDetail);
            Console.WriteLine($"SPACING place inner={placeInner} between places={placeGap} before group title={groupGap}");
            Assert.True(placeGap > placeInner, $"places ({placeGap}px apart) must sit further apart than the lines of one place ({placeInner}px)");
            Assert.True(groupGap > placeGap, $"a group title ({groupGap}px after the last place) must sit further off than places sit from each other ({placeGap}px)");
        }), CancellationToken.None);

    /// <summary>
    ///     The misfit rule itself: a busy bar inside the pane (its indicators slide outside the bar, which clips
    ///     them), and a part pushed past a clipping box, pass; the same part with no clip in between, and a box that
    ///     itself sticks out, fail.
    /// </summary>
    [Fact]
    public async Task MisfitRule_PassesClippedParts_AndStillFailsARealOverflow() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var busy = new ProgressBar { IsIndeterminate = true, Height = 4 };
            var clippedPart = new Border { Width = 120, Height = 10, Margin = new Thickness(-80, 0, 0, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            var clip = new Border { Width = 100, Height = 10, ClipToBounds = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Child = clippedPart };
            var stack = new StackPanel { Children = { busy, clip } };
            var pane = new Border { Width = 300, Height = 200, Child = stack };
            var window = NarrowWidthLayout.NewWindow(300, 200, pane);
            try
            {
                window.Show();
                NarrowWidthLayout.Settle(window);
                Assert.True(busy.Bounds.Width > 1 && clippedPart.TranslatePoint(default, pane)!.Value.X < -1, "the fixture must put a part outside the pane");
                var passing = Misfits(pane);
                Assert.True(passing.Count == 0, "clipped parts were judged: " + string.Join("; ", passing));

                clip.ClipToBounds = false;
                NarrowWidthLayout.Settle(window);
                Assert.Contains(Misfits(pane), p => p.StartsWith("Border spans x", StringComparison.Ordinal));

                clip.ClipToBounds = true;
                clip.Margin = new Thickness(250, 0, 0, 0);
                NarrowWidthLayout.Settle(window);
                Assert.Contains(Misfits(pane), p => p.StartsWith("Border spans x 250", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private static double Top(Visual root, Control c) => c.TranslatePoint(default, root)!.Value.Y;

    private static double Bottom(Visual root, Control c) => Top(root, c) + c.Bounds.Height;

    /// <summary>
    ///     The pager stands where the rail stood: one named button per paged card, the shown one lit, a click shows
    ///     that card AND opens its section; the captions keep what the rail's badges said (the findings count, a dot
    ///     on a card that needs a look).
    /// </summary>
    [Fact]
    public async Task ThePager_NamesEveryPage_AndAClickOpensItsSection() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(480, 370, (window, vm, issues) =>
        {
            var cards = Cards(issues);
            var buttons = cards.Pager.Buttons;
            Assert.True(buttons.Count >= 2, $"a 480 px pane shows {buttons.Count} page buttons — every card fits, so this case proves nothing");
            Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b)), "a page button has no accessible name"));
            Assert.Contains(buttons, b => AutomationProperties.GetName(b)!.EndsWith(" 14", StringComparison.Ordinal));
            Assert.True(vm.IssuesSections.ProjectNeedsAttention && vm.IssuesSections.EditorOnFireDrops);
            Assert.Equal(2, buttons.Count(b => AutomationProperties.GetName(b)!.EndsWith(" •", StringComparison.Ordinal)));

            foreach (var section in new[] { IssuesSection.Project, IssuesSection.EditorOnFire, IssuesSection.Findings })
            {
                var card = issues.CardFor(section);
                var button = cards.Pager.Buttons.FirstOrDefault(b => AutomationProperties.GetName(b) == ColumnFlowPanel.GetTitle(card));
                if (button is null)
                {
                    Assert.Contains(card, cards.ShownCards); // a card that is never paged away needs no button
                    continue;
                }

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                NarrowWidthLayout.Settle(window);
                Assert.Equal(section, vm.IssuesSections.Section);
                Assert.Contains(card, cards.ShownCards);
                Assert.True(card.Bounds is { Width: > 1, Height: > 1 }, $"{card.Name} laid out as {card.Bounds}");
                Assert.True(cards.Pager.Buttons.Single(b => b.IsChecked == true) == button, "the clicked page is not the lit one");
            }
        }), CancellationToken.None);

    /// <summary>
    ///     A click on a finding (D's list) jumps to it, brings the Fixes card on screen with that finding, and marks
    ///     its row with the accent spine; a row of a rule the fix menu repairs carries its own Fix.
    /// </summary>
    [Fact]
    public async Task AFindingClick_ShowsItOnTheFixesCard_AndMarksItsRow() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(480, 370, (window, vm, issues) =>
        {
            var row = issues.GetVisualDescendants().OfType<Button>().First(b => b.Name == "IssueRow" && b.IsEffectivelyVisible);
            var finding = Assert.IsType<ArrangementIssue>(row.CommandParameter);
            Assert.DoesNotContain(issues.GetVisualDescendants().OfType<Border>(), b => b.Name == "IssueSpine" && b.IsVisible);

            row.Command!.Execute(finding);
            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            NarrowWidthLayout.Settle(window);

            Assert.Same(finding, vm.Issues.IssueAi.Issue);
            Assert.Contains(Named<Control>(issues, "IssuesFixesCard"), Cards(issues).ShownCards);
            var message = Named<TextBlock>(issues, "IssueDetailMessage");
            Assert.True(message.IsEffectivelyVisible && message.Bounds is { Width: > 1, Height: > 1 }, $"the Fixes card's finding laid out as {message.Bounds}");
            var spines = issues.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "IssueSpine" && b.IsVisible).ToList();
            Assert.Single(spines);
            Assert.Same(finding, Assert.IsType<IssueListRow>(spines[0].DataContext).Issue);
            var fixes = issues.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "IssueRowFix").ToList();
            Assert.All(fixes, fix => Assert.Equal(Assert.IsType<IssueListRow>(fix.DataContext).HasFix, fix.IsVisible));

            // the seeded tone change on a note has a fix: scroll its row on and see its Fix, so the column is not vacuous
            var list = Named<ItemsControl>(issues, "IssueGroupList");
            var withFix = vm.Issues.Rows.Items.ToList().FindIndex(r => r.HasFix);
            Assert.True(withFix >= 0, "the seed has no finding the fix menu can fix");
            list.ScrollIntoView(withFix);
            NarrowWidthLayout.Settle(window);
            Assert.Contains(issues.GetVisualDescendants().OfType<Button>(), b => b.Name == "IssueRowFix" && b.IsVisible
                && ReferenceEquals(b.DataContext, vm.Issues.Rows.Items[withFix]));
        }), CancellationToken.None);

    /// <summary>
    ///     A Tiny pane (one card per page) shows the card the section names: Findings is the list, with more than one row,
    ///     when the tab first opens and when the section is opened again from elsewhere. A click on a finding then pages
    ///     the Fixes card on (D's flow), and the Findings page button brings the list back.
    /// </summary>
    [Fact]
    public async Task ATinyPane_OnFindings_ShowsTheList_AndAFindingClickPagesTheFixes() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(420, 370, (window, vm, issues) =>
        {
            var cards = Cards(issues);
            var list = Named<Control>(issues, "IssuesFindingsCard");
            Assert.Equal(ColumnFlowTier.Tiny, cards.Tier);
            Assert.Equal(IssuesSection.Findings, vm.IssuesSections.Section);
            AssertListIsThePage("the tab opened");

            vm.IssuesSections.Open(IssuesSection.Project);
            NarrowWidthLayout.Settle(window);
            Assert.Equal([Named<Control>(issues, "IssuesProjectCard")], cards.ShownCards);
            vm.IssuesSections.Open(IssuesSection.Findings);
            NarrowWidthLayout.Settle(window);
            AssertListIsThePage("Findings opened again");

            var row = ShownRows().First();
            row.Command!.Execute(row.CommandParameter);
            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            NarrowWidthLayout.Settle(window);
            Assert.Equal([Named<Control>(issues, "IssuesFixesCard")], cards.ShownCards);
            Assert.Equal(IssuesSection.Findings, vm.IssuesSections.Section);

            cards.Pager.Buttons.Single(b => AutomationProperties.GetName(b) == ColumnFlowPanel.GetTitle(list)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            NarrowWidthLayout.Settle(window);
            AssertListIsThePage("the Findings page picked");

            List<Button> ShownRows() => [.. issues.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Name == "IssueRow" && b.IsEffectivelyVisible && b.Bounds.Height > 1 && OnScreen(cards, b))];

            void AssertListIsThePage(string when)
            {
                Assert.True(cards.ShownCards.SequenceEqual([list]), $"{when}: the page shows {string.Join(", ", cards.ShownCards.Select(c => c.Name))}, not the findings list");
                Assert.True(ShownRows().Count > 1, $"{when}: the findings list shows {ShownRows().Count} row(s)");
            }
        }), CancellationToken.None);

    /// <summary>
    ///     A song can hold hundreds of findings: the list VIRTUALIZES (300 findings realize a viewport's worth of
    ///     rows, not 300), still shows more than one row at the Small pane, scrolls to its last finding, and Tab
    ///     walks from one row on to the next.
    /// </summary>
    [Fact]
    public async Task ThreeHundredFindings_RealizeOnlyAViewport_AndTabWalksTheRows() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 370, (window, vm, issues) =>
        {
            var many = Enumerable.Range(0, 300).Select(i =>
                new ArrangementIssue(ArrangementIssueType.LinkNextMissingTargetNote, $"The linked note at bar {i + 3} has no target on the next string.", 4000 + i * 500)).ToList();
            vm.Issues.IssueGroups.Clear();
            vm.Issues.IssueGroups.Add(new InspectorIssueGroup(ArrangementIssueSeverity.Warning, many));
            vm.Issues.IssueCount = many.Count;
            vm.IssuesSections.Open(IssuesSection.Findings);
            NarrowWidthLayout.Settle(window);
            Assert.Equal(300, vm.Issues.Rows.Items.Count);

            var list = Named<ItemsControl>(issues, "IssueGroupList");
            var realized = list.GetRealizedContainers().Count();
            Assert.True(realized is > 1 and < 40, $"300 findings realized {realized} row containers; the list does not virtualize");
            var shown = issues.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Name == "IssueRow" && b.IsEffectivelyVisible && b.Bounds.Height > 1).ToList();
            Assert.True(shown.Count > 1, $"the list shows {shown.Count} row(s)");

            var rows = Named<ScrollViewer>(issues, "IssuesRowsScroll");
            Assert.True(rows.Viewport.Height > 1 && rows.Extent.Height > rows.Viewport.Height * 4,
                $"extent {rows.Extent.Height:0.#} over viewport {rows.Viewport.Height:0.#}: the list did not take a bounded height");
            list.ScrollIntoView(299);
            NarrowWidthLayout.Settle(window);
            Assert.Contains(issues.GetVisualDescendants().OfType<Button>(), b => b.Name == "IssueRow" && b.IsEffectivelyVisible
                && ReferenceEquals(b.DataContext, vm.Issues.Rows.Items[299]));
            Assert.True(list.GetRealizedContainers().Count() < 40, "scrolling to the end realized the whole list");

            list.ScrollIntoView(0);
            NarrowWidthLayout.Settle(window);
            var first = issues.GetVisualDescendants().OfType<Button>()
                .First(b => b.Name == "IssueRow" && ReferenceEquals(b.DataContext, vm.Issues.Rows.Items[0]));
            Assert.True(first.Focus(NavigationMethod.Tab), "the first finding row would not take keyboard focus");
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            NarrowWidthLayout.Settle(window);
            var next = window.FocusManager?.GetFocusedElement() as Control;
            Assert.True(next is { DataContext: IssueListRow row } && !ReferenceEquals(row, vm.Issues.Rows.Items[0]),
                $"Tab from the first row went to {next?.GetType().Name} '{next?.Name}', not the next finding");
        }), CancellationToken.None);

    /// <summary>A finding row reached by Tab shows the focus ring the row theme draws (the old spine's focus light).</summary>
    [Fact]
    public async Task AFindingRow_ReachedByKeyboard_ShowsItsFocusRing() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 370, (window, _, issues) =>
        {
            var row = issues.GetVisualDescendants().OfType<Button>().First(b => b.Name == "IssueRow" && b.IsEffectivelyVisible);
            var chrome = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Chrome");
            var idle = chrome.BorderBrush?.ToString();

            Assert.True(row.Focus(NavigationMethod.Tab), "the finding row would not take keyboard focus");
            NarrowWidthLayout.Settle(window);

            Assert.True(row.IsFocused);
            Assert.NotEqual(idle, chrome.BorderBrush?.ToString());
        }), CancellationToken.None);

    /// <summary>
    ///     Design D's Auto-fix asks first: the question and its Confirm / Cancel lay out under the buttons (en, de, ja at both
    ///     paged sizes), Cancel withdraws it. The Difficulty choice lays out once the arrangement has levels.
    /// </summary>
    [Theory]
    [InlineData("en-US", 480)]
    [InlineData("en-US", 530)]
    [InlineData("de-DE", 480)]
    [InlineData("de-DE", 530)]
    [InlineData("ja-JP", 480)]
    [InlineData("ja-JP", 530)]
    public async Task AutoFix_AsksFirst_AndTheDifficultyChoiceLaysOut(string culture, int width) =>
        await HeadlessSessionFixture.Dispatch(() => InCulture(culture, () => WithPane(width, 370, (window, vm, issues) =>
        {
            var arrangement = vm.ProjectState.ActiveArrangement!;
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = arrangement.Levels.Count });
            vm.Issues.Rows.Rebuild(vm.Issues.IssueGroups, arrangement, vm.ProjectState.Beats);
            vm.IssuesSections.Open(IssuesSection.Findings);
            NarrowWidthLayout.Settle(window);
            Named<Button>(issues, "IssuesAutoFix").Command!.Execute(null); // what a click runs (a raised Click event runs no command)
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, $"issues-autofix-ask-{width}-{culture}");

            var ask = Named<Control>(issues, "AutoFixConfirmRow");
            Assert.True(ask.IsEffectivelyVisible && ask.Bounds is { Width: > 1, Height: > 1 }, $"the Auto-fix question laid out as {ask.Bounds}");
            Assert.False(string.IsNullOrWhiteSpace(Named<TextBlock>(issues, "ConfirmSentence").Text));
            var filter = Named<ComboBox>(issues, "IssueLevelFilter");
            Assert.True(filter.IsEffectivelyVisible && filter.Bounds is { Width: > 1, Height: > 1 }, $"the Difficulty choice laid out as {filter.Bounds}");
            AssertSectionFits(issues, IssuesSection.Findings, $"auto-fix ask @{width} {culture}");

            Named<Button>(issues, "ConfirmNo").Command!.Execute(null); // what a click runs (a raised Click event runs no command)
            NarrowWidthLayout.Settle(window);
            Assert.False(ask.IsVisible);
        })), CancellationToken.None);

    /// <summary>
    ///     The Whole project card's check titles read as headings, not as the mono value face; its findings list with the
    ///     Arrangement choice and Jump / Fix lays out; the Fixes card's offline Explain opens with the remedy line.
    /// </summary>
    [Fact]
    public async Task TheProjectCard_ListsFindings_AndTheFixesCardExplainsOffline() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 900, (window, vm, issues) =>
        {
            var stampFont = issues.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("ColumnFlowStamp")).FontFamily;
            vm.IssuesSections.Open(IssuesSection.Project);
            NarrowWidthLayout.Settle(window);
            foreach (var name in new[] { "AudioCheckTitle", "ExportCheckTitle" })
                Assert.NotEqual(stampFont, Named<TextBlock>(issues, name).FontFamily);

            var choice = Named<ComboBox>(issues, "ProjectArrangementFilter");
            Assert.True(choice.Bounds is { Width: > 1, Height: > 1 }, $"the Arrangement choice laid out as {choice.Bounds}");
            Assert.Equal(vm.ProjectState.ActiveProject!.Arrangements.Count + 1, vm.ExportReadiness.ProjectFindings.ArrangementChoices.Count);
            foreach (var name in new[] { "ProjectFindingJump", "ProjectFindingFix" })
                Assert.True(Named<Button>(issues, name).Bounds is { Width: > 1, Height: > 1 }, $"{name} has no size");

            vm.IssuesSections.Open(IssuesSection.Findings);
            NarrowWidthLayout.Settle(window);
            var row = issues.GetVisualDescendants().OfType<Button>().First(b => b.Name == "IssueRow" && b.IsEffectivelyVisible);
            row.Command!.Execute(row.CommandParameter);
            NarrowWidthLayout.Settle(window);
            var status = Named<TextBlock>(issues, "IssueDetailStatus");
            Assert.True(status.IsEffectivelyVisible && !string.IsNullOrEmpty(status.Text), "the Fixes card has no status line");
            var about = Named<Button>(issues, "IssueRuleInfoButton");
            var flyout = Assert.IsAssignableFrom<Flyout>(about.Flyout);
            flyout.ShowAt(about);
            NarrowWidthLayout.Settle(window);
            var info = Assert.IsAssignableFrom<Control>(flyout.Content);
            Assert.True(info.Bounds is { Width: > 1, Height: > 1 }, $"the offline Explain laid out as {info.Bounds}");
            Assert.False(string.IsNullOrEmpty(vm.Issues.IssueSuggestion));
            flyout.Hide();
        }), CancellationToken.None);

    private static void InCulture(string culture, Action body)
    {
        var localization = LocalizationService.Instance;
        var restore = localization.ConfiguredCulture;
        try
        {
            localization.SetConfiguredCulture(culture);
            body();
        }
        finally
        {
            localization.SetConfiguredCulture(restore);
            CultureInfo.CurrentUICulture = localization.Culture;
        }
    }

    /// <summary>
    ///     A pane wide enough to show the Review card beside the findings computes the Editor on Fire round trip
    ///     without its section being opened; a paged pane that hides it does not.
    /// </summary>
    [Theory]
    [InlineData(1100, 450, true)]
    [InlineData(480, 370, false)]
    public async Task TheReviewCardOnScreen_ComputesTheRoundTrip(int width, int height, bool shown) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, height, (window, vm, issues) =>
        {
            vm.IssuesSections.Open(IssuesSection.Findings);
            NarrowWidthLayout.Settle(window);
            Assert.Equal(shown, Cards(issues).ShownCards.Contains(issues.CardFor(IssuesSection.EditorOnFire)));
            Assert.Equal(shown, vm.ExportReadiness.EofRoundTrip.IsExpanded);
            Assert.Equal(IssuesSection.Findings, vm.IssuesSections.Section);
        }), CancellationToken.None);

    [Fact]
    public async Task EveryDoor_OpensItsSection() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var shell = services.GetRequiredService<MainWindowViewModel>();
            var state = services.GetRequiredService<ProjectState>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;
            var previousTab = shell.Inspector.SelectedTabIndex;
            try
            {
                NarrowWidthLayout.SeedSong(state);
                var sections = shell.Inspector.IssuesSections;
                var readiness = shell.Inspector.ExportReadiness;
                // each door lands on its section from wherever the tab was left
                var doors = new (string Name, Action Open, IssuesSection Expected)[]
                {
                    ("status rail counters / Jump to issue", () => shell.StatusBar.OpenChartIssues!(), IssuesSection.Findings),
                    ("Tools > Chart quality report (and the palette entry)", () => shell.ShellDialogs.OpenQualityReportCommand.Execute(null), IssuesSection.Project),
                    ("the Rocksmith export's Show in Issues", () => shell.ShellDialogs.ShowChartIssues!(), IssuesSection.Project),
                    ("the Editor on Fire export's Show", () => shell.Export.ShowChartReport!(), IssuesSection.EditorOnFire),
                    ("the Sound tab's selection findings", () => shell.Inspector.ShowSelectionIssuesCommand.Execute(null), IssuesSection.Findings),
                    ("the Sound tab's export verdict", () => shell.Inspector.ShowExportReadinessCommand.Execute(null), IssuesSection.Project),
                    ("a Whole project row", () => readiness.SelectProjectArrangementCommand.Execute(new ProjectArrangementRow(1, "Rhythm", "", "", false, false)), IssuesSection.Findings)
                };
                foreach (var (name, open, expected) in doors)
                {
                    foreach (var from in Enum.GetValues<IssuesSection>().Where(s => s != expected))
                    {
                        sections.Open(from);
                        open();
                        Assert.True(expected == sections.Section, $"{name}: from {from} it opened {sections.Section}, not {expected}");
                        if (name.StartsWith("a Whole", StringComparison.Ordinal)) continue; // opens no tab itself
                        Assert.Equal(InspectorViewModel.IssuesTabIndex, shell.Inspector.SelectedTabIndex);
                    }
                }

                // with no Review card on screen, the round trip runs while its section is open, and only then
                sections.Open(IssuesSection.EditorOnFire);
                Assert.True(readiness.EofRoundTrip.IsExpanded);
                sections.Open(IssuesSection.Project);
                Assert.False(readiness.EofRoundTrip.IsExpanded);
            }
            finally
            {
                shell.Inspector.IssuesSections.Open(IssuesSection.Findings);
                shell.Inspector.SelectedTabIndex = previousTab;
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
            }
        }, CancellationToken.None);

    private static ColumnFlowPanel Cards(IssuesInspectorView issues) => Named<ColumnFlowPanel>(issues, "IssuesCards");

    private static T Named<T>(Visual root, string name) where T : Control => NarrowWidthLayout.Named<T>(root, name);

    /// <summary>
    ///     The opened section's card is on screen with a size, the tab fits the pane (<see cref="Misfits" />) and the
    ///     card shows the content the fixture put there, so a layout pass over an empty card cannot be green.
    /// </summary>
    private static void AssertSectionFits(IssuesInspectorView issues, IssuesSection section, string where)
    {
        Assert.True(issues.Bounds is { Width: > 1, Height: > 1 }, $"the Issues tab laid out as {issues.Bounds}");
        var cards = Cards(issues);
        var card = issues.CardFor(section);
        Assert.Contains(card, cards.ShownCards);
        Assert.True(card.Bounds is { Width: > 1, Height: > 1 }, $"{where}: {card.Name} laid out as {card.Bounds}");
        Assert.True(card.TranslatePoint(default, issues) is { X: >= -0.5 } origin && origin.X + card.Bounds.Width <= issues.Bounds.Width + 0.5,
            $"{where}: {card.Name} is not on screen");
        var problems = Misfits(issues);
        Assert.True(problems.Count == 0, $"{where}: " + string.Join("; ", problems));

        var visible = issues.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && OnScreen(cards, c)).ToList();
        switch (section)
        {
            case IssuesSection.Findings:
                Assert.True(visible.OfType<Button>().Count(b => b.Name == "IssueRow") >= 3, $"{where}: the findings card shows fewer than three rows");
                break;
            case IssuesSection.Project:
                Assert.True(Named<ItemsControl>(issues, "ArrangementDifficultyList").GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible) >= 3,
                    $"{where}: Whole project lists fewer than three arrangements");
                break;
            default:
                Assert.True(Named<ItemsControl>(issues, "EofGroupList").GetVisualDescendants().OfType<ItemsControl>().Count(i => i.IsEffectivelyVisible) >= 3,
                    $"{where}: Review lists fewer than three groups");
                break;
        }
    }

    /// <summary>A control is on screen unless it sits in a card the pager has put away (arranged off stage, still measured).</summary>
    private static bool OnScreen(ColumnFlowPanel cards, Control control)
    {
        var shown = cards.ShownCards;
        var card = control.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Parent == cards && a is not CardPager);
        return card is null || shown.Contains(card);
    }

    /// <summary>
    ///     What is wrong with the laid-out tab: a control on screen arranged beyond the pane (or, inside a scroller,
    ///     beyond its width), arranged smaller than it asked to be outside a scroller, or two sibling blocks of one
    ///     stack or grid overlapping. Empty when it fits. A part inside a clipping ancestor (an indeterminate
    ///     <see cref="ProgressBar" />'s sliding indicators) draws only where that ancestor is, and the ancestor is
    ///     judged itself, so its span is not; a scroller's viewport is no such ancestor, or sideways scrolling would
    ///     hide. A card the pager put away is arranged off stage on purpose and is not judged.
    /// </summary>
    private static List<string> Misfits(Control pane)
    {
        var problems = new List<string>();
        var paneW = pane.Bounds.Width;
        var paneH = pane.Bounds.Height;
        var cards = pane.GetVisualDescendants().OfType<ColumnFlowPanel>().ToList();
        foreach (var control in pane.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds is { Width: > 0, Height: > 0 } && !c.GetVisualAncestors().Any(a => a is ScrollBar)))
        {
            if (!cards.All(panel => OnScreen(panel, control))) continue;
            if (control.TranslatePoint(new Point(0, 0), pane) is not { } origin) continue;
            var ancestors = control.GetVisualAncestors().TakeWhile(a => !ReferenceEquals(a, pane)).ToList();
            var scroller = ancestors.OfType<ScrollViewer>().FirstOrDefault();
            var clipped = ancestors.Any(a => a is { ClipToBounds: true } and not ScrollContentPresenter);
            var label = $"{control.GetType().Name}{(string.IsNullOrEmpty(control.Name) ? "" : " '" + control.Name + "'")}";
            if (!clipped && (origin.X < -0.5 || origin.X + control.Bounds.Width > paneW + 0.5))
            {
                problems.Add($"{label} spans x {origin.X:0.#}..{origin.X + control.Bounds.Width:0.#} of a {paneW:0.#} px pane");
            }

            if (!clipped && scroller == null && (origin.Y < -0.5 || origin.Y + control.Bounds.Height > paneH + 0.5))
            {
                problems.Add($"{label} spans y {origin.Y:0.#}..{origin.Y + control.Bounds.Height:0.#} of a {paneH:0.#} px pane");
            }

            if (scroller == null && control.Parent is Panel && control.Bounds.Height + 0.5 < control.DesiredSize.Height - control.Margin.Top - control.Margin.Bottom)
            {
                problems.Add($"{label} is arranged {control.Bounds.Height:0.#} px high but asked for {control.DesiredSize.Height - control.Margin.Top - control.Margin.Bottom:0.#}");
            }

            if (control is not Panel panel || control is not (Grid or StackPanel)) continue;
            var blocks = panel.Children.Where(c => c.IsEffectivelyVisible && c.Bounds is { Width: > 0, Height: > 0 }).ToList();
            for (var i = 0; i < blocks.Count; i++)
            {
                for (var j = i + 1; j < blocks.Count; j++)
                {
                    if (panel is Grid && Grid.GetRow(blocks[i]) == Grid.GetRow(blocks[j])) continue;
                    var overlap = blocks[i].Bounds.Intersect(blocks[j].Bounds);
                    if (overlap.Width > 1 && overlap.Height > 1)
                    {
                        problems.Add($"{blocks[i].GetType().Name} and {blocks[j].GetType().Name} overlap by {overlap.Width:0.#}x{overlap.Height:0.#} in {label}");
                    }
                }
            }
        }

        return problems;
    }

    private static void WithPane(int width, double height, Action<Window, InspectorViewModel, IssuesInspectorView> body)
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
            var view = new InspectorView { DataContext = vm };
            window = NarrowWidthLayout.NewWindow(width, height, view);
            window.Show();
            vm.SelectedTabIndex = InspectorViewModel.IssuesTabIndex;
            NarrowWidthLayout.Settle(window);
            SettleProjectCheck(window, vm.ExportReadiness);
            SettleIssuesPass(window, vm);
            SeedContent(vm);
            NarrowWidthLayout.Settle(window);
            body(window, vm, view.GetVisualDescendants().OfType<IssuesInspectorView>().Single());
        }
        finally
        {
            vm.IssuesSections.Open(IssuesSection.Findings);
            vm.SelectedTabIndex = previousTab;
            window?.Close();
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
        }
    }

    /// <summary>
    ///     Opening the tab starts the whole-project check off the UI thread; its busy bar shows until the result
    ///     lands, and the landing rewrites the section. The seed and the measurement wait for it: under load the
    ///     check can still run after the layout passes that usually outlast it.
    /// </summary>
    private static void SettleProjectCheck(Window window, ExportReadinessViewModel readiness)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!ProjectCheckLanded(readiness) && DateTime.UtcNow < deadline)
        {
            NarrowWidthLayout.Settle(window);
            if (!ProjectCheckLanded(readiness)) Thread.Sleep(5);
        }

        Assert.True(ProjectCheckLanded(readiness), "the whole-project check did not land in 60 s: the box was starved or the check never finished");
        NarrowWidthLayout.Settle(window);
    }

    /// <summary>
    ///     Seeding the song queues the Issues tab's own validation (a 150 ms debounce, then a pass off the UI
    ///     thread, #380). Left queued, the timer fired in the settle after <see cref="SeedContent" /> and its pass
    ///     landed mid-measurement, replacing the 14 seeded findings with the song's real 267 rows, so a section
    ///     scrolled to its end was then measured on different content. The queued refresh runs now and the seed
    ///     waits for its pass.
    /// </summary>
    private static void SettleIssuesPass(Window window, InspectorViewModel vm)
    {
        vm.FlushIssueRefresh();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!IssuesPassLanded(vm) && DateTime.UtcNow < deadline)
        {
            NarrowWidthLayout.Settle(window);
            if (!IssuesPassLanded(vm)) Thread.Sleep(5);
        }

        Assert.True(IssuesPassLanded(vm), "the Issues tab's validation did not land in 60 s: the box was starved or the pass never finished");
        NarrowWidthLayout.Settle(window);
    }

    private static bool IssuesPassLanded(InspectorViewModel vm) => vm.Issues.Pending.IsCompleted;

    private static bool ProjectCheckLanded(ExportReadinessViewModel readiness) =>
        readiness.ProjectReportPending.IsCompleted && !readiness.IsProjectChecking;

    /// <summary>Several arrangements with findings in two, a failed check, and a round trip with four groups of several places.</summary>
    private static void SeedContent(InspectorViewModel vm)
    {
        var warnings = Enumerable.Range(0, 9).Select(i =>
            new ArrangementIssue(ArrangementIssueType.LinkNextMissingTargetNote, $"The linked note at bar {i + 3} has no target on the next string.", 4000 + i * 1350)).ToList();
        // a tone change on a note, which the fix menu moves a 16th earlier: one row carries a Fix
        var toneAt = new FractionalPosition(6);
        vm.ProjectState.ActiveArrangement!.ToneChanges.Add(new ToneChange { Position = toneAt, ToneName = "lead" });
        warnings[0] = new ArrangementIssue(ArrangementIssueType.ToneChangeOnNote, "The tone change at bar 6 lands on a note.", toneAt.GetPositionMs(vm.ProjectState.Beats));
        var important = Enumerable.Range(0, 5).Select(i =>
            new ArrangementIssue(ArrangementIssueType.HandShapeWithoutTemplate, $"The hand shape at bar {i + 11} matches no chord template of this arrangement.", 24000 + i * 1800)).ToList();
        vm.Issues.IssueGroups.Clear();
        vm.Issues.IssueGroups.Add(new InspectorIssueGroup(ArrangementIssueSeverity.Important, important));
        vm.Issues.IssueGroups.Add(new InspectorIssueGroup(ArrangementIssueSeverity.Warning, warnings));
        vm.Issues.IssueCount = warnings.Count + important.Count;

        var readiness = vm.ExportReadiness;
        readiness.ProjectVerdict = "2 things stop the export";
        readiness.ProjectCounts = "2 errors · 12 warnings";
        readiness.ProjectIsReady = false;
        readiness.ProjectArrangements =
        [
            new ProjectArrangementRow(0, "Lead", "Difficulty 6.4", "2 errors · 9 warnings", false, true),
            new ProjectArrangementRow(1, "Rhythm", "Difficulty 4.1", "3 warnings", false, false),
            new ProjectArrangementRow(2, "Bass", "Difficulty 3.0", "No problems", true, false),
            new ProjectArrangementRow(3, "Combo", "Difficulty 5.2", "No problems", true, false),
            new ProjectArrangementRow(ProjectArrangementRow.SongWideIndex, "Lyrics and song-wide checks", "", "2 warnings", false, false, 2,
                "Show the findings no arrangement owns, such as the lyric checks")
        ];

        var report = new EofConversionReport();
        string[] subjects = ["Lead", "Rhythm", "Bass"];
        foreach (var (loss, kind, count) in new[]
                 {
                     (EofLoss.UnusedTone, EofConversionKind.Dropped, 3),
                     (EofLoss.BendRounded, EofConversionKind.Approximated, 7),
                     (EofLoss.GraceNote, EofConversionKind.Approximated, 4),
                     (EofLoss.FlatBend, EofConversionKind.Approximated, 3)
                 })
        {
            for (var i = 0; i < count; i++) report.Add(kind, loss, subjects[i % subjects.Length], "note", 2000 + i * 3100 + (int)loss * 7, $"detail {i}");
        }
        var summary = EofLossSummary.From(report);
        var model = readiness.EofRoundTripModel;
        model.OffThread = _ => Task.FromResult(new EofRoundTripCheck(summary, ""));
        model.Show(summary, "", []);
    }
}
