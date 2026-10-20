// The Amp & FX rig's FIRST open (#234): what the post-first-paint prewarm must have
// paid for so the click pays nothing — the virtualized gear list's rows, and the
// artwork those rows draw.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.Views.Shell;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Pins the two halves of the rig's first-open cost that the prewarm used to
///     leave on the click — measured on a Release R2R build at 315 ms for the
///     first open against ~2 ms for every later one.
///     <list type="bullet">
///         <item><b>The gear list's rows.</b> <see cref="HiddenSubtreePrewarmer.Realize" />
///         measured the list's <c>VirtualizingStackPanel</c> before it had a
///         viewport, so it built one row; the viewport that arrived after the next
///         layout pass invalidated it under a hidden overlay nothing re-measures,
///         and the click built the visible rows (185 controls, ten faceplates).
///         <see cref="HiddenSubtreePrewarmer.ScheduleDrain" /> now SETTLES each
///         part the turn after it realizes it.</item>
///         <item><b>The artwork.</b> The compositor records <c>Render</c> for a
///         realized control under a hidden overlay too, and the faceplates decoded
///         their WebP art inside it, on the UI thread: 13 decodes, ~190 ms.
///         <see cref="RigArtworkStore.GetForRender(string, int, Visual)" /> queues a
///         hidden control's decode on the pool instead.</item>
///     </list>
///     Structure only, never a wall clock (repo rule).
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigFirstOpenPrewarmUiPerfSmokeTests
{
    private static readonly TimeSpan DeadlockCap = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TheDrain_LeavesTheRigsFirstOpenNothingToBuild()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var rig = TestRigBuilders.Pedalboard(
                new Infrastructure.FakeDspChainService(), new Infrastructure.FakeDspTonePresetService(), LocalizationService.Instance);
            var view = new AmpFxRigView { DataContext = new AmpFxRigViewModel(rig) };
            // The shell's shape: the rig under a hidden overlay panel, beside the
            // surface that is showing.
            var overlay = new Panel { IsVisible = false, Children = { view } };
            var window = new Window
            {
                Width = 1600,
                Height = 1000,
                Content = new Grid { Children = { new Border(), overlay } }
            };
            window.Show();
            try
            {
                var done = false;
                HiddenSubtreePrewarmer.ScheduleDrain([overlay], () => window.Bounds.Size, (_, _) => done = true);
                var deadline = DateTime.UtcNow + DeadlockCap;
                while (!done && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    if (!done) await Task.Delay(5);
                }

                Assert.True(done, "the prewarm drain never finished (deadlock detector, not a budget — the box was starved)");
                Assert.False(overlay.IsVisible);

                var gearList = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "RigGearGroups");
                Assert.True(gearList.ItemCount > 1, "the gear catalogue is empty; this case proves nothing");
                Assert.True(gearList.GetRealizedContainers().Count() > 1,
                    $"the prewarm realized {gearList.GetRealizedContainers().Count()} of {gearList.ItemCount} gear rows — " +
                    "the virtualizing panel never got its viewport, and the first open builds the rows on the click");

                // The first open itself: nothing may be constructed now.
                var before = view.GetVisualDescendants().ToHashSet();
                overlay.IsVisible = true;
                window.UpdateLayout();
                var created = view.GetVisualDescendants().Count(v => !before.Contains(v));
                Assert.True(created == 0,
                    $"opening the prewarmed rig built {created} controls on the click");
                Assert.True(view.Bounds is { Width: > 0, Height: > 0 }, "the rig did not lay out");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AHiddenRender_DecodesArtworkOffTheUiThread_AndAVisibleOneDoesNot()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            // Decode widths nothing else in the app asks for, so both keys are cold.
            const string file = RigFaceplates.ToggleUp;
            const int hiddenWidth = 77;
            const int visibleWidth = 79;

            var hidden = new Border { Width = 20, Height = 20 };
            var visible = new Border { Width = 20, Height = 20 };
            var window = new Window
            {
                Width = 200,
                Height = 200,
                Content = new StackPanel { Children = { new Panel { IsVisible = false, Children = { hidden } }, visible } }
            };
            window.Show();
            try
            {
                Assert.False(hidden.IsEffectivelyVisible);
                Assert.True(visible.IsEffectivelyVisible);

                // Hidden: no bitmap now, the decode is queued.
                Assert.Null(RigArtworkStore.GetForRender(file, hiddenWidth, hidden));
                Assert.True(RigArtworkStore.IsPending(file, hiddenWidth) || RigArtworkStore.IsCached(file, hiddenWidth));

                var deadline = DateTime.UtcNow + DeadlockCap;
                while (!RigArtworkStore.IsCached(file, hiddenWidth) && DateTime.UtcNow < deadline)
                    await Task.Delay(10);
                Assert.True(RigArtworkStore.IsCached(file, hiddenWidth),
                    "the queued decode never landed (deadlock detector, not a budget — the box was starved)");
                Assert.False(RigArtworkStore.IsPending(file, hiddenWidth));
                Assert.NotNull(RigArtworkStore.GetForRender(file, hiddenWidth, hidden));

                // Visible: what the user can see is never drawn without its picture.
                Assert.NotNull(RigArtworkStore.GetForRender(file, visibleWidth, visible));
                Assert.False(RigArtworkStore.IsPending(file, visibleWidth));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
