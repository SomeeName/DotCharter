using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Library;
using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI;
using DotCharter.UI.Services;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.Views.Library;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #130: the status rail's left half (the message and the running-task chip) painted over the
///     project name and the Snap readout at 1280 px, and at 1920 px while a task ran. It was a
///     horizontal <c>StackPanel</c>: that measures its children with infinite width, so the
///     message's <c>TextTrimming</c> never engaged and the panel drew past its <c>*</c> column.
///     The Library footer had the same fault in front of the Load button.
///     <para>
///         The rail is the app's own <see cref="StatusBarViewModel" /> over the UI sweep's seeded
///         song, so the metric cluster on the right carries what it carries in the editor. Width-
///         shaped, so this class is in the release's de-DE / ja-JP layout filter.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class StatusRailNarrowWidthUiPerfSmokeTests
{
    private const string LongMessage =
        "3D preview unavailable: the compositor has no GPU interop on this device, so the highway panel stays blank until restart.";

    private const string LongTaskLabel =
        "Separating stems: The Extraordinarily Long Band Name Appreciation Society Orchestra – Symphony No. 4 in D Minor";

    [Theory]
    [InlineData(1280)]
    [InlineData(1920)]
    public async Task TheMessageAndTheTaskChip_StayLeftOfTheMetricCluster(int width) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var statusBar = services.GetRequiredService<IStatusBarService>();
            var vm = services.GetRequiredService<StatusBarViewModel>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;
            Window? window = null;
            try
            {
                NarrowWidthLayout.SeedSong(state);
                // The rail as the chart editor shows it: the editing and transport readouts, and
                // the chart-lint counters, all in the metric cluster.
                vm.IsEditorSurface = true;
                vm.SetChartIssueCounts(17, 250);
                state.SetMessage(LongMessage, StatusSeverity.Warning);

                var view = new StatusBar { DataContext = vm };
                window = NarrowWidthLayout.NewWindow(width, 120, view);
                // The breakpoint class the app's main window wears at this width (Compact
                // below 1600), which the rail's narrow-width rules key off (#213).
                UiBreakpointController.Apply(window, UiBreakpointController.For(width));
                window.Show();
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"statusbar-{width}");

                var message = NarrowWidthLayout.Named<TextBlock>(view, "StatusMessageText");
                var cluster = NarrowWidthLayout.Named<StackPanel>(view, "StatusMetricCluster");
                var (clusterLeft, _) = NarrowWidthLayout.Span(cluster, view);
                var (_, messageRight) = NarrowWidthLayout.Span(message, view);
                Console.WriteLine($"[narrow-width] status rail @{width}, no task: message ends {messageRight:0.#}, " +
                                  $"metrics start {clusterLeft:0.#} ({cluster.Bounds.Width:0.#} px wide).");

                // 1. The message alone: it gets the column and trims inside it.
                Assert.True(message.Bounds.Width > 1, $"at {width} px the message laid out {message.Bounds.Width:0.#} px wide — nothing of it shows");
                Assert.True(messageRight <= clusterLeft + 0.5,
                    $"at {width} px the status message ends at {messageRight:0.#} px, past the metric cluster's left edge at " +
                    $"{clusterLeft:0.#} px — it paints over the project name (#130).");
                // Either it fits (ja-JP's shorter cluster leaves 1920 px room for all of it) or it
                // ends in an ellipsis — never a full-length line cut by its slot.
                Assert.True(message.TextLayout.TextLines.Any(l => l.HasCollapsed)
                            || message.TextLayout.WidthIncludingTrailingWhitespace <= message.Bounds.Width + 0.5,
                    $"a {LongMessage.Length}-character message at {width} px is neither whole nor ellipsized — it was not given a width to trim to.");

                // 2. …and with a task running: the chip and the message share the column, neither
                // reaches the metrics, and they do not overlap each other.
                statusBar.BeginTask("narrow-width-test", LongTaskLabel, indeterminate: false, cancel: () => { });
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"statusbar-task-{width}");
                Assert.True(vm.HasActiveTask, "the task never reached the rail — the case would test nothing");

                var chip = NarrowWidthLayout.Named<Border>(view, "StatusTaskChip");
                (clusterLeft, _) = NarrowWidthLayout.Span(cluster, view);
                var (messageLeft, messageEnd) = NarrowWidthLayout.Span(message, view);
                var (chipLeft, chipRight) = NarrowWidthLayout.Span(chip, view);
                Console.WriteLine($"[narrow-width] status rail @{width}, task: message {messageLeft:0.#}..{messageEnd:0.#}, " +
                                  $"chip {chipLeft:0.#}..{chipRight:0.#}, metrics start {clusterLeft:0.#}.");

                Assert.True(chip.IsEffectivelyVisible && chip.Bounds.Width > 1, $"the task chip laid out as {chip.Bounds}");
                Assert.True(chipRight <= clusterLeft + 0.5,
                    $"at {width} px the task chip ends at {chipRight:0.#} px, past the metric cluster's left edge at " +
                    $"{clusterLeft:0.#} px — it paints over the project name (#130).");
                Assert.True(messageEnd <= chipLeft + 0.5 || message.Bounds.Width < 1,
                    $"at {width} px the message ({messageLeft:0.#}..{messageEnd:0.#}) runs under the task chip ({chipLeft:0.#}..{chipRight:0.#}).");
                // The chip never overprints its own parts: bar and cancel button lie inside it,
                // side by side.
                var bar = chip.GetVisualDescendants().OfType<ProgressBar>().Single();
                var cancel = chip.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible);
                var (barLeft, barRight) = NarrowWidthLayout.Span(bar, view);
                var (cancelLeft, cancelRight) = NarrowWidthLayout.Span(cancel, view);
                Assert.True(barLeft >= chipLeft - 0.5 && cancelRight <= chipRight + 0.5 && barRight <= cancelLeft + 0.5,
                    $"at {width} px the chip ({chipLeft:0.#}..{chipRight:0.#}) overprints its parts: bar {barLeft:0.#}..{barRight:0.#}, " +
                    $"cancel {cancelLeft:0.#}..{cancelRight:0.#}.");

                // #213: the task's NAME shows, not just its bar. At 1280 the chip used to keep its
                // 160 px minimum right-aligned in a ~150 px cell, and the cell clipped the clock and
                // the label off its left edge. The full name is the chip's tooltip either way.
                var label = NarrowWidthLayout.Named<TextBlock>(view, "StatusTaskLabel");
                var cell = NarrowWidthLayout.Named<DockPanel>(view, "StatusMessageCell");
                var (labelLeft, labelRight) = NarrowWidthLayout.Span(label, view);
                var (cellLeft, _) = NarrowWidthLayout.Span(cell, view);
                var visibleLabel = labelRight - Math.Max(labelLeft, cellLeft);
                Console.WriteLine($"[narrow-width] status rail @{width}, task label {labelLeft:0.#}..{labelRight:0.#}, cell starts {cellLeft:0.#}.");
                Assert.True(labelLeft >= cellLeft - 0.5 && visibleLabel >= 120,
                    $"at {width} px the task label shows {visibleLabel:0.#} px ({labelLeft:0.#}..{labelRight:0.#}, cell starts {cellLeft:0.#}) — " +
                    "the rail shows a progress bar with no name (#213).");
                Assert.Equal(LongTaskLabel, ToolTip.GetTip(chip));

                // Where the rail has room for both (a 1920 px window), both show.
                if (chipLeft - messageLeft > 200)
                    Assert.True(message.Bounds.Width > 100, $"at {width} px the message shrank to {message.Bounds.Width:0.#} px beside the chip");
            }
            finally
            {
                statusBar.CompleteTask("narrow-width-test");
                window?.Close();
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
            }
        }, CancellationToken.None);

    /// <summary>
    ///     #638: a message's one verb ("Show" on a lossy EOF export) sits right after the text, and a
    ///     message too long for the rail trims before the verb does — both stay left of the metrics.
    /// </summary>
    [Theory]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    [InlineData(1920, false)]
    public async Task AMessagesVerb_FollowsTheText_AndOutlastsTheTrim(int width, bool longMessage) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var vm = services.GetRequiredService<StatusBarViewModel>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;
            Window? window = null;
            try
            {
                NarrowWidthLayout.SeedSong(state);
                vm.IsEditorSurface = true;
                state.SetMessage(longMessage ? LongMessage : "EOF project written: song.eof — 3 things could not be kept exactly",
                    StatusSeverity.Warning, new StatusMessageAction("Show", () => { }));

                var view = new StatusBar { DataContext = vm };
                window = NarrowWidthLayout.NewWindow(width, 120, view);
                UiBreakpointController.Apply(window, UiBreakpointController.For(width));
                window.Show();
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"statusbar-verb-{width}-{(longMessage ? "long" : "short")}");

                var message = NarrowWidthLayout.Named<TextBlock>(view, "StatusMessageText");
                var verb = NarrowWidthLayout.Named<Button>(view, "StatusMessageActionButton");
                var cluster = NarrowWidthLayout.Named<StackPanel>(view, "StatusMetricCluster");
                var (clusterLeft, _) = NarrowWidthLayout.Span(cluster, view);
                var (messageLeft, messageRight) = NarrowWidthLayout.Span(message, view);
                var (verbLeft, verbRight) = NarrowWidthLayout.Span(verb, view);
                Console.WriteLine($"[narrow-width] status verb @{width} long={longMessage}: message {messageLeft:0.#}..{messageRight:0.#}, " +
                                  $"verb {verbLeft:0.#}..{verbRight:0.#}, metrics start {clusterLeft:0.#}.");

                Assert.True(verb.IsEffectivelyVisible && verb.Bounds.Width > 1, $"the verb laid out as {verb.Bounds}");
                Assert.True(message.Bounds.Width > 1, $"the message laid out {message.Bounds.Width:0.#} px wide");
                Assert.True(verbLeft >= messageRight - 0.5, $"the verb ({verbLeft:0.#}) starts inside the message (ends {messageRight:0.#})");
                Assert.True(verbLeft - messageRight <= 16, $"the verb sits {verbLeft - messageRight:0.#} px after the text, adrift on the rail");
                Assert.True(verbRight <= clusterLeft + 0.5, $"the verb ends at {verbRight:0.#}, past the metrics at {clusterLeft:0.#}");
                if (longMessage)
                    Assert.True(message.TextLayout.TextLines.Any(l => l.HasCollapsed), "the long message was not trimmed to make room for its verb");

                state.SetMessage("Project saved", StatusSeverity.Success);
                NarrowWidthLayout.Settle(window);
                Assert.False(verb.IsVisible, "the verb outlived its message");
            }
            finally
            {
                window?.Close();
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
            }
        }, CancellationToken.None);

    [Fact]
    public async Task TheLibraryFooter_KeepsALongStatusLeftOfTheLoadButton() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // A song whose title alone is 200 characters, selected: the footer then says
            // "Ready to load — <title> · <path>".
            var screen = FakeLibraryScreen.LongText();
            var longRow = new FakeLibraryRow
            {
                Title = string.Join(" ", Enumerable.Repeat(FakeFlowText.LongSongTitle, 2))[..200],
                Artist = FakeFlowText.LongArtist,
                Paths = ["L", "R", "B"]
            };
            screen.Rows = [longRow, .. screen.Rows];
            screen.SelectedRow = longRow;
            var view = new LibraryView { DataContext = screen };
            // The Library inside a 1280 px window, beside the 76 px activity rail.
            var window = NarrowWidthLayout.NewWindow(1204, 800, view);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "library-footer-1204");
                var footer = NarrowWidthLayout.Named<Border>(view, "LibraryFooter");
                var load = NarrowWidthLayout.Named<Button>(view, "LibraryLoad");
                var status = footer.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == screen.LoadStatusText);
                var tag = NarrowWidthLayout.Named<Border>(view, "LibraryReplacesTag");
                Assert.True(tag.IsEffectivelyVisible, "the fixture no longer replaces the open project — the tag is untested");

                var (_, statusRight) = NarrowWidthLayout.Span(status, footer);
                var (_, tagRight) = NarrowWidthLayout.Span(tag, footer);
                var (loadLeft, _) = NarrowWidthLayout.Span(load, footer);
                Console.WriteLine($"[narrow-width] library footer: status ends {statusRight:0.#}, tag ends {tagRight:0.#}, Load starts {loadLeft:0.#}.");

                Assert.True(statusRight <= loadLeft + 0.5,
                    $"the load status ends at {statusRight:0.#} px, under the Load button at {loadLeft:0.#} px (#130).");
                Assert.True(tagRight <= loadLeft + 0.5,
                    $"the 'replaces the open project' tag ends at {tagRight:0.#} px, under the Load button at {loadLeft:0.#} px (#130).");
                Assert.True(status.TextLayout.TextLines.Any(l => l.HasCollapsed),
                    "the long load status is not ellipsized — it was not given a width to trim to.");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
}
