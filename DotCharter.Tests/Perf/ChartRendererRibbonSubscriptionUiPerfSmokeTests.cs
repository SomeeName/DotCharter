using Avalonia.Controls;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <c>ChartRenderer.SyncRibbonSubscription</c> was reachable ONLY from
///     <c>OnPropertyChanged</c> when <c>Editor</c>/<c>OverrideArrangement</c>
///     changed. <c>OnDetachedFromVisualTree</c> tore the subscription down and
///     <c>OnAttachedToVisualTree</c> did not restore it — and a re-parent raises
///     no property change. Since the chart timeline composite must stay a SINGLE
///     instance across layout toggles and pop-out (<c>DockPaneContractTests</c>),
///     re-parenting IS detach + attach on the same object, so after the first
///     pop-out/redock, toggling a ROWS ribbon row never produced another frame.
///     <para>
///         <b>Why <c>*UiPerfSmokeTests</c> (outside the default gate):</b> the
///         defect only exists across a real visual-tree detach + re-attach, and
///         both hooks are protected — it needs a window in the Avalonia headless
///         session.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChartRendererRibbonSubscriptionUiPerfSmokeTests
{
    [Fact]
    public async Task RibbonToggles_StillReachTheRenderer_AfterDetachAndReattach()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var editor = CreateEditor(out var arrangement);
            var renderer = new ChartRenderer { Editor = editor };
            var ribbon = editor.TrackContexts.GetOrCreate(arrangement).Ribbon;

            var host = new Panel { Children = { renderer } };
            var window = new Window { Width = 640, Height = 320, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();

                var before = renderer.RibbonChangeNotifications;
                ribbon.ShowPhrases = !ribbon.ShowPhrases;
                Assert.True(renderer.RibbonChangeNotifications > before,
                    "A ROWS ribbon toggle did not reach the renderer even while attached.");

                // Pop out / redock: the SAME instance leaves and re-enters the
                // tree, and nothing raises a property change on the way.
                host.Children.Remove(renderer);
                var afterDetach = renderer.RibbonChangeNotifications;
                ribbon.ShowPhrases = !ribbon.ShowPhrases;
                Assert.Equal(afterDetach, renderer.RibbonChangeNotifications);

                host.Children.Add(renderer);
                window.UpdateLayout();

                var afterReattach = renderer.RibbonChangeNotifications;
                ribbon.ShowPhrases = !ribbon.ShowPhrases;
                Assert.True(renderer.RibbonChangeNotifications > afterReattach,
                    "After a detach + re-attach the ROWS ribbon toggle no longer reaches the " +
                    "renderer — OnAttachedToVisualTree must call SyncRibbonSubscription.");

                // …and it is still exactly ONE handler, not a stack of them.
                var single = renderer.RibbonChangeNotifications;
                ribbon.ShowLyrics = !ribbon.ShowLyrics;
                Assert.Equal(single + 1, renderer.RibbonChangeNotifications);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static ChartEditorViewModel CreateEditor(out Arrangement arrangement)
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var selection = new SelectionService();
        var history = new HistoryService(logger);
        var config = new FakeConfigurationService();

        var project = new ChartProject();
        arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var tracks = new TrackContextRegistry(state, selection);
        return new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger), tracks, FakeLocalization.KeysOnly());
    }
}
