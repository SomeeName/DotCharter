using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #745, behaviourally: the timeline gutter's per-string tick wears the string's IDENTITY
///     colour. On a 7-string the low B (string 0) is grey and the low E (string 1) red; the
///     gutter used to index its palette by lane (<c>Math.Min(i, Count - 1)</c>), which painted
///     the low B red and the high E grey. Renders the real control and reads the brush of each tick.
///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1 DOTCHARTER_RIG_CAPTURE_DIR=&lt;dir&gt;</c> the frame is
///     written to <c>gutter-7-string.png</c> (and the 6-string one to <c>gutter-6-string.png</c>).
/// </summary>
[Collection("HeadlessUI")]
public sealed class SevenStringGutterColourUiPerfSmokeTests
{
    private const int Width = 160;
    private const int Height = 420;

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task EveryStringTick_WearsItsIdentityColour(int stringCount)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var editor = CreateEditor(stringCount);
            var renderer = new ChartRenderer();
            var gutter = new TimelineGutterControl { Renderer = renderer, Editor = editor };
            var frame = new Border
            {
                Width = Width, Height = Height, Background = new SolidColorBrush(Color.Parse("#0E0B15")), Child = gutter
            };
            var window = new Window { Width = Width, Height = Height, Content = frame };
            window.Show();
            try
            {
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                Assert.True(gutter.Bounds is { Width: > 1, Height: > 1 }, $"the gutter laid out as {gutter.Bounds}");

                SaveCapture(gutter, $"gutter-{stringCount}-string.png");

                // The recorded drawing, not pixels: headless drawing only rasterizes under the
                // capture switch. Each string's tick is a 3 x 18 rectangle centred on its lane.
                var layout = TimelineGutterLayout.For(gutter.Bounds.Height, renderer.TopRibbon, stringCount,
                    renderer.LeftyFlip ^ renderer.InvertStrings, renderer.BottomMargin);
                var group = new DrawingGroup();
                using (var ctx = group.Open()) gutter.Render(ctx);
                var ticks = group.Children.OfType<GeometryDrawing>()
                    .Where(d => d.Geometry?.Bounds is { Width: 3, Height: 18 } && d.Brush is ISolidColorBrush)
                    .ToList();
                Assert.Equal(stringCount, ticks.Count);

                var got = new List<string>();
                var want = new List<string>();
                for (var i = 0; i < stringCount; i++)
                {
                    var centre = layout.LaneCentreFor(i);
                    var tick = Assert.Single(ticks, d => Math.Abs(d.Geometry!.Bounds.Center.Y - centre) < 0.5);
                    got.Add(Hex(((ISolidColorBrush)tick.Brush!).Color));
                    want.Add(Hex(ChartTheme.StringBrushFor(i, stringCount).Color));
                }

                Assert.Equal(want, got);
                // The point of #745 in one line each: the low E is red on every guitar…
                Assert.Equal(Hex(ChartTheme.BaseStringColors[0]), got[stringCount - 6]);
                // …and the high E purple.
                Assert.Equal(Hex(ChartTheme.BaseStringColors[5]), got[stringCount - 1]);
                if (stringCount > 6)
                    Assert.Equal(Hex(ChartTheme.BaseStringColors[StringColorSlots.SeventhStringSlot]), got[stringCount - 7]);
                if (stringCount > 7)
                    Assert.Equal(Hex(ChartTheme.BaseStringColors[StringColorSlots.EighthStringSlot]), got[0]);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void SaveCapture(Control gutter, string name)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        var size = new PixelSize((int)gutter.Bounds.Width, (int)gutter.Bounds.Height);
        using var target = new RenderTargetBitmap(size);
        using (var ctx = target.CreateDrawingContext(true))
        {
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#0E0B15")), new Rect(0, 0, size.Width, size.Height));
            gutter.Render(ctx);
        }

        target.Save(Path.Combine(dir, name), new PngBitmapEncoderOptions());
    }

    private static string[] TuningFor(int stringCount) => stringCount switch
    {
        7 => ["B", "E", "A", "D", "G", "B", "e"],
        8 => ["F#", "B", "E", "A", "D", "G", "B", "e"],
        _ => ["E", "A", "D", "G", "B", "e"]
    };

    private static ChartEditorViewModel CreateEditor(int stringCount)
    {
        var logger = new FakeAppLogger();
        var state = App.Services?.GetService<ProjectState>() ?? new ProjectState();
        var selection = new SelectionService();
        var config = new FakeConfigurationService();
        var project = new ChartProject();
        var arrangement = new Arrangement
        {
            Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = stringCount,
            Tuning = TuningFor(stringCount)
        };
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var tracks = new TrackContextRegistry(state, selection);
        return new ChartEditorViewModel(state, transport, new HistoryService(logger), selection,
            new TempoService(logger), tracks, FakeLocalization.KeysOnly());
    }
}
