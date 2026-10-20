using Avalonia.Controls;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Both 2D editor surfaces kept rendering while the editor tab was HIDDEN.
///     The whole editor subtree is <c>IsVisible</c>-gated
///     (<c>Views/MainWindow.axaml</c>) — hidden, NOT detached — and Avalonia
///     leaves <c>Bounds</c> at its last arranged value when <c>IsVisible</c> goes
///     false, so <c>ChartRenderer.RenderTimelineFrame</c>'s only guard
///     (<c>Bounds.Width/Height &lt;= 0</c>) never fired. Meanwhile
///     <c>OnProjectStateChanged</c> went on firing on <c>CurrentTimeMs</c> at
///     60 Hz during playback and requested frames, so the whole pipeline
///     (snapshot → ribbon layout → 10 layers → vertex upload → Vulkan submit →
///     compositor import) ran at compositor rate for a surface nobody could see.
///     Worst case is Play Mode, where the invisible 2D timeline competes with the
///     3D highway for the same UI thread and GPU. <c>MinimapOverviewControl</c>
///     had the same hole at ~30 Hz.
///     <para>
///         Both now gate on EFFECTIVE visibility through
///         <c>EffectiveVisibilityWatcher</c> — the same mechanism as
///         <c>Preview3DComposedCanvas</c> and
///         <c>MenuBackdropControl</c>, and never an <c>IsEffectivelyVisible</c>
///         property branch, which Avalonia 12 does not raise.
///     </para>
///     <para>
///         <b>Why <c>*UiPerfSmokeTests</c> (outside the default gate):</b>
///         effective visibility only exists inside a real visual tree with a
///         window at its root — the hidden ANCESTOR is the whole point, and a
///         detached control cannot model it.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class HiddenEditorRenderGateUiPerfSmokeTests
{
    [Fact]
    public async Task ChartRenderer_FrameRequestsDoNotReachTheBackendWhileHidden()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var renderer = new ChartRenderer();
            var host = new Panel { Children = { renderer } };
            var window = new Window { Width = 640, Height = 320, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(renderer.IsEffectivelyVisibleForRender,
                    "the renderer is attached to a shown window and still reads as not visible");

                // Visible: the request funnel every playback frame goes through
                // (OnProjectStateChanged, the ribbon handler, the tools, the
                // self-perpetuating playback loop) reaches the backend.
                var visibleBaseline = renderer.BackendFrameRequests;
                renderer.RequestNextFrameRendering();
                Assert.Equal(visibleBaseline + 1, renderer.BackendFrameRequests);

                // The ANCESTOR hides — exactly what MainSplitGrid does when the
                // user switches to Play Mode or the song browser. Bounds stay
                // arranged, so nothing downstream would have stopped this.
                host.IsVisible = false;
                Assert.False(renderer.IsEffectivelyVisibleForRender);

                var hiddenBaseline = renderer.BackendFrameRequests;
                for (var i = 0; i < 60; i++) renderer.RequestNextFrameRendering();
                Assert.Equal(hiddenBaseline, renderer.BackendFrameRequests);

                // Coming back must request ONE frame by itself, or the timeline
                // shows the frame it was hidden with.
                host.IsVisible = true;
                Assert.True(renderer.IsEffectivelyVisibleForRender);
                Assert.Equal(hiddenBaseline + 1, renderer.BackendFrameRequests);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Minimap_RepaintsDoNotHappenWhileHidden()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var minimap = new MinimapOverviewControl();
            var host = new Panel { Children = { minimap } };
            var window = new Window { Width = 400, Height = 80, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(minimap.IsEffectivelyVisibleForRender);

                var visibleBaseline = minimap.RepaintRequests;
                minimap.InvalidateStaticLayer();
                Assert.Equal(visibleBaseline + 1, minimap.RepaintRequests);

                host.IsVisible = false;
                Assert.False(minimap.IsEffectivelyVisibleForRender);

                var hiddenBaseline = minimap.RepaintRequests;
                for (var i = 0; i < 30; i++) minimap.InvalidateStaticLayer();
                Assert.Equal(hiddenBaseline, minimap.RepaintRequests);

                host.IsVisible = true;
                Assert.True(minimap.IsEffectivelyVisibleForRender);
                Assert.Equal(hiddenBaseline + 1, minimap.RepaintRequests);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }
}
