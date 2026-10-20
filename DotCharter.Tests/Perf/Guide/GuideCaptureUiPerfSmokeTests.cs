using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tests.Guide;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls;
using SkiaSharp;

namespace DotCharter.Tests.Perf.Guide;

/// <summary>
///     Regenerates the usage guide's screenshots (#60): for every shot in
///     <c>docs/guide/manifests/*.json</c> it builds the scene (<see cref="GuideStages" />), finds
///     each callout's control by <c>x:Name</c> (or a menu row by its header key), draws the
///     numbered markers (<see cref="GuideAnnotator" />) and writes <c>docs/guide/images/&lt;id&gt;.png</c>;
///     then it rewrites each page's numbered lists from the same manifest.
///     <para>
///         OPT-IN, never part of a gate run: it does nothing unless <see cref="EnableVar" /> is 1
///         AND the session draws with Skia (<c>DOTCHARTER_UI_SWEEP_DUMP=1</c>, set before the
///         session starts). <c>DOTCHARTER_GUIDE_ONLY</c> limits it to shot ids starting with that
///         text; <c>DOTCHARTER_GUIDE_OUT</c> writes somewhere other than the repo's
///         <c>docs/guide</c>. How to run it: <c>docs/guide/README.md</c>.
///     </para>
///     <para>
///         A control a manifest names that is not on screen FAILS the run: an arrow is never
///         dropped quietly. Beside the images, <c>TestResults/guide/&lt;id&gt;.names.txt</c> lists
///         every visible named control with its box, which is how a new callout is picked.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class GuideCaptureUiPerfSmokeTests(ITestOutputHelper output)
{
    public const string EnableVar = "DOTCHARTER_GUIDE_CAPTURE";

    private const double CropMargin = 64;
    private const double EdgeSnap = 96;

    private static bool Enabled => Environment.GetEnvironmentVariable(EnableVar) == "1" && UiSweepRunner.CaptureEnabled;

    private static string OutRoot =>
        Environment.GetEnvironmentVariable("DOTCHARTER_GUIDE_OUT") is { Length: > 0 } dir ? dir : GuideManifest.GuideRoot;

    private static string NotesRoot => Path.Combine(AppContext.BaseDirectory, "TestResults", "guide");

    [Fact]
    public async Task TheGuide_IsCaptured_WithEveryCalloutOnItsControl()
    {
        Assert.SkipUnless(Enabled, $"opt-in: set {EnableVar}=1 and {UiSweepRunner.CaptureEnvVar}=1 (docs/guide/README.md)");
        var only = Environment.GetEnvironmentVariable("DOTCHARTER_GUIDE_ONLY") ?? "";
        var pages = GuideManifest.LoadAll();
        Directory.CreateDirectory(Path.Combine(OutRoot, "images"));
        Directory.CreateDirectory(NotesRoot);
        var failures = new List<string>();
        File.Delete(Path.Combine(NotesRoot, "failures.txt"));

        await HeadlessSessionFixture.Dispatch(async () =>
        {
            using var stages = new GuideStages();
            foreach (var shot in pages.SelectMany(p => p.Shots).Where(s => s.Id.StartsWith(only, StringComparison.Ordinal)))
            {
                try
                {
                    var (window, undo) = await stages.ShowAsync(shot);
                    try
                    {
                        Capture(window, shot, stages.FocusMs);
                    }
                    finally
                    {
                        undo();
                    }
                }
                catch (Exception e) when (e is not Xunit.Sdk.SkipException)
                {
                    failures.Add($"{shot.Id}: {e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}");
                    File.WriteAllLines(Path.Combine(NotesRoot, "failures.txt"), failures);
                }
            }
        }, CancellationToken.None);

        foreach (var page in pages)
        {
            var path = Path.Combine(OutRoot, page.Page);
            if (File.Exists(path)) File.WriteAllText(path, GuideManifest.Rewrite(File.ReadAllText(path), page));
        }

        File.WriteAllLines(Path.Combine(NotesRoot, "failures.txt"), failures);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private void Capture(Window window, GuideShot shot, double focusMs)
    {
        var roots = Roots(window);
        File.WriteAllLines(Path.Combine(NotesRoot, shot.Id + ".names.txt"), Inventory(window, roots));
        using (var raw = window.CaptureRenderedFrame()) raw?.Save(Path.Combine(NotesRoot, shot.Id + ".raw.png"), new PngBitmapEncoderOptions());

        var targets = new List<GuideAnnotator.Target>();
        var missing = new List<string>();
        for (var i = 0; i < shot.Callouts.Count; i++)
        {
            var callout = shot.Callouts[i];
            var control = Resolve(window, roots, callout, preferMenuRows: shot.Arg("menu").Length > 0);
            if (control is null)
            {
                missing.Add(callout.Name ?? callout.Loc!);
                continue;
            }

            targets.Add(new GuideAnnotator.Target(i + 1, ToSk(BoxIn(window, control).Intersect(new Rect(window.ClientSize))), callout.Side));
        }

        Assert.True(missing.Count == 0, $"{shot.Id}: not on screen — {string.Join(", ", missing)} (see TestResults/guide/{shot.Id}.names.txt)");

        var frame = Frame(window, roots, out var scale);
        frame = PaintGpuPanes(window, frame, scale, focusMs);
        var text = TextRuns(window, roots).Select(r => ToSk(r)).ToList();
        var crop = CropBox(window, roots, shot, targets);
        var scaled = targets.Select(t => t with { Box = Scale(t.Box, scale) }).ToList();
        var png = GuideAnnotator.Annotate(frame, scaled, [.. text.Select(r => Scale(r, scale))], Pixels(crop, scale, frame));
        File.WriteAllBytes(Path.Combine(OutRoot, "images", shot.Image), png);
        output.WriteLine($"{shot.Image}: {targets.Count} callouts, crop {crop}, {png.Length / 1024} KiB");
    }

    // ── finding things ───────────────────────────────────────────────────────

    /// <summary>The window and every open popup's root (an overlay popup is already inside the window).</summary>
    private static List<Visual> Roots(Window window)
    {
        var roots = new List<Visual> { window };
        for (var i = 0; i < roots.Count; i++)
        {
            foreach (var popup in roots[i].GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen))
            {
                if (popup.Child is { } child && TopLevel.GetTopLevel(child) is { } top && !roots.Contains(top)) roots.Add(top);
            }
        }

        return roots;
    }

    private static IEnumerable<Control> Visible(IEnumerable<Visual> roots) =>
        roots.SelectMany(r => r.GetVisualDescendants().Prepend(r)).OfType<Control>().Distinct()
            .Where(c => c.IsEffectivelyVisible && c.Bounds is { Width: > 1, Height: > 1 });

    private static Control? Resolve(Window window, List<Visual> roots, GuideCallout callout, bool preferMenuRows)
    {
        var screen = new Rect(window.ClientSize);
        var visible = Visible(roots).Where(c => OnScreen(BoxIn(window, c), screen)).ToList();
        if (callout.Name is { } name) return visible.FirstOrDefault(c => c.Name == name);
        // A key names a menu row's header, a control's tooltip or a label's text. The same words
        // can be all three ("Settings"), so an open menu's rows win in a menu shot, tooltips elsewhere.
        var text = LocalizationService.Instance[callout.Loc!];
        var row = visible.OfType<MenuItem>().LastOrDefault(m => Equals(m.Header, text));
        var tipped = visible.LastOrDefault(c => ToolTip.GetTip(c) is string tip && tip == text);
        var label = visible.OfType<TextBlock>().FirstOrDefault(t => t.Text == text);
        // A label inside a button (a pause-menu tile) stands for the button.
        return (preferMenuRows ? row ?? tipped : tipped ?? row) ?? (Control?)label?.FindAncestorOfType<Button>() ?? label;
    }

    /// <summary>At least half of the box, or a 120 px tall strip of a long list, is inside the window:
    /// a row scrolled out of view is not a target.</summary>
    private static bool OnScreen(Rect box, Rect screen)
    {
        var inside = box.Intersect(screen);
        return inside.Width * inside.Height >= box.Width * box.Height / 2 || (inside.Height >= 120 && inside.Width >= box.Width / 2);
    }

    private static Rect BoxIn(Window window, Visual control)
    {
        var size = control.Bounds.Size;
        if (control.TranslatePoint(default, window) is { } p) return new Rect(p, size);
        var screen = control.PointToScreen(default);
        return new Rect(window.PointToClient(screen), size);
    }

    /// <summary>What a marker should not cover: every visible text run, and every small control a
    /// reader would look for (an icon button, a picker, a toggle).</summary>
    private static IEnumerable<Rect> TextRuns(Window window, List<Visual> roots) =>
        Visible(roots)
            .Where(c => c is TextBlock { Text.Length: > 0 } or TextPresenter
                        || (c is Button or ToggleButton or ComboBox or TextBox or PathIcon && c.Bounds.Width * c.Bounds.Height < 12_000))
            .Select(c => BoxIn(window, c));

    private static Rect CropBox(Window window, List<Visual> roots, GuideShot shot, List<GuideAnnotator.Target> targets)
    {
        var whole = new Rect(window.ClientSize);
        if (shot.Crop is not { Count: > 0 }) return whole;
        var boxes = targets.Select(t => new Rect(t.Box.Left, t.Box.Top, t.Box.Width, t.Box.Height)).ToList();
        foreach (var name in shot.Crop)
        {
            var control = Visible(roots).FirstOrDefault(c => c.Name == name)
                          ?? throw new Xunit.Sdk.XunitException($"{shot.Id}: crop target '{name}' is not on screen");
            boxes.Add(BoxIn(window, control));
        }

        foreach (var popup in roots.Skip(1).OfType<Control>()) boxes.Add(BoxIn(window, popup));
        foreach (var popup in window.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen && p.Child is not null))
            boxes.Add(BoxIn(window, popup.Child!));
        var union = boxes.Aggregate((a, b) => a.Union(b)).Inflate(CropMargin).Intersect(whole);
        // A cut that would leave a sliver of the window beside it takes the window edge instead.
        var left = union.X < EdgeSnap ? 0 : union.X;
        var top = union.Y < EdgeSnap ? 0 : union.Y;
        var right = whole.Right - union.Right < EdgeSnap ? whole.Right : union.Right;
        var bottom = whole.Bottom - union.Bottom < EdgeSnap ? whole.Bottom : union.Bottom;
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Every visible named control and its box, for whoever writes the next manifest entry.</summary>
    private static IEnumerable<string> Inventory(Window window, List<Visual> roots)
    {
        foreach (var control in Visible(roots).Where(c => c.Name is { Length: > 0 } name && !name.StartsWith("PART_", StringComparison.Ordinal)))
        {
            var box = BoxIn(window, control);
            yield return string.Create(CultureInfo.InvariantCulture,
                $"{control.Name,-40} {control.GetType().Name,-24} {box.X:0},{box.Y:0} {box.Width:0}x{box.Height:0}");
        }

        foreach (var item in Visible(roots).OfType<MenuItem>().Where(m => m.Header is string))
            yield return $"menu row: {item.Header}";
    }

    // ── pixels ───────────────────────────────────────────────────────────────

    /// <summary>The window's frame with every popup root that is its own top level drawn in at its place.</summary>
    private static byte[] Frame(Window window, List<Visual> roots, out double scale)
    {
        using var frame = window.CaptureRenderedFrame()
                          ?? throw new Xunit.Sdk.XunitException($"no frame — {UiSweepRunner.CaptureEnvVar}=1 must be set before the session starts");
        scale = frame.PixelSize.Width / window.ClientSize.Width;
        using var stream = new MemoryStream();
        frame.Save(stream, new PngBitmapEncoderOptions());
        var popups = roots.Skip(1).OfType<TopLevel>().Where(t => t != window).ToList();
        if (popups.Count == 0) return stream.ToArray();

        using var bitmap = SKBitmap.Decode(stream.ToArray());
        using var canvas = new SKCanvas(bitmap);
        foreach (var popup in popups)
        {
            using var part = popup.CaptureRenderedFrame();
            if (part is null) continue;
            using var partStream = new MemoryStream();
            part.Save(partStream, new PngBitmapEncoderOptions());
            using var partBitmap = SKBitmap.Decode(partStream.ToArray());
            var at = window.PointToClient(popup.PointToScreen(default));
            using var partImage = SKImage.FromBitmap(partBitmap);
            canvas.DrawImage(partImage, (float)(at.X * scale), (float)(at.Y * scale), new SKSamplingOptions(SKFilterMode.Nearest), null);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The timeline and the 3D preview, rendered offscreen (<see cref="GuideChartFrames" />) into their boxes.</summary>
    private byte[] PaintGpuPanes(Window window, byte[] png, double scale, double focusMs)
    {
        var visible = Visible([window]).ToList();
        var panes = new List<(Rect Box, byte[] Bgra, int Width, int Height)>();
        // The 3D preview reads the same snapshot as the timeline, so it needs the renderer even
        // when the timeline itself is hidden (Play Mode's gameplay presentation).
        if (window.GetVisualDescendants().OfType<ChartRenderer>().FirstOrDefault(c => c.Name == "MainChartRenderer") is { Editor: not null } timeline)
        {
            var box = BoxIn(window, timeline);
            var (w, h) = ((int)Math.Round(box.Width * scale), (int)Math.Round(box.Height * scale));
            var viewStart = Math.Max(0, focusMs - (ChartEditorConstants.PlayheadX / timeline.PixelsPerMs));
            if (visible.Contains(timeline) && GuideChartFrames.Timeline(timeline, w, h, viewStart, output.WriteLine) is { } bgra)
                panes.Add((box, bgra, w, h));
            if (visible.FirstOrDefault(c => c.Name == "Viewport3D") is { } viewport)
            {
                var box3d = BoxIn(window, viewport);
                var (w3, h3) = ((int)Math.Round(box3d.Width * scale), (int)Math.Round(box3d.Height * scale));
                if (GuideChartFrames.Highway(timeline, w3, h3, focusMs, output.WriteLine) is { } highway) panes.Add((box3d, highway, w3, h3));
            }
        }

        if (panes.Count == 0) return png;
        using var bitmap = SKBitmap.Decode(png);
        using var original = SKImage.FromBitmap(bitmap);
        using var canvas = new SKCanvas(bitmap);
        foreach (var (box, bgra, w, h) in panes)
        {
            using var pane = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(bgra, 0, pane.GetPixels(), Math.Min(bgra.Length, w * h * 4));
            using var image = SKImage.FromBitmap(pane);
            canvas.DrawImage(image, (float)(box.X * scale), (float)(box.Y * scale), new SKSamplingOptions(SKFilterMode.Nearest), null);
        }

        // An open menu is drawn above the panes: put its pixels back over the painted frames.
        foreach (var popup in window.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen && p.Child is not null))
        {
            var box = Scale(ToSk(BoxIn(window, popup.Child!)), scale);
            canvas.DrawImage(original, box, box, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }

        using var composed = SKImage.FromBitmap(bitmap);
        using var encoded = composed.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static SKRect ToSk(Rect r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    private static SKRect Scale(SKRect r, double scale) =>
        SKRect.Create((float)(r.Left * scale), (float)(r.Top * scale), (float)(r.Width * scale), (float)(r.Height * scale));

    private static SKRectI Pixels(Rect crop, double scale, byte[] png)
    {
        using var codec = SKCodec.Create(new MemoryStream(png));
        var size = codec.Info.Size;
        var left = Math.Clamp((int)Math.Floor(crop.X * scale), 0, size.Width - 1);
        var top = Math.Clamp((int)Math.Floor(crop.Y * scale), 0, size.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(crop.Right * scale), left + 1, size.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(crop.Bottom * scale), top + 1, size.Height);
        return new SKRectI(left, top, right, bottom);
    }
}
