using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using System.Globalization;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Fakes.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The AI chat pane (#559), every fake variant built, attached and laid out at the two edges
///     of the dock's width (340 and 420 px): the head, the thread and the composer each measure
///     non-zero, the history dropdown opens under the title as wide as the pane with its search
///     box, every turn and every history entry realizes, a delete question shows, and
///     nothing a reader sees runs past the pane's right edge or is cut short — long titles trim,
///     long messages wrap. Width-shaped, so the class is in the release's de-DE / ja-JP layout filter. With
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> it writes one PNG per variant and width
///     (<see cref="NarrowWidthLayout.ArtifactDirectory" />).
///     <para>
///         And the box's keys, through real key presses: Shift+Enter starts a new line,
///         Enter sends.
///     </para>
///     <para>
///         And #600's slim footer: the head and the composer stay under a height budget in every
///         shipped culture, and the box grows from two lines.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class AiChatPaneConstructionUiPerfSmokeTests
{
    private const double PaneHeight = 860;

    public static TheoryData<string, double> VariantsAtWidths
    {
        get
        {
            var data = new TheoryData<string, double>();
            foreach (var variant in FakeAiChatScreen.Variants)
            {
                data.Add(variant.Name, 340);
                data.Add(variant.Name, 420);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(VariantsAtWidths))]
    public Task EveryFakeVariant_LaysOutInsideTheDock(string variant, double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeAiChatScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new AiChatPaneView { DataContext = screen };
            var window = NarrowWidthLayout.NewWindow(width, PaneHeight, view);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"ai-chat-{ScreenSlug(variant)}-{width:0}");
                var at = $"{variant} at {width:0} px";

                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{at}: the pane laid out as {view.Bounds}");
                AssertLaidOut(view, "AiChatHead", at);
                AssertLaidOut(view, "AiChatComposer", at);
                AssertLaidOut(view, "AiChatInput", at);
                AssertLaidOut(view, "AiChatModel", at);
                AssertLaidOut(view, "AiChatJob", at);
                AssertLaidOut(view, "AiChatScope", at);
                AssertLaidOut(view, "AiChatArrangement", at);
                AssertLaidOut(view, screen.IsBusy ? "AiChatCancel" : "AiChatSend", at);
                if (!screen.HasProvider) AssertLaidOut(view, "AiChatOpenSettings", at);

                // The composer is pinned: it ends at the pane's bottom edge, whatever the thread holds.
                var composer = NarrowWidthLayout.Named<Border>(view, "AiChatComposer");
                Assert.True(Math.Abs(NarrowWidthLayout.TopIn(composer, view) + composer.Bounds.Height - view.Bounds.Height) < 1,
                    $"{at}: the composer is not pinned to the bottom");

                if (screen.History.IsOpen)
                {
                    // The dropdown: in the window's overlay layer, under the title, as wide as the pane.
                    var dropdown = HistoryDropdown(view);
                    AssertLaidOut(dropdown, "AiChatHistorySearch", at);
                    AssertLaidOut(dropdown, "AiChatHistoryCaption", at);
                    if (screen.History.HasNotice) AssertLaidOut(dropdown, "AiChatHistoryNotice", at);
                    if (!screen.History.HasEntries) AssertLaidOut(dropdown, "AiChatHistoryEmpty", at);
                    var head = NarrowWidthLayout.Named<Border>(view, "AiChatHead");
                    Assert.True(NarrowWidthLayout.TopIn(dropdown, view) >= NarrowWidthLayout.TopIn(head, view) + head.Bounds.Height - 1,
                        $"{at}: the dropdown does not open under the title");
                    Assert.True(Math.Abs(dropdown.Bounds.Width - head.Bounds.Width) < 1,
                        $"{at}: the dropdown is {dropdown.Bounds.Width:0} px wide under a {head.Bounds.Width:0} px title");
                    var entries = NarrowWidthLayout.Named<ItemsControl>(dropdown, "AiChatHistoryEntries");
                    Assert.Equal(screen.History.Entries.Count, entries.GetRealizedContainers().Count());
                    Assert.All(entries.GetRealizedContainers(), e => Assert.True(e.Bounds.Height > 20, $"{at}: an entry laid out as {e.Bounds}"));
                    foreach (var asking in screen.History.Entries.Where(e => e.IsConfirmingDelete))
                    {
                        var row = entries.ContainerFromItem(asking) ?? throw new Xunit.Sdk.XunitException($"{at}: no row for {asking}");
                        var prompt = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == asking.DeletePromptText);
                        Assert.True(prompt.IsEffectivelyVisible && prompt.Bounds.Height > 1, $"{at}: the delete question is not shown");
                    }

                    AssertNothingPastTheRightEdge(dropdown, view, width, at);
                }

                // The thread stays under the dropdown.
                if (screen.HasTurns)
                {
                    var turns = NarrowWidthLayout.Named<ItemsControl>(view, "AiChatTurns");
                    Assert.Equal(screen.Turns.Count, turns.GetRealizedContainers().Count());
                    Assert.All(turns.GetRealizedContainers(), t => Assert.True(t.Bounds.Height > 20, $"{at}: a turn laid out as {t.Bounds}"));
                    if (screen.IsBusy) AssertLaidOut(view, "AiChatWorking", at);
                    if (screen.HasError) AssertLaidOut(view, "AiChatError", at);
                }
                else
                {
                    AssertLaidOut(view, "AiChatEmpty", at);
                    Assert.Equal(screen.EmptyText, NarrowWidthLayout.Named<TextBlock>(view, "AiChatEmpty").Text);
                }

                // #640: the box's hint is the picked task's; Retry shows on exactly the replies that offer it.
                Assert.Equal(screen.InputHint, NarrowWidthLayout.Named<TextBox>(view, "AiChatInput").PlaceholderText);
                var retries = Retries(view);
                Assert.Equal(screen.Turns.Count(t => t.CanRetry), retries.Count);
                Assert.All(retries, r => Assert.True(r.Bounds is { Width: > 1, Height: > 1 }, $"{at}: Retry laid out as {r.Bounds}"));
                // #803: "Ask the AI to fix these" shows on exactly the replies that offer it.
                var fixes = view.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "AiChatFixThese" && b.IsEffectivelyVisible).ToList();
                Assert.Equal(screen.Turns.Count(t => t.CanFix), fixes.Count);
                Assert.All(fixes, b => Assert.True(b.Bounds is { Width: > 1, Height: > 1 }, $"{at}: the fix laid out as {b.Bounds}"));

                AssertNothingPastTheRightEdge(view, view, width, at);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task ShiftEnter_StartsANewLine_AndEnter_Sends() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeAiChatScreen.Default();
            var turnsBefore = screen.Turns.Count;
            var view = new AiChatPaneView { DataContext = screen };
            var window = NarrowWidthLayout.NewWindow(380, PaneHeight, view);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var input = NarrowWidthLayout.Named<TextBox>(view, "AiChatInput");
                input.Focus();
                window.KeyTextInput("Mark the sections");
                window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
                window.KeyTextInput("from bar 9");
                NarrowWidthLayout.Settle(window);

                Assert.Equal(turnsBefore, screen.Turns.Count);
                Assert.Contains('\n', screen.InputText.Replace("\r\n", "\n", StringComparison.Ordinal));
                Assert.StartsWith("Mark the sections", screen.InputText, StringComparison.Ordinal);
                Assert.EndsWith("from bar 9", screen.InputText, StringComparison.Ordinal);

                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                NarrowWidthLayout.Settle(window);

                Assert.Equal(turnsBefore + 1, screen.Turns.Count);
                var sent = screen.Turns[^1];
                Assert.True(sent.IsUser);
                Assert.EndsWith("from bar 9", sent.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("from bar 9\n", sent.Text + "", StringComparison.Ordinal);
                Assert.Equal("", screen.InputText);
                Assert.True(screen.IsBusy, "Enter did not send");
                AssertLaidOut(view, "AiChatWorking", "after Enter");
                AssertLaidOut(view, "AiChatCancel", "after Enter");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>#640: a click on an unanswered reply's Retry sends the message again: the reply leaves
    /// the thread and the pane shows it working, through the real button's binding.</summary>
    [Fact]
    public Task Retry_OnAnUnansweredReply_SendsItAgain() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeAiChatScreen.Unanswered();
            var turnsBefore = screen.Turns.Count;
            var view = new AiChatPaneView { DataContext = screen };
            var window = NarrowWidthLayout.NewWindow(380, PaneHeight, view);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var retry = Assert.Single(Retries(view));
                retry.BringIntoView();
                NarrowWidthLayout.Settle(window);
                var (left, _) = NarrowWidthLayout.Span(retry, window);
                var at = new Avalonia.Point(left + retry.Bounds.Width / 2, NarrowWidthLayout.TopIn(retry, window) + retry.Bounds.Height / 2);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                NarrowWidthLayout.Settle(window);

                Assert.True(screen.IsBusy, "the click did not send the message again");
                Assert.Equal(turnsBefore - 1, screen.Turns.Count);
                Assert.True(screen.Turns[^1].IsUser);
                Assert.Empty(Retries(view));
                AssertLaidOut(view, "AiChatWorking", "after Retry");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     #640: each task's own empty pane and hint, on the REAL pane view model with a chart open, in
    ///     every shipped culture at the dock's narrowest width: the line is that task's, laid out and
    ///     wrapped inside the pane; the box's hint is that task's and fits.
    /// </summary>
    [Theory]
    [InlineData(AiChartJob.FingeringOnly, "FingeringOnly")]
    [InlineData(AiChartJob.FhpAndFingering, "Fingering")]
    [InlineData(AiChartJob.SectionsAndPhrases, "Structure")]
    [InlineData(AiChartJob.Explain, "Explain")]
    [InlineData(AiChartJob.Tones, "Tones")]
    public Task EachTasksEmptyPaneAndHint_LayOutInsideThePane_InEveryShippedCulture(AiChartJob job, string name) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var restore = LocalizationService.Instance.ConfiguredCulture;
            var lines = new List<string>();
            try
            {
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    var screen = RealScreen(withChart: true);
                    screen.SelectedJob = screen.Jobs.Single(j => j is AiChatOptionItem { Key: AiChartJob k } && k == job);
                    var empty = LocalizationService.Instance["AiChat_Empty_" + name];
                    var hint = LocalizationService.Instance["AiChat_Hint_" + name];
                    Assert.Equal(empty, screen.EmptyText);
                    Assert.Equal(hint, screen.InputHint);
                    lines.Add(empty + "|" + hint);
                    var view = new AiChatPaneView { DataContext = screen };
                    var window = NarrowWidthLayout.NewWindow(340, PaneHeight, view);
                    window.Show();
                    try
                    {
                        NarrowWidthLayout.Settle(window);
                        NarrowWidthLayout.Capture(window, $"ai-chat-empty-{name.ToLowerInvariant()}-{culture}");
                        var at = $"{name} in {culture} at 340 px";
                        AssertLaidOut(view, "AiChatEmpty", at);
                        Assert.Equal(empty, NarrowWidthLayout.Named<TextBlock>(view, "AiChatEmpty").Text);
                        var input = NarrowWidthLayout.Named<TextBox>(view, "AiChatInput");
                        Assert.Equal(hint, input.PlaceholderText);
                        // The box's template holds more than one copy of the hint; the visible one is laid out.
                        var shown = input.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == hint && t.IsEffectivelyVisible);
                        Assert.True(shown.Bounds is { Width: > 1, Height: > 1 }, $"{at}: the hint laid out as {shown.Bounds}");
                        AssertNothingPastTheRightEdge(view, view, 340, at);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }

            // Proof the sweep is not one language three times.
            Assert.Equal(Cultures.Length, lines.Distinct(StringComparer.Ordinal).Count());
        }, CancellationToken.None);

    /// <summary>Every shipped resx: German is the longest, Japanese the widest per glyph.</summary>
    private static readonly string[] Cultures = ["en-US", "de-DE", "ja-JP"];

    /// <summary>
    ///     #563 / #614: the Task picker's "Ask / explain" and "Tones" entries, on the REAL pane view model in
    ///     each shipped culture at the dock's narrowest width: picked, the closed box shows it inside the pane;
    ///     opened, every entry realizes and lays out, the picked one with its own words.
    /// </summary>
    [Theory]
    [InlineData(AiChartJob.Explain, "AiChat_Job_Explain", "explain")]
    [InlineData(AiChartJob.Tones, "AiChat_Job_Tones", "tones")]
    public Task TheTaskPicker_OffersTheTask_AndLaysItOut_InEveryShippedCulture(AiChartJob job, string key, string capture) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var restore = LocalizationService.Instance.ConfiguredCulture;
            var labels = new List<string>();
            try
            {
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    var screen = RealScreen();
                    var explain = screen.Jobs.Single(j => j is AiChatOptionItem { Key: AiChartJob k } && k == job);
                    Assert.Equal(LocalizationService.Instance[key], explain.Label);
                    labels.Add(explain.Label + "|" + LocalizationService.Instance["AiChat_Job_Tip"]);
                    screen.SelectedJob = explain;
                    var view = new AiChatPaneView { DataContext = screen };
                    var window = NarrowWidthLayout.NewWindow(340, PaneHeight, view);
                    window.Show();
                    try
                    {
                        NarrowWidthLayout.Settle(window);
                        var at = $"{culture} at 340 px";
                        var combo = NarrowWidthLayout.Named<ComboBox>(view, "AiChatJob");
                        var shown = combo.GetVisualDescendants().OfType<TextBlock>()
                            .Single(t => t.Text == explain.Label && t.IsEffectivelyVisible);
                        Assert.True(shown.Bounds is { Width: > 1, Height: > 1 }, $"{at}: the picked task laid out as {shown.Bounds}");
                        AssertNothingPastTheRightEdge(view, view, 340, at);

                        combo.IsDropDownOpen = true;
                        NarrowWidthLayout.Settle(window);
                        NarrowWidthLayout.Capture(window, $"ai-chat-task-{capture}-{culture}");
                        var rows = combo.GetRealizedContainers().ToList();
                        Assert.Equal(screen.Jobs.Count, rows.Count);
                        Assert.All(rows, r => Assert.True(r.IsEffectivelyVisible && r.Bounds is { Width: > 1, Height: > 10 },
                            $"{at}: a task entry laid out as {r.Bounds}"));
                        var row = combo.ContainerFromItem(explain) ?? throw new Xunit.Sdk.XunitException($"{at}: no entry for {key}");
                        var text = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == explain.Label);
                        Assert.True(text.IsEffectivelyVisible && text.Bounds is { Width: > 1, Height: > 1 },
                            $"{at}: the Ask / explain entry's text laid out as {text.Bounds}");
                        combo.IsDropDownOpen = false;
                        NarrowWidthLayout.Settle(window);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }

            // Proof the sweep is not one language three times ("Tones" is the German word too, so the
            // tip, which differs in every language, carries the proof alongside the label).
            Assert.Equal(Cultures.Length, labels.Distinct(StringComparer.Ordinal).Count());
        }, CancellationToken.None);

    /// <summary>
    ///     #600: the head and the composer are SLIM, so the conversation keeps the pane's height. The
    ///     owner's screenshot had a ~300 px footer of full-height boxes (Task, scope + arrangement,
    ///     a 76 px box, the model picker beside Send) under a 60 px head. On the REAL view model in
    ///     every shipped culture, at both edges of the dock: the composer stays under
    ///     <see cref="ComposerBudget" />, the head under <see cref="HeadBudget" />, Task · scope ·
    ///     arrangement share ONE row, the model picker sits beside Send, and the box starts at two
    ///     lines and grows with what is typed — up to a cap, past which it scrolls.
    /// </summary>
    [Fact]
    public Task TheHeadAndComposer_StaySlim_AndTheBoxGrowsFromTwoLines_InEveryShippedCulture() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var restore = LocalizationService.Instance.ConfiguredCulture;
            // Every culture and width is measured (and captured) before anything fails, so a red
            // run reports the whole table, not its first row.
            var failures = new List<string>();
            void Check(bool ok, string failure)
            {
                if (!ok) failures.Add(failure);
            }

            try
            {
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    foreach (var width in new[] { 340d, 420d })
                    {
                        var screen = RealScreen();
                        var view = new AiChatPaneView { DataContext = screen };
                        var window = NarrowWidthLayout.NewWindow(width, PaneHeight, view);
                        window.Show();
                        try
                        {
                            NarrowWidthLayout.Settle(window);
                            NarrowWidthLayout.Capture(window, $"ai-chat-compact-{culture}-{width:0}");
                            var at = $"{culture} at {width:0} px";
                            var head = NarrowWidthLayout.Named<Border>(view, "AiChatHead");
                            var composer = NarrowWidthLayout.Named<Border>(view, "AiChatComposer");
                            var thread = NarrowWidthLayout.Named<ScrollViewer>(view, "AiChatThread");
                            var input = NarrowWidthLayout.Named<TextBox>(view, "AiChatInput");
                            NarrowWidthLayout.Report(
                                $"#600 {at}: head {head.Bounds.Height:0.#} px, composer {composer.Bounds.Height:0.#} px, " +
                                $"thread {thread.Bounds.Height:0.#} px of {view.Bounds.Height:0.#}, empty box {input.Bounds.Height:0.#} px");
                            Check(head.Bounds.Height <= HeadBudget,
                                $"{at}: the head is {head.Bounds.Height:0.#} px, over the {HeadBudget:0} px budget");
                            Check(composer.Bounds.Height <= ComposerBudget,
                                $"{at}: the composer is {composer.Bounds.Height:0.#} px, over the {ComposerBudget:0} px budget");

                            // Task · scope · arrangement on ONE row; the model picker beside Send.
                            var job = NarrowWidthLayout.Named<ComboBox>(view, "AiChatJob");
                            foreach (var name in new[] { "AiChatScope", "AiChatArrangement" })
                            {
                                var other = NarrowWidthLayout.Named<ComboBox>(view, name);
                                Check(Math.Abs(NarrowWidthLayout.TopIn(other, view) - NarrowWidthLayout.TopIn(job, view)) < 1,
                                    $"{at}: {name} is not on the Task picker's row");
                            }

                            var model = NarrowWidthLayout.Named<ComboBox>(view, "AiChatModel");
                            var send = NarrowWidthLayout.Named<Button>(view, "AiChatSend");
                            Check(Math.Abs(NarrowWidthLayout.TopIn(model, view) + model.Bounds.Height / 2 -
                                                 (NarrowWidthLayout.TopIn(send, view) + send.Bounds.Height / 2)) < 1,
                                $"{at}: the model picker is not beside Send");

                            // The box: two lines empty, taller with three, capped with thirty.
                            var lineHeight = input.FontSize * 1.1;
                            var empty = input.Bounds.Height;
                            Check(empty >= 2 * lineHeight, $"{at}: the empty box is {empty:0.#} px, under two lines");
                            Check(empty < 4 * lineHeight + 20, $"{at}: the empty box is {empty:0.#} px, more than two lines");
                            screen.InputText = "one\ntwo\nthree\nfour";
                            NarrowWidthLayout.Settle(window);
                            var four = input.Bounds.Height;
                            Check(four > empty + lineHeight, $"{at}: four lines left the box at {four:0.#} px (empty: {empty:0.#})");
                            screen.InputText = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
                            NarrowWidthLayout.Settle(window);
                            var thirty = input.Bounds.Height;
                            Check(thirty <= InputCap, $"{at}: thirty lines grew the box to {thirty:0.#} px, past its {InputCap:0} px cap");
                            Check(thread.Bounds.Height > view.Bounds.Height / 2,
                                $"{at}: a long message left the thread {thread.Bounds.Height:0.#} px of {view.Bounds.Height:0.#}");
                            AssertNothingPastTheRightEdge(view, view, width, at);
                        }
                        finally
                        {
                            window.Close();
                        }
                    }
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }, CancellationToken.None);

    /// <summary>
    ///     #603 and #602 on the REAL pane view model, in every shipped culture at both edges of the
    ///     dock: the reasoning level Settings ▸ AI sends sits beside the model picker, on its row, in
    ///     that culture's words; and the measured reason under the pickers is one laid-out line.
    /// </summary>
    [Fact]
    public Task TheEffortLabel_SitsBesideTheModel_AndTheJobNoteLaysOut_InEveryShippedCulture() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var restore = LocalizationService.Instance.ConfiguredCulture;
            var labels = new List<string>();
            try
            {
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    foreach (var width in new[] { 340d, 420d })
                    {
                        var screen = RealScreen(AiReasoningEffort.High);
                        var expected = LocalizationService.Instance.Format("AiChat_Effort",
                            LocalizationService.Instance["Ai_Effort_High"]);
                        Assert.Equal(expected, screen.EffortText);
                        Assert.Equal(LocalizationService.Instance["AiChat_JobNote_Guitar"], screen.JobNote);
                        var view = new AiChatPaneView { DataContext = screen };
                        var window = NarrowWidthLayout.NewWindow(width, PaneHeight, view);
                        window.Show();
                        try
                        {
                            NarrowWidthLayout.Settle(window);
                            NarrowWidthLayout.Capture(window, $"ai-chat-effort-{culture}-{width:0}");
                            var at = $"{culture} at {width:0} px";
                            var effort = NarrowWidthLayout.Named<TextBlock>(view, "AiChatEffort");
                            AssertLaidOut(view, "AiChatEffort", at);
                            Assert.Equal(expected, effort.Text);
                            var model = NarrowWidthLayout.Named<ComboBox>(view, "AiChatModel");
                            Assert.True(Math.Abs(NarrowWidthLayout.TopIn(model, view) + model.Bounds.Height / 2 -
                                                 (NarrowWidthLayout.TopIn(effort, view) + effort.Bounds.Height / 2)) < 2,
                                $"{at}: the reasoning label is not on the model picker's row");
                            var (_, modelRight) = NarrowWidthLayout.Span(model, view);
                            var (effortLeft, _) = NarrowWidthLayout.Span(effort, view);
                            Assert.True(effortLeft >= modelRight, $"{at}: the reasoning label overlaps the model picker");
                            Assert.True(model.Bounds.Width > 100, $"{at}: the label squeezed the model picker to {model.Bounds.Width:0.#} px");

                            AssertLaidOut(view, "AiChatJobNote", at);
                            AssertNothingPastTheRightEdge(view, view, width, at);
                            labels.Add(effort.Text!);
                        }
                        finally
                        {
                            window.Close();
                        }
                    }
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }

            // Proof the sweep is not one language three times.
            Assert.Equal(Cultures.Length, labels.Distinct(StringComparer.Ordinal).Count());
        }, CancellationToken.None);

    /// <summary>#600's budgets. The composer before it measured ~250 px with a provider (Task 36, scope 36,
    /// a 76 px box, the model row 36, a wrapped summary, five gaps) and the head 60. #602 added ONE
    /// line on purpose, the measured reason under the pickers (trimmed, the rest on hover): the composer
    /// measures 171 px with it, so the composer budget moved by that one line (170 -> 190).</summary>
    private const double ComposerBudget = 190;

    private const double HeadBudget = 44;

    /// <summary>How tall a long message may grow the box before it scrolls inside it.</summary>
    private const double InputCap = 190;

    /// <summary>The pane's real view model, with a provider configured and no chart open; its chart
    /// actions are never driven here.</summary>
    private static AiChatViewModel RealScreen(AiReasoningEffort effort = AiReasoningEffort.Default, bool withChart = false)
    {
        var config = new FakeConfigurationService();
        config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-sonnet-5-5", null, Effort: effort);
        var state = new ProjectState();
        if (withChart) NarrowWidthLayout.SeedSong(state);
        var services = new AiChatServices(new AiChatHistory(new ProjectStateAiChatChartSource(state)), new NoChartActions());
        return new AiChatViewModel(config, state, new SelectionService(), services, new TestUiDispatcher(),
            LocalizationService.Instance);
    }

    private sealed class NoChartActions : IAiChatChartActions
    {
        public bool IsRunning => false;

        public event EventHandler<AiChatApplied>? Applied { add { } remove { } }

        public Task<AiChatInstructionReply> RunInstructionAsync(AiChatInstructionRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("the layout test never sends");
    }

    private static void AssertLaidOut(Control root, string name, string at)
    {
        var control = NarrowWidthLayout.Named<Control>(root, name);
        Assert.True(control.IsEffectivelyVisible, $"{at}: {name} is not visible");
        Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{at}: {name} laid out as {control.Bounds}");
    }

    /// <summary>The Retry buttons a reader can see: one per reply that offers it.</summary>
    private static List<Button> Retries(Control root) =>
        [.. root.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "AiChatRetry" && b.IsEffectivelyVisible)];

    /// <summary>The history dropdown's panel; it lives in the window's overlay layer, not under the view.</summary>
    private static Control HistoryDropdown(AiChatPaneView view)
    {
        var popup = NarrowWidthLayout.Named<Popup>(view, "AiChatHistoryPopup");
        Assert.True(popup.IsOpen, "the history dropdown is not open");
        var panel = Assert.IsAssignableFrom<Control>(popup.Child);
        Assert.True(panel.Bounds is { Width: > 1, Height: > 1 }, $"the dropdown laid out as {panel.Bounds}");
        return panel;
    }

    /// <summary>Every visible text, button and field under <paramref name="root" /> ends inside the pane (a trimmed title, a wrapped reply).</summary>
    private static void AssertNothingPastTheRightEdge(Control root, AiChatPaneView view, double width, string at)
    {
        var parts = root.GetVisualDescendants().OfType<Control>()
            .Where(c => c is TextBlock or Button or ComboBox or TextBox && c.IsEffectivelyVisible && c.Bounds.Width > 0)
            // A scroll bar's own parts sit on the pane's edge by design.
            .Where(c => !c.GetVisualAncestors().OfType<ScrollBar>().Any())
            .ToList();
        Assert.NotEmpty(parts);
        foreach (var part in parts)
        {
            var (_, right) = NarrowWidthLayout.Span(part, view);
            Assert.True(right <= width + 0.5,
                $"{at}: {part.GetType().Name} '{(part as TextBlock)?.Text}' ends at {right:0.#} px in a {width:0} px pane");

            // A line that neither wraps nor trims is arranged at the slot's width and CLIPPED, so its
            // bounds pass the check above; its own layout is what runs long.
            if (part is TextBlock { Text.Length: > 0 } text)
            {
                Assert.True(text.TextLayout.Width <= text.Bounds.Width + 0.5,
                    $"{at}: '{text.Text}' needs {text.TextLayout.Width:0.#} px and is cut to {text.Bounds.Width:0.#}");
            }
        }
    }

    private static string ScreenSlug(string variant) => DotCharter.UI.Views.Shell.ScreenCatalog.Slug(variant);
}
