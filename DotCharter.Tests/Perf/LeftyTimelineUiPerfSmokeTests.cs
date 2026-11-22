using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DotCharter.Rendering.Data;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tests.Perf.Guide;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class LeftyTimelineUiPerfSmokeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StringMoveKey_UsesOnlyInvertStrings(bool lefty, bool invert) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, _, _) = CreateEditor(lefty, invert);
            var note = editor.PlacedNotes[0];
            note.StringIndex = 2;
            App.Services!.GetRequiredService<ISelectionService>().SelectedNotes.Add(note);
            var registrar = App.Services!.GetRequiredService<AppKeybindRegistrar>();
            typeof(AppKeybindRegistrar).GetMethod("MoveStringOnScreen", Private)!.Invoke(registrar, [true, false]);
            Assert.Equal(invert ? 3 : 1, note.StringIndex);
        }, CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GutterLabels_UseOnlyInvertStrings(bool lefty, bool invert) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, state, transport) = CreateEditor(lefty, invert);
            var renderer = new ChartRenderer { Editor = editor, Transport = transport };
            var gutter = new TimelineGutterControl { Renderer = renderer, Editor = editor };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*") };
            grid.Children.Add(gutter);
            Grid.SetColumn(renderer, 1);
            grid.Children.Add(renderer);
            WithWindow(grid, 1000, 420, () =>
            {
                Assert.True(renderer.Bounds is { Width: > 1, Height: > 1 });
                Assert.True(gutter.Bounds is { Width: > 1, Height: > 1 });
                var group = new DrawingGroup();
                using (var context = group.Open()) gutter.Render(context);
                var ticks = group.Children.OfType<GeometryDrawing>()
                    .Where(d => d.Geometry?.Bounds is { Width: 3, Height: 18 }).ToList();
                var low = Assert.Single(ticks, d => d.Brush is ISolidColorBrush brush &&
                    brush.Color == ChartTheme.StringBrushFor(0, 6).Color);
                var expected = TimelineGutterLayout.For(gutter.Bounds.Height, renderer.TopRibbon, 6,
                    state.InvertStrings, renderer.BottomMargin);
                Assert.Equal(expected.LaneCentreFor(0), low.Geometry!.Bounds.Center.Y, 3);
            });
        }, CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MinimapNotesAndSelection_UseOnlyInvertStrings(bool lefty, bool invert) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, _, _) = CreateEditor(lefty, invert);
            editor.SelectedNotes.Add(editor.PlacedNotes[0]);
            var minimap = new MinimapOverviewControl { Editor = editor, TotalTimeMs = 8000 };
            WithWindow(minimap, 1000, 120, () =>
            {
                Assert.True(minimap.Bounds is { Width: > 1, Height: > 1 });
                using var target = new RenderTargetBitmap(new PixelSize(1000, 120));
                using (var context = target.CreateDrawingContext(true)) minimap.Render(context);
                var flip = (bool)typeof(MinimapOverviewControl).GetProperty("MinimapFlipStrings", Private)!.GetValue(minimap)!;
                var bodyHeight = minimap.Bounds.Height;
                // Both draw calls receive the same note-body height. Selection
                // takes a full map height and emits a translated drawing group.
                var mapHeight = bodyHeight + MinimapOverviewControl.NoteBody(bodyHeight).Top;
                var group = new DrawingGroup();
                using (var context = group.Open())
                {
                    typeof(MinimapOverviewControl).GetMethod("DrawSelection", Private)!
                        .Invoke(minimap, [context, editor, 1.0, mapHeight, flip]);
                }
                var core = Assert.Single(GeometryDrawings(group), d =>
                    d.Brush is ISolidColorBrush brush && brush.Color == ChartTheme.StringBrushFor(0, 6).Color);
                var expectedY = (invert ? 5.5 : 0.5) * bodyHeight / 6;
                Assert.Equal(expectedY, core.Geometry!.Bounds.Center.Y, 3);

                // Exercise the static note drawing as well as the live selection overlay.
                var notes = new DrawingGroup();
                using (var context = notes.Open())
                {
                    typeof(MinimapOverviewControl).GetMethod("DrawNotes", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [context, editor, 1.0, bodyHeight, flip]);
                }
                var note = Assert.Single(notes.Children.OfType<GeometryDrawing>());
                Assert.Equal(expectedY, note.Geometry!.Bounds.Center.Y, 3);
            });
        }, CancellationToken.None);

    [Fact]
    public async Task HighwaySnapshot_KeepsLeftyWhileChordBoxFollowsTimeline() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (editor, state, _) = CreateEditor(true, false);
            var driver = new Preview3DRenderDriver(() => false);
            typeof(Preview3DRenderDriver).GetField("_pendingEditor", Private)!.SetValue(driver, editor);
            typeof(Preview3DRenderDriver).GetMethod("RunSnapshotUpdate", Private)!.Invoke(driver, null);
            Assert.True((bool)typeof(Preview3DRenderDriver).GetField("_lastSnapshotSuccess", Private)!.GetValue(driver)!);
            var snapshot = (FrameSnapshot)typeof(Preview3DRenderDriver).GetField("_stagingSnapshot", Private)!.GetValue(driver)!;
            Assert.True(snapshot.LeftyFlip);
            Assert.False(snapshot.InvertStrings);
            Assert.False(state.FlipStrings);
            var box = new ChordBoxControl { FlipStrings = editor.FlipStrings };
            WithWindow(box, 400, 200, () =>
            {
                Assert.True(box.Bounds is { Width: > 1, Height: > 1 });
                var geometry = new ChordBoxGeometry(400, 200, 6, 0, 5, box.FlipStrings);
                Assert.True(geometry.StringY(0) < geometry.StringY(5));
                Assert.Equal(0, geometry.HitString(geometry.StringY(0)));
                state.InvertStrings = true;
                Assert.True(editor.FlipStrings);
            });
        }, CancellationToken.None);

    [Fact]
    public async Task CaptureLeftHandedTimeline() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var path = Environment.GetEnvironmentVariable("DOTCHARTER_LEFTY_CAPTURE");
            if (string.IsNullOrWhiteSpace(path)) return;
            var (editor, _, transport) = CreateEditor(true, false);
            for (var s = 1; s < 6; s++)
            {
                editor.PlacedNotes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(s + 1), StringIndex = s, Fret = s + 2,
                    Sustain = new FractionalPosition(1)
                });
            }
            var renderer = new ChartRenderer { Editor = editor, Transport = transport, PixelsPerMs = 0.2 };
            WithWindow(renderer, 1200, 420, () =>
            {
                Assert.True(renderer.Bounds is { Width: > 1, Height: > 1 });
                var errors = new List<string>();
                var pixels = GuideChartFrames.Timeline(renderer, 1200, 420, 0, errors.Add);
                Assert.True(pixels != null, string.Join(Environment.NewLine, errors));
                using var bitmap = new SKBitmap(new SKImageInfo(1200, 420, SKColorType.Bgra8888, SKAlphaType.Premul));
                Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Create(path);
                data.SaveTo(stream);
            });
        }, CancellationToken.None);

    private static (ChartEditorViewModel Editor, ProjectState State, TransportViewModel Transport) CreateEditor(bool lefty, bool invert)
    {
        var state = App.Services!.GetRequiredService<ProjectState>();
        var profile = new PlayerProfile { Name = "Left-handed review", LeftyFlip = lefty };
        state.LeftyFlip = profile.LeftyFlip;
        state.InvertStrings = invert;
        state.TotalAudioLengthMs = 8000;
        var project = new ChartProject();
        for (var i = 0; i < 17; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "e"] };
        arrangement.Levels.Add(new ChartLevel());
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];
        var logger = new FakeAppLogger();
        var config = new FakeConfigurationService();
        var selection = new SelectionService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, new HistoryService(logger), selection,
            new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());
        return (editor, state, transport);
    }

    private static IEnumerable<GeometryDrawing> GeometryDrawings(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) return [geometry];
        return drawing is DrawingGroup group ? group.Children.SelectMany(GeometryDrawings) : [];
    }

    private static void WithWindow(Control content, double width, double height, Action assert)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        try
        {
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            assert();
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }
}
