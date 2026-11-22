using Avalonia.Controls;
using Avalonia.Media;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.UI.Controls;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class LivePlaySectionGraphUiPerfSmokeTests
{
    [Fact]
    public Task UnjudgedSectionsDrawWithTheirOwnPaletteColours() => HeadlessSessionFixture.Dispatch(() =>
    {
        var state = new ProjectState();
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        arrangement.EventPoints.Add(new EventPoint { Section = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Section = "chorus" });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), Fret = 3 });
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = level;
        for (var i = 0; i < 4; i++) state.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

        var logger = new FakeAppLogger();
        var profiles = new PracticeProfileService(logger,
            Path.Combine(Path.GetTempPath(), "section-graph-" + Guid.NewGuid().ToString("N"), "profile.json"));
        var vm = LivePlayTestFactory.Create(new FakeAudioPlaybackService { InputSampleRate = 48_000 },
            state, logger, new FakeConfigurationService(), profiles);
        vm.IsActive = true;
        var graph = new LivePlaySectionGraph { LivePlay = vm };
        var window = new Window { Width = 360, Height = 60, Content = graph };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(graph.Bounds is { Width: > 0, Height: > 0 });
            Assert.Equal(["verse", "chorus"], vm.Sections.Select(section => section.Name));

            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            {
                graph.Render(context);
            }

            var fills = drawing.Children.OfType<GeometryDrawing>()
                .Where(rect => rect.Geometry!.Bounds.Y == graph.Bounds.Height - 6)
                .Select(rect => ((ISolidColorBrush)rect.Brush!).Color).ToArray();
            Assert.Equal(2, fills.Length);
            Assert.NotEqual(fills[0], fills[1]);
            Assert.Equal(SongMapPalette.FillFor("verse").Color, fills[0]);
            Assert.Equal(SongMapPalette.FillFor("chorus").Color, fills[1]);
        }
        finally
        {
            vm.IsActive = false;
            window.Content = null;
            window.Close();
        }
    }, CancellationToken.None);
}
