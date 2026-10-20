using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The chart timeline is a custom-drawn composed-Vulkan surface with no visual
///     children, so UI Automation saw one unnamed group (#858). Its
///     <see cref="ChartRendererAutomationPeer" /> names the surface, exposes the
///     selection as a read-only value, and announces each move. This class drives
///     the editor's OWN keyboard verb (<c>SelectNextNote</c>, Shift+PageDown by
///     default) and reads the peer a screen reader would read.
///     <para>
///         <b>Why <c>*UiPerfSmokeTests</c>:</b> the announcement rides the real
///         attach/detach subscription and the dispatcher, so it needs a window in
///         the headless session. The wording itself is pinned in the default gate
///         by <c>UI/ChartTimelineDescriptionTests</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChartRendererAutomationPeerUiPerfSmokeTests
{
    [Fact]
    public async Task SelectingAChord_NamesIt_AndAnnouncesTheMove()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var editor = CreateEditor();
            var renderer = new ChartRenderer { Editor = editor, Focusable = true }; // as MainWindow.axaml declares it
            var window = new Window { Width = 640, Height = 320, Content = renderer };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(renderer.Bounds.Width > 0 && renderer.Bounds.Height > 0, "the timeline did not lay out");
                Assert.True(renderer.Focus(), "the timeline refused keyboard focus");

                var peer = ControlAutomationPeer.CreatePeerForElement(renderer);
                Assert.IsType<ChartRendererAutomationPeer>(peer);
                var value = Assert.IsAssignableFrom<IValueProvider>(peer);
                Assert.Equal("Chart timeline, Lead, bar 1 beat 1", peer.GetName());
                Assert.Equal("No selection", value.Value);
                Assert.True(value.IsReadOnly);
                Assert.False(string.IsNullOrWhiteSpace(peer.GetHelpText()), "the peer does not say which keys move it");

                var raised = new List<AutomationPropertyChangedEventArgs>();
                peer.PropertyChanged += (_, e) => raised.Add(e);

                Assert.Single(editor.PlacedChords);
                editor.SelectNextNote(extendSelection: false);
                Assert.Single(editor.SelectedChords);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Chord Am, bar 12 beat 3, 4 notes", value.Value);
                var valueChange = Assert.Single(raised, e => e.Property == ValuePatternIdentifiers.ValueProperty);
                Assert.Equal("No selection", valueChange.OldValue);
                Assert.Equal("Chord Am, bar 12 beat 3, 4 notes", valueChange.NewValue);

                // A burst (Clear + Add) is ONE announcement, and a detached
                // timeline announces nothing: it let go of the singleton.
                raised.Clear();
                editor.Selection.ClearSelections();
                Dispatcher.UIThread.RunJobs();
                Assert.Single(raised, e => e.Property == ValuePatternIdentifiers.ValueProperty);

                window.Content = null;
                raised.Clear();
                editor.SelectedChords.Add(editor.PlacedChords[0]);
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(raised);
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
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 3, Name = "Am" });
        var level = new ChartLevel();
        // Beat index 46 in 4/4 = bar 12, beat 3.
        var chord = new Chord { Position = new FractionalPosition(46), TemplateId = 3 };
        for (var s = 0; s < 4; s++) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 2 });
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        for (var i = 0; i < 64; i++) state.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0 });
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = level;

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var tracks = new TrackContextRegistry(state, selection);
        return new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger), tracks, FakeLocalization.English());
    }
}
