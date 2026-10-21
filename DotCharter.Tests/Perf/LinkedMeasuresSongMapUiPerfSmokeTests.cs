using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.State.Linking;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Linked measures on the real song map: hovering or selecting a link group draws a mark over
///     each of its placements in the arrangement on screen and nothing for any other group; a
///     change of the emphasised group repaints once, a pointer moving over the same chip does not,
///     and detach drops the subscription. The geometry is <c>SongMapLinkMarks</c>' (default gate:
///     <c>UI/Linking/SongMapLinkMarksTests</c>). Writes <c>song-map-links-*.png</c> with
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class LinkedMeasuresSongMapUiPerfSmokeTests
{
    [Fact]
    public async Task The_song_map_marks_the_emphasised_groups_placements_only() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, h) = CreateEditor();
            var chorus = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 1));
            var other = h.Link((h.Lead, 7), (h.Rhythm, 7));
            var minimap = new MinimapOverviewControl { Editor = editor, Height = 40 };
            var host = new Panel { Children = { minimap } };
            var window = new Window { Width = 900, Height = 52, Content = host };
            window.Show();
            try
            {
                Shoot(window, minimap, "1-none");
                Assert.Empty(minimap.LinkMarks);

                var repaints = minimap.RepaintRequests;
                editor.LinkHighlight.SetHovered(chorus.Id, this, new Point(10, 10));
                Assert.Equal(repaints + 1, minimap.RepaintRequests);
                // The pointer moving over the same chip: Changed fires, the map does not repaint.
                Shoot(window, minimap, "2-hover");
                var settled = minimap.RepaintRequests;
                editor.LinkHighlight.SetHovered(chorus.Id, this, new Point(14, 10));
                Assert.Equal(settled, minimap.RepaintRequests);

                Assert.Equal([At(1, 0), At(4, 0)], minimap.LinkMarks.Select(m => m.Placement.Position));
                Assert.All(minimap.LinkMarks, m => Assert.Equal(chorus.Letter, m.Letter));
                var scale = minimap.Bounds.Width / LengthMs(h);
                Assert.Equal(At(4, 0).GetPositionMs(h.Project.Beats) * scale, minimap.LinkMarks[1].Bounds.X, 1);

                // The panel's selected group, once the hover leaves.
                editor.LinkHighlight.SetSelected(other.Id);
                editor.LinkHighlight.SetHovered(null);
                Shoot(window, minimap, "3-selected");
                Assert.Equal([At(7, 0)], minimap.LinkMarks.Select(m => m.Placement.Position));

                editor.LinkHighlight.SetSelected(null);
                Shoot(window, minimap, "4-cleared");
                Assert.Empty(minimap.LinkMarks);

                host.Children.Remove(minimap);
                var handled = minimap.LinkHighlightHandlerInvocations;
                editor.LinkHighlight.SetHovered(chorus.Id);
                Assert.Equal(handled, minimap.LinkHighlightHandlerInvocations);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);

    private static double LengthMs(LinkHarness h) =>
        App.Services?.GetService<ProjectState>()?.EffectiveTimelineLengthMs ?? At(9, 0).GetPositionMs(h.Project.Beats);

    private static void Shoot(Window window, MinimapOverviewControl minimap, string state)
    {
        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
        Assert.True(minimap.Bounds is { Width: > 1, Height: > 1 }, $"the song map laid out as {minimap.Bounds}");
        using (var target = new RenderTargetBitmap(new PixelSize(900, 40)))
        using (var ctx = target.CreateDrawingContext(false))
            minimap.Render(ctx);
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"song-map-links-{state}.png"), new PngBitmapEncoderOptions());
    }

    /// <summary>The link fixture's chart (Lead and a Rhythm copy) loaded into the APP's project state,
    /// which the map reads the song length from, with sections so the map reads like a song.</summary>
    private static (ChartEditorViewModel Editor, LinkHarness Links) CreateEditor()
    {
        var h = LinkHarness.WithRhythm();
        string[] sections = ["intro", "chorus", "verse", "verse", "chorus", "verse", "verse", "chorus", "outro"];
        for (var m = 0; m < sections.Length; m++)
        {
            if (m > 0 && sections[m] == sections[m - 1]) continue;
            h.Lead.EventPoints.Add(new EventPoint { Position = At(m, 0), Section = sections[m] });
        }

        var logger = new FakeAppLogger();
        var state = App.Services?.GetService<ProjectState>() ?? new ProjectState();
        state.ActiveProject = h.Project;
        state.ActiveArrangement = h.Lead;
        state.ActiveLevel = LinkHarness.Hardest(h.Lead);
        var selection = new SelectionService();
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var tracks = new TrackContextRegistry(state, selection);
        var editor = new ChartEditorViewModel(state, transport, new HistoryService(logger), selection, new TempoService(logger),
            tracks, FakeLocalization.KeysOnly());
        return (editor, h);
    }
}
