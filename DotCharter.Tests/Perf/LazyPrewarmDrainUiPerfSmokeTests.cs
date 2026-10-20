using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The hidden-surface drain consumes its list LAZILY, one item per paced turn
///     (#234). The listing used to be built up front in ONE dispatcher turn — Play
///     Mode's deferred bodies and the rig view, 34 dialog views, the three top-level
///     views: 0.7-1.9 s on the Release R2R build that no click could interrupt.
/// </summary>
/// <remarks>
///     Headless because the drain posts through the real dispatcher. The evidence
///     is ORDER, never time: an item is produced only after the part before it has
///     been realized, which an up-front list cannot do.
/// </remarks>
[Collection("HeadlessUI")]
public sealed class LazyPrewarmDrainUiPerfSmokeTests
{
    private static readonly Size Slot = new(400, 300);

    [Fact]
    public Task TheDrain_ProducesEachItem_OnlyAfterThePartBeforeItWasRealized() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var log = new List<string>();
            var parts = new List<TextBlock>();

            IEnumerable<Control?> Produce()
            {
                for (var i = 0; i < 3; i++)
                {
                    log.Add($"build {i} (part before measured: {parts.Count > 0 && parts[^1].IsMeasureValid})");
                    yield return null;

                    var text = new TextBlock { Text = "surface " + i };
                    parts.Add(text);
                    yield return new Border { IsVisible = false, Child = text };
                }
            }

            HiddenSubtreePrewarmer.ScheduleDrain(Produce(), () => Slot, (realized, total) => log.Add($"done {realized}/{total}"));

            // Nothing is produced in the caller's turn: the first item waits for the drain's.
            Assert.Empty(log);

            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

            Assert.Equal(
            [
                "build 0 (part before measured: False)",
                "build 1 (part before measured: True)",
                "build 2 (part before measured: True)",
                "done 3/3"
            ], log);
        }, CancellationToken.None);

    /// <summary>A producer that throws ends the drain through its callbacks — a
    /// listing failure is reported, and the drain never just stops answering.</summary>
    [Fact]
    public Task AThrowingProducer_EndsTheDrainThroughOnErrorAndOnDone() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var log = new List<string>();

            static IEnumerable<Control?> Produce()
            {
                yield return new Border { IsVisible = false, Child = new TextBlock { Text = "first" } };
                throw new InvalidOperationException("listing failed");
            }

            HiddenSubtreePrewarmer.ScheduleDrain(Produce(), () => Slot, (realized, total) => log.Add($"done {realized}/{total}"),
                ex => log.Add("error " + ex.Message));
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

            Assert.Equal(["error listing failed", "done 1/1"], log);
        }, CancellationToken.None);
}
