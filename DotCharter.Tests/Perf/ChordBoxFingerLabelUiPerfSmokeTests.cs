using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Draws the Inspector's chord box with a finger on every one of the six
///     strings and checks that each finger number reads on its pip (#453).
///     <para>
///         The label used to be near-black, which disappears on the red, blue,
///         orange and purple pips. It is plain white now. The pure half, the label
///         brush, is in the gate as <c>UI/Controls/ChordBoxFingerLabelTests</c>.
///     </para>
///     <para>
///         Pixels can only be read when the session draws for real, i.e. with
///         <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>; then each pip is sampled for white
///         label pixels and the box is written to
///         <c>TestResults/chord-box-finger-labels/</c>. Without it the case
///         asserts only that the box lays out and renders without throwing.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChordBoxFingerLabelUiPerfSmokeTests
{
    private const double BoxWidth = 300;
    private const double BoxHeight = 200;

    /// <summary>String → (fret, finger). Finger 0 is the thumb, drawn "T".</summary>
    private static readonly (int String, int Fret, int Finger)[] Shape =
    [
        (0, 1, 1), (1, 2, 2), (2, 3, 3), (3, 4, 4), (4, 5, 0), (5, 3, 3)
    ];

    private static string ArtifactDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestResults", "chord-box-finger-labels");

    [Fact]
    public Task EveryFingerLabel_ReadsOnItsStringColour() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var chord = new Chord();
            var template = new ChordTemplate();
            foreach (var (s, fret, finger) in Shape)
            {
                chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
                template.Frets[s] = fret;
                template.Fingers[s] = finger;
            }

            var box = new ChordBoxControl
            {
                StringCount = 6,
                Chord = chord,
                Template = template,
                Width = BoxWidth,
                Height = BoxHeight
            };
            var window = new Window
            {
                Width = BoxWidth,
                Height = BoxHeight,
                Background = Brushes.Black,
                Content = box
            };

            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(box.Bounds is { Width: > 1, Height: > 1 },
                    $"the chord box laid out as {box.Bounds} — it rendered nothing");

                if (!UiSweepRunner.CaptureEnabled) return;

                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Directory.CreateDirectory(ArtifactDirectory);
                frame!.Save(Path.Combine(ArtifactDirectory, "chord-box.png"), new PngBitmapEncoderOptions());

                var (minFret, maxFret) = ChordBoxGeometry.DisplayFrets(Shape.Select(n => n.Fret), 0);
                var g = new ChordBoxGeometry(box.Bounds.Width, box.Bounds.Height, 6, minFret, maxFret);
                foreach (var (s, fret, finger) in Shape)
                {
                    var centre = new Point(g.FretCenterX(fret), g.StringY(s));
                    var white = CountNearWhitePixels(frame, centre, 6);
                    Assert.True(white >= 4,
                        $"string {s} ({ChartTheme.BaseStringColors[s]}): finger {finger} drew {white} white " +
                        "pixels on its pip — the label is not white, so it does not read on that string colour");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>Pixels with every channel ≥ 225 in the square of half-width
    /// <paramref name="radius" /> around <paramref name="centre" />.</summary>
    private static int CountNearWhitePixels(Bitmap frame, Point centre, int radius)
    {
        var x0 = (int)Math.Round(centre.X) - radius;
        var y0 = (int)Math.Round(centre.Y) - radius;
        var side = 2 * radius + 1;
        var pixels = new byte[side * side * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels,
            System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(x0, y0, side, side), handle.AddrOfPinnedObject(), pixels.Length,
                side * 4);
        }
        finally
        {
            handle.Free();
        }

        var count = 0;
        for (var i = 0; i < side * side; i++)
        {
            if (pixels[i * 4] >= 225 && pixels[i * 4 + 1] >= 225 && pixels[i * 4 + 2] >= 225)
                count++;
        }

        return count;
    }
}
