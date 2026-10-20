using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.Controls;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The song map's use marks (#485), on the real control: it draws a mark for
///     EXACTLY the bulk-edit preview's members on the level on screen, it draws the
///     selected HAND SHAPES (its selection subscription used to cover notes and
///     chords only), and both changes repaint through the one visibility-gated
///     funnel — never while the map is hidden. The geometry is
///     <c>SongMapMarks</c>' (default gate: <c>UI/Controls/SongMapMarksTests</c>).
///     <para>
///         <b>Why <c>*UiPerfSmokeTests</c>:</b> the subscription and the repaint gate
///         only exist on a real visual-tree attach. The headless session draws
///         nothing visible, so what is asserted is what <c>Render</c> handed the
///         drawing context: the control's <c>HighlightMarks</c> / <c>SelectionMarks</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class MinimapUseMarksUiPerfSmokeTests
{
    [Fact]
    public async Task ThePreviewsMarks_AreExactlyItsMembersOnTheLevelOnScreen()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, arr, state) = CreateEditor();
            var level = arr.Levels[0];
            var marked = new[] { level.Chords[1], level.Chords[3] };
            var bar = level.HandShapes[0];
            var minimap = new MinimapOverviewControl { Editor = editor, TotalTimeMs = 32000 };
            var window = new Window { Width = 800, Height = 60, Content = minimap };
            window.Show();
            try
            {
                window.UpdateLayout();
                editor.PublishUseHighlight(ChordUseHighlight.From([.. marked, arr.Levels[1].Chords[0]], [bar], arr));
                RenderOnce(minimap);

                Assert.Equal([marked[0], marked[1], bar], minimap.HighlightMarks.Select(m => m.Sound));
                // The map scales by the APP's ProjectState (it resolves it on attach),
                // which CreateEditor built the editor on.
                var scale = minimap.Bounds.Width / state.EffectiveTimelineLengthMs;
                Assert.Equal(marked[0].Position.GetPositionMs(editor.Beats) * scale,
                    minimap.HighlightMarks[0].Bounds.Center.X, 3);

                editor.PublishUseHighlight(ChordUseHighlight.None);
                RenderOnce(minimap);
                Assert.Empty(minimap.HighlightMarks);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SelectedHandShapes_ReachTheSongMap()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, arr, _) = CreateEditor();
            var bar = arr.Levels[0].HandShapes[0];
            var minimap = new MinimapOverviewControl { Editor = editor, TotalTimeMs = 32000 };
            var window = new Window { Width = 800, Height = 60, Content = minimap };
            window.Show();
            try
            {
                window.UpdateLayout();
                var handled = minimap.SelectionHandlerInvocations;
                var repaints = minimap.RepaintRequests;

                editor.SelectedHandShapes.Add(bar);
                RenderOnce(minimap);

                Assert.Equal(handled + 1, minimap.SelectionHandlerInvocations);
                Assert.Equal(repaints + 1, minimap.RepaintRequests);
                Assert.Equal([bar], minimap.SelectionMarks.Select(m => m.Sound));
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>A preview change repaints once through the funnel while shown, not
    /// at all while hidden — and detach drops the subscription.</summary>
    [Fact]
    public async Task APreviewChange_RepaintsThroughTheGatedFunnel_AndDetachUnsubscribes()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, arr, _) = CreateEditor();
            var minimap = new MinimapOverviewControl { Editor = editor, TotalTimeMs = 32000 };
            var host = new Panel { Children = { minimap } };
            var window = new Window { Width = 800, Height = 60, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                var repaints = minimap.RepaintRequests;
                editor.PublishUseHighlight(ChordUseHighlight.From([arr.Levels[0].Chords[0]], [], arr));
                Assert.Equal(1, minimap.UseHighlightHandlerInvocations);
                Assert.Equal(repaints + 1, minimap.RepaintRequests);

                host.IsVisible = false;
                var hidden = minimap.RepaintRequests;
                editor.PublishUseHighlight(ChordUseHighlight.From([arr.Levels[0].Chords[1]], [], arr));
                Assert.Equal(2, minimap.UseHighlightHandlerInvocations);
                Assert.Equal(hidden, minimap.RepaintRequests);

                host.IsVisible = true;
                host.Children.Remove(minimap);
                editor.PublishUseHighlight(ChordUseHighlight.None);
                Assert.Equal(2, minimap.UseHighlightHandlerInvocations);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The song map in its three states — nothing marked, "select uses" (chords
    ///     AND bars selected), and the bulk-edit preview — and, with
    ///     <c>DOTCHARTER_UI_SWEEP_DUMP=1 DOTCHARTER_RIG_CAPTURE_DIR=&lt;dir&gt;</c>,
    ///     written to <c>song-map-&lt;state&gt;.png</c> for the PR's review. Without
    ///     the variables it is a plain layout smoke test.
    /// </summary>
    [Fact]
    public async Task TheSongMap_InEachMarkState_LaysOutAndCanBeCaptured()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, arr, _) = CreateEditor(dense: true);
            var level = arr.Levels[0];
            var minimap = new MinimapOverviewControl { Editor = editor, TotalTimeMs = 32000, Height = 40 };
            var frame = new Border
            {
                Padding = new Thickness(6),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E0B15")),
                Child = minimap
            };
            var window = new Window { Width = 900, Height = 52, Content = frame };
            window.Show();
            try
            {
                Shoot(window, minimap, "1-nothing-marked");

                // "Select uses" on the A5 row (#486): its chords and its bars.
                foreach (var chord in level.Chords.Where(c => c.TemplateId == 0)) editor.SelectedChords.Add(chord);
                foreach (var bar in level.HandShapes.Where(h => h.TemplateId == 0)) editor.SelectedHandShapes.Add(bar);
                Shoot(window, minimap, "2-select-uses");

                editor.SelectedChords.Clear();
                editor.SelectedHandShapes.Clear();
                editor.SelectedChords.Add(level.Chords[0]);
                editor.PublishUseHighlight(ChordUseHighlight.From(
                    [.. level.Chords.Where(c => c.TemplateId == 0)],
                    [.. level.HandShapes.Where(h => h.TemplateId == 0)], arr));
                Shoot(window, minimap, "3-bulk-edit-preview");
                RenderOnce(minimap);
                Assert.Equal(16, minimap.HighlightMarks.Count); // eight A5 chords, eight bars
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The open hand-shape suggestions (#639) on the real control: a band per suggested bar of the
    ///     level on screen, which an accept (one history step, so a new revision) removes, its Undo
    ///     brings back, and View ▸ Show suggested hand shapes off clears — and a frame that changed
    ///     nothing reruns no detector.
    /// </summary>
    [Fact]
    public async Task OpenSuggestions_AreBandsOnTheMap_ThatFollowAcceptUndoAndTheToggle()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, arr, state) = CreateEditor(dense: false, out var history);
            var level = arr.Levels[0];
            AddSuggestionRuns(level, 46, 52);
            var minimap = new MinimapOverviewControl { Editor = editor, TotalTimeMs = 32000 };
            var window = new Window { Width = 800, Height = 60, Content = minimap };
            var shown = state.ShowSuggestedHandShapes;
            state.ShowSuggestedHandShapes = true;
            window.Show();
            try
            {
                window.UpdateLayout();
                RenderOnce(minimap);
                Assert.Equal(2, minimap.SuggestionMarks.Count);
                // Which bar each band stands for; the bands' geometry is SongMapSuggestionMarksTests'.
                Assert.Equal(new FractionalPosition(46), BandStart(minimap, 0));
                var computed = minimap.SuggestionComputeCount;
                RenderOnce(minimap);
                Assert.Equal(computed, minimap.SuggestionComputeCount);

                var acceptor = new HandShapeSuggestionAcceptor(history, new FakeAppLogger());
                var first = HandShapeSuggester.Suggest(arr, level)[0];
                Assert.Equal(1, acceptor.Accept(arr, level, [first], arpeggio: false));
                RenderOnce(minimap);
                Assert.Single(minimap.SuggestionMarks);
                Assert.Equal(new FractionalPosition(52), BandStart(minimap, 0));

                history.Undo();
                RenderOnce(minimap);
                Assert.Equal(2, minimap.SuggestionMarks.Count);

                state.ShowSuggestedHandShapes = false;
                RenderOnce(minimap);
                Assert.Empty(minimap.SuggestionMarks);
            }
            finally
            {
                state.ShowSuggestedHandShapes = shown;
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>The song map with open suggestions over the second half of a riff; with
    /// <c>DOTCHARTER_UI_SWEEP_DUMP=1 DOTCHARTER_RIG_CAPTURE_DIR=&lt;dir&gt;</c> written to
    /// <c>song-map-4-open-suggestions.png</c>. Without the variables a plain layout smoke test.</summary>
    [Fact]
    public async Task TheSongMap_WithOpenSuggestions_LaysOutAndCanBeCaptured()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, arr, state) = CreateEditor(dense: true);
            var level = arr.Levels[0];
            var half = new FractionalPosition(32);
            foreach (var note in level.Notes.Where(n => n.Position >= half).ToList()) level.Notes.Remove(note);
            foreach (var chord in level.Chords.Where(c => c.Position >= half).ToList()) level.Chords.Remove(chord);
            foreach (var bar in level.HandShapes.Where(h => h.Position >= half).ToList()) level.HandShapes.Remove(bar);
            AddSuggestionRuns(level, 34, 40);
            AddSuggestionRuns(level, 48, 56);
            var minimap = new MinimapOverviewControl { Editor = editor, TotalTimeMs = 32000, Height = 40 };
            var frame = new Border
            {
                Padding = new Thickness(6),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E0B15")),
                Child = minimap
            };
            var window = new Window { Width = 900, Height = 52, Content = frame };
            var shown = state.ShowSuggestedHandShapes;
            state.ShowSuggestedHandShapes = true;
            window.Show();
            try
            {
                Shoot(window, minimap, "4-open-suggestions");
                RenderOnce(minimap);
                Assert.Equal(4, minimap.SuggestionMarks.Count);
            }
            finally
            {
                state.ShowSuggestedHandShapes = shown;
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static FractionalPosition BandStart(MinimapOverviewControl minimap, int index) =>
        ((HandShape)minimap.SuggestionMarks[index].Sound).Position;

    /// <summary>Two runs of single notes the suggester bars: an open-C arpeggio (four beats) at
    /// <paramref name="cBeat" /> and an Am grip at the fifth fret (two beats) at <paramref name="amBeat" />.</summary>
    private static void AddSuggestionRuns(ChartLevel level, double cBeat, double amBeat)
    {
        (int String, int Fret)[] c = [(1, 3), (2, 2), (3, 0), (4, 1), (5, 0), (4, 1), (3, 0), (2, 2)];
        (int String, int Fret)[] am = [(1, 7), (2, 7), (3, 5), (4, 5)];
        foreach (var (start, notes) in new[] { (cBeat, c), (amBeat, am) })
        {
            for (var i = 0; i < notes.Length; i++)
            {
                level.Notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(0, new Fraction((int)Math.Round((start + i * 0.5) * 4), 4)),
                    StringIndex = notes[i].String,
                    Fret = notes[i].Fret
                });
            }
        }
    }

    private static void Shoot(Window window, Control view, string state)
    {
        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
        Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"the song map laid out as {view.Bounds}");
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"song-map-{state}.png"), new PngBitmapEncoderOptions());
    }

    private static void RenderOnce(MinimapOverviewControl minimap)
    {
        using var target = new RenderTargetBitmap(new PixelSize(800, 60));
        using var ctx = target.CreateDrawingContext(false);
        minimap.Render(ctx);
    }

    /// <summary>A song-length capture chart: a riff of single notes with an A5 and
    /// a D5 power chord (each under its bar) every other bar, replacing the five
    /// bare chords.</summary>
    private static void Densify(Arrangement arrangement)
    {
        var level = arrangement.Levels[0];
        level.Chords.Clear();
        level.HandShapes.Clear();
        for (var beat = 0; beat < 62; beat++)
        {
            if (beat % 8 is 0 or 4)
            {
                // A5 on the low E and A strings, D5 one string higher; both 5-7.
                var id = beat % 16 < 8 ? 0 : 1;
                var chord = new Chord { TemplateId = id, Position = new FractionalPosition(beat) };
                chord.ChordNotes.Add(new ChordNote { StringIndex = id, Fret = 5, Sustain = new FractionalPosition(1) });
                chord.ChordNotes.Add(new ChordNote { StringIndex = id + 1, Fret = 7, Sustain = new FractionalPosition(1) });
                level.Chords.Add(chord);
                level.HandShapes.Add(new HandShape
                {
                    TemplateId = id, Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 2)
                });
            }
            else
            {
                level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 3 + beat % 3, Fret = beat % 5 + 2 });
            }
        }
    }

    /// <summary>Two levels; level 0 has five A5 chords two beats apart and one bar.</summary>
    private static (ChartEditorViewModel Editor, Arrangement Arrangement, ProjectState State) CreateEditor(
        bool dense = false) => CreateEditor(dense, out _);

    private static (ChartEditorViewModel Editor, Arrangement Arrangement, ProjectState State) CreateEditor(
        bool dense, out HistoryService history)
    {
        var logger = new FakeAppLogger();
        // The app's own state: the map reads the song length from it.
        var state = App.Services?.GetService<ProjectState>() ?? new ProjectState();
        var selection = new SelectionService();
        history = new HistoryService(logger);
        var config = new FakeConfigurationService();

        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        arrangement.Levels.Add(new ChartLevel());
        for (var i = 0; i < 5; i++)
            arrangement.Levels[0].Chords.Add(new Chord { Position = new FractionalPosition(i * 8 + 2) });
        arrangement.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(40), EndPosition = new FractionalPosition(44)
        });
        arrangement.Levels[1].Chords.Add(new Chord { Position = new FractionalPosition(6) });
        if (dense) Densify(arrangement);
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];
        for (var i = 0; i < 64; i++) state.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var tracks = new TrackContextRegistry(state, selection);
        return (new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger), tracks, FakeLocalization.KeysOnly()),
            arrangement, state);
    }
}
