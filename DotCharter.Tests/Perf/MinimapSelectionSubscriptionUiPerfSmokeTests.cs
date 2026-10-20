using Avalonia.Controls;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <c>MinimapOverviewControl</c> subscribed to the selection collections
///     TWICE and leaked one handler per lifetime: <c>SubscribeSelection</c> ran
///     from BOTH <c>OnPropertyChanged(EditorProperty)</c> and
///     <c>OnAttachedToVisualTree</c>, and <c>App.axaml.cs</c> sets the window
///     DataContext BEFORE <c>Show()</c>, so the Editor binding lands before the
///     attach and both paths ran. Detach then removed only ONE copy, leaving the
///     detached control rooted by the long-lived editor view model's collections
///     (the shape <c>DotCharter.Tests/UI/ViewSubscriptionLeakTests.cs</c> exists
///     for), and every marquee burst raised two <c>InvalidateVisual</c> per event.
///     <para>
///         <b>Why <c>*UiPerfSmokeTests</c> (outside the default gate):</b> the
///         second subscription only happens on a real visual-tree ATTACH, and
///         <c>OnAttachedToVisualTree</c> is protected — reproducing it needs a
///         window in the Avalonia headless session, which is exactly what that
///         suffix is reserved for in this repo.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class MinimapSelectionSubscriptionUiPerfSmokeTests
{
    [Fact]
    public async Task BindingBeforeAttach_LeavesExactlyOneSelectionHandler()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var editor = CreateEditor();
            var minimap = new MinimapOverviewControl
            {
                // The production order: the Editor binding lands FIRST (DataContext
                // before Show), the attach comes second.
                Editor = editor
            };

            var window = new Window { Width = 400, Height = 80, Content = minimap };
            window.Show();
            try
            {
                window.UpdateLayout();

                var note = new GuitarNote { StringIndex = 0, Fret = 3 };

                var before = minimap.SelectionHandlerInvocations;
                editor.SelectedNotes.Add(note);
                Assert.Equal(before + 1, minimap.SelectionHandlerInvocations);

                editor.SelectedNotes.Clear();
                Assert.Equal(before + 2, minimap.SelectionHandlerInvocations);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DetachDropsEverySelectionHandler_AndReattachRestoresExactlyOne()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var editor = CreateEditor();
            var minimap = new MinimapOverviewControl { Editor = editor };
            var host = new Panel { Children = { minimap } };
            var window = new Window { Width = 400, Height = 80, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();

                var note = new GuitarNote { StringIndex = 0, Fret = 3 };

                // DETACHED: not one handler may survive, or the long-lived
                // selection collections root the dead control forever.
                host.Children.Remove(minimap);
                var afterDetach = minimap.SelectionHandlerInvocations;
                editor.SelectedNotes.Add(note);
                Assert.Equal(afterDetach, minimap.SelectionHandlerInvocations);

                // RE-ATTACH (float/redock, pop-out): exactly one comes back —
                // no property change occurs on a re-parent, so the attach path
                // has to be the one that restores it.
                editor.SelectedNotes.Clear();
                host.Children.Add(minimap);
                window.UpdateLayout();

                var afterReattach = minimap.SelectionHandlerInvocations;
                editor.SelectedNotes.Add(note);
                Assert.Equal(afterReattach + 1, minimap.SelectionHandlerInvocations);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static ChartEditorViewModel CreateEditor()
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var selection = new SelectionService();
        var history = new HistoryService(logger);
        var config = new FakeConfigurationService();

        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
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
