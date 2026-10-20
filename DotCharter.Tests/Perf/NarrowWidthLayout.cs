using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Shared by the narrow-width layout cases (#130, #134): the UI sweep's seeded song, a settle
///     loop, edge geometry in a common ancestor's space, and the optional PNG capture.
/// </summary>
internal static class NarrowWidthLayout
{
    /// <summary>Where <see cref="Capture" /> writes: <c>DOTCHARTER_RIG_CAPTURE_DIR</c> when set,
    /// else <c>TestResults/narrow-width/</c>. Only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.</summary>
    public static string ArtifactDirectory =>
        Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "TestResults", "narrow-width");

    /// <summary>The UI sweep's fixture (<c>UiSweepDataContexts.SeedProjectState</c>): the
    /// 240-sound stress chart as a three-path song, so every readout the shell derives from a
    /// song is populated.</summary>
    public static void SeedSong(ProjectState state)
    {
        var chart = StressChartFactory.Chart(soundCount: 240, lengthMinutes: 2);
        var project = new ChartProject
        {
            Chart = { SongName = "UI sweep fixture", Artist = "DotCharter test harness", AlbumName = "(synthetic)" }
        };
        foreach (var beat in chart.Beats) project.Beats.Add(beat);

        foreach (var (name, type, strings) in new[]
                 {
                     ("Lead", ArrangementType.Lead, 6),
                     ("Rhythm", ArrangementType.Rhythm, 6),
                     ("Bass", ArrangementType.Bass, 4)
                 })
        {
            var arrangement = new Arrangement { Name = name, ArrangementType = type, StringCount = strings };
            var level = new ChartLevel { DifficultyIndex = 0 };
            foreach (var note in chart.Notes.Where(n => n.StringIndex < strings)) level.Notes.Add(note);
            foreach (var chord in chart.Chords.Where(c => c.ChordNotes.All(n => n.StringIndex < strings)))
                level.Chords.Add(chord);
            foreach (var shape in chart.HandShapes) level.HandShapes.Add(shape);
            foreach (var anchor in chart.Anchors) level.Anchors.Add(anchor);
            arrangement.Levels.Add(level);
            foreach (var point in chart.EventPoints) arrangement.EventPoints.Add(point);
            foreach (var tone in chart.Tones) arrangement.ToneChanges.Add(tone);
            project.Arrangements.Add(arrangement);
        }

        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements[0];
    }

    /// <summary>A bare window on the app's own ground, so a capture reads like the shell.</summary>
    public static Window NewWindow(double width, double height, Control content)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        if (Application.Current is { } app
            && app.TryGetResource("AppBackground", app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            window.Background = brush;
        }

        return window;
    }

    public static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The control's horizontal extent in <paramref name="space" />'s coordinates.</summary>
    public static (double Left, double Right) Span(Visual control, Visual space)
    {
        var left = control.TranslatePoint(new Point(0, 0), space)!.Value.X;
        return (left, left + control.Bounds.Width);
    }

    /// <summary>The control's top edge in <paramref name="space" />'s coordinates.</summary>
    public static double TopIn(Visual control, Visual space) => control.TranslatePoint(new Point(0, 0), space)!.Value.Y;

    public static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} named '{name}' under {root.GetType().Name} — repoint this test.");

    /// <summary>A measurement line: to the console (shown for a FAILED case), and with
    /// <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> also to <c>measurements.log</c> beside the captures, so a
    /// green run's numbers can be read too.</summary>
    public static void Report(string line)
    {
        Console.WriteLine(line);
        if (!UiSweepRunner.CaptureEnabled) return;
        Directory.CreateDirectory(ArtifactDirectory);
        File.AppendAllText(Path.Combine(ArtifactDirectory, "measurements.log"),
            $"{HeadlessSessionFixture.SweptCulture} {line}{Environment.NewLine}");
    }

    public static void Capture(Window window, string name)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        Directory.CreateDirectory(ArtifactDirectory);
        using var frame = window.CaptureRenderedFrame();
        File.AppendAllText(Path.Combine(ArtifactDirectory, "capture.log"),
            $"{name}: window {window.Bounds}, frame {(frame is null ? "NONE" : frame.PixelSize.ToString())}{Environment.NewLine}");
        Assert.NotNull(frame);
        frame.Save(Path.Combine(ArtifactDirectory, $"{name}-{HeadlessSessionFixture.SweptCulture}.png"), new PngBitmapEncoderOptions());
    }
}
