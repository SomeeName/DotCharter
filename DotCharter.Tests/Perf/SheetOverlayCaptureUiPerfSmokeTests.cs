using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.Controls.Sheet;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The sheet strip with a playhead cursor, a selection and a hover on it, in the strip
///     and in page layout, rendered for real: AlphaTab lays the score out, the chunk canvas
///     blits it and the three overlays paint over it. The before/after evidence for giving
///     the overlays one shared measure (#655): a capture that is pixel-identical on both
///     sides of a refactor changed nothing the user sees.
///     <para>
///         The structural half always runs — the score renders, every overlay measures to
///         the canvas's size, and the hover lands on a note. The PNGs (and a SHA-256 of each,
///         in <c>measurements.log</c>) are written only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>
///         (Skia drawing), to <c>DOTCHARTER_RIG_CAPTURE_DIR</c> when set, else
///         <c>TestResults/sheet-overlays/</c>. The rebuild is awaited directly instead of
///         through the 250 ms debounce, so no timer decides what is on the canvas.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SheetOverlayCaptureUiPerfSmokeTests
{
    private const double Scale = 2;

    [Theory]
    [InlineData(false, 1000, 220)]
    [InlineData(true, 1000, 600)]
    public async Task TheSheet_WithCursorSelectionAndHover_RendersAndIsCaptured(bool pageMode, double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var (state, level) = Fixture();
            var selection = new SelectionService();
            selection.SelectedNotes.Add(level.Notes[6]);
            selection.SelectedNotes.Add(level.Notes[13]);

            var view = new SheetMusicView { PageMode = pageMode, Selection = selection, ProjectState = state };
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                await Rebuild(view);
                state.CurrentTimeMs = 2250;
                NarrowWidthLayout.Settle(window);

                var canvas = Single<SheetCanvas>(view);
                Assert.True(canvas.ContentWidth > 0, "the sheet did not render — the capture would be blank");
                foreach (var overlay in new Control[] { Single<CursorOverlay>(view), Single<SelectionOverlay>(view), Single<HoverOverlay>(view) })
                {
                    Assert.True(Math.Abs(overlay.Bounds.Width - canvas.Bounds.Width) < 0.5 &&
                                Math.Abs(overlay.Bounds.Height - canvas.Bounds.Height) < 0.5,
                        $"{overlay.GetType().Name} is {overlay.Bounds.Size}, the canvas it covers is {canvas.Bounds.Size}");
                }

                var hovered = HoverANote(window, view, canvas);
                NarrowWidthLayout.Settle(window);
                Save(view, $"sheet-{(pageMode ? "page" : "strip")}", hovered);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>Eight bars of 4/4 at 120 BPM, a note on every beat walking the strings and
    /// frets, so the cursor, the selection bands and the hover box each have glyphs under them.</summary>
    private static (ProjectState State, ChartLevel Level) Fixture()
    {
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = i % 6, Fret = 3 + (i % 5) });
        }

        var state = new ProjectState { ActiveProject = project, ActiveArrangement = arrangement };
        return (state, level);
    }

    /// <summary>Runs the view's rebuild to completion. Private by design (a debounce owns it
    /// in the app), so the attach's pending debounce is stopped first: left running, it fires
    /// during the AlphaTab await and cancels this render for its own.</summary>
    private static async Task Rebuild(SheetMusicView view)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var debounce = typeof(SheetMusicView).GetField("_debounce", flags)?.GetValue(view) as DispatcherTimer
                       ?? throw new Xunit.Sdk.XunitException("SheetMusicView._debounce is gone — repoint this harness.");
        var rebuild = typeof(SheetMusicView).GetMethod("RebuildAsync", flags)
                      ?? throw new Xunit.Sdk.XunitException("SheetMusicView.RebuildAsync is gone — repoint this harness.");
        debounce.Stop();
        await (Task)rebuild.Invoke(view, null)!;
        Assert.False(debounce.IsEnabled, "a rebuild was scheduled while the capture's own was in flight");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Moves the pointer over the canvas in a fixed scan order until the hover overlay
    /// holds a band, and returns that point (the same one on either side of a refactor).</summary>
    private static Point HoverANote(Window window, SheetMusicView view, SheetCanvas canvas)
    {
        var hover = Single<HoverOverlay>(view);
        var band = typeof(HoverOverlay).GetField("_band", BindingFlags.Instance | BindingFlags.NonPublic)
                   ?? throw new Xunit.Sdk.XunitException("HoverOverlay._band is gone — repoint this harness.");
        for (var fy = 0.55; fy <= 0.95; fy += 0.05)
        {
            for (var fx = 0.3; fx <= 0.7; fx += 0.01)
            {
                var local = new Point(canvas.Bounds.Width * fx, canvas.Bounds.Height * fy);
                if (canvas.TranslatePoint(local, window) is not { } point) continue;
                window.MouseMove(point);
                Dispatcher.UIThread.RunJobs();
                if (band.GetValue(hover) is not null) return point;
            }
        }

        throw new Xunit.Sdk.XunitException("no point over the canvas hovered a note — the hover overlay stayed empty");
    }

    private static T Single<T>(Visual root) where T : Visual => Assert.Single(root.GetVisualDescendants().OfType<T>());

    private static void Save(Control view, string name, Point hovered)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        var size = new PixelSize((int)Math.Ceiling(view.Bounds.Width * Scale), (int)Math.Ceiling(view.Bounds.Height * Scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * Scale, 96 * Scale));
        bitmap.Render(view);
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "TestResults", "sheet-overlays");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        bitmap.Save(path, new PngBitmapEncoderOptions());

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        File.WriteAllText(Path.Combine(dir, name + ".sha256"), hash);
        NarrowWidthLayout.Report($"sheet-overlays {name}: {size.Width}x{size.Height} hover@{hovered} png sha256 {hash}");
    }
}
