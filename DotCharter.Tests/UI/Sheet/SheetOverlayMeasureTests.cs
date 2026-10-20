using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.UI.Controls.Sheet;

namespace DotCharter.Tests.UI.Sheet;

/// <summary>
///     The sheet's three overlays (cursor, selection, hover) measure through ONE
///     <see cref="SheetOverlayLayer" /> since #655. Each used to carry its own copy of the
///     same page-mode state and <c>MeasureOverride</c>; an overlay that measures a pixel off
///     the canvas it covers draws its cursor, band or hover box beside the glyph it marks.
///     <para>
///         <see cref="LegacyOverlay" /> is that copy, verbatim, as the oracle: every overlay
///         walks the same sequence of page-mode, metrics, zoom, hint and available-size
///         changes as it does, on one instance each, so the "a measure without metrics keeps
///         the previous scale" carry-over is compared too. Bare layout primitives, detached,
///         no window — platform-free like <c>ChunkedPanelTests</c>.
///     </para>
/// </summary>
public sealed class SheetOverlayMeasureTests
{
    public static TheoryData<string> Overlays => ["cursor", "selection", "hover"];

    private static SheetOverlayLayer Create(string name) => name switch
    {
        "cursor" => new CursorOverlay(),
        "selection" => new SelectionOverlay(),
        "hover" => new HoverOverlay(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(Overlays))]
    public void EveryOverlay_MeasuresAsTheCopyItReplaced_InPageAndStripMode(string name)
    {
        var overlay = Create(name);
        var legacy = new LegacyOverlay();
        var steps = 0;

        foreach (var (pageMode, pageScale, (w, h), zoom, hint, available) in Steps())
        {
            overlay.SetPageMode(pageMode, pageScale);
            legacy.SetPageMode(pageMode, pageScale);
            overlay.SetMetrics(w, h);
            legacy.SetMetrics(w, h);
            overlay.ZoomFactor = legacy.ZoomFactor = zoom;
            overlay.ViewportHeightHint = legacy.ViewportHeightHint = hint;

            overlay.Measure(available);
            legacy.Measure(available);

            var at = $"{name}: page={pageMode} pageScale={pageScale} source={w}x{h} zoom={zoom} hint={hint} available={available}";
            Assert.True(overlay.DesiredSize == legacy.DesiredSize,
                $"{at}: measured {overlay.DesiredSize}, the copy it replaced measured {legacy.DesiredSize}");
            Assert.True(overlay.Scale.Equals(legacy.Scale), $"{at}: scale {overlay.Scale}, the copy's {legacy.Scale}");
            steps++;
        }

        Assert.Equal(2 * 3 * 5 * 3 * 2 * 4, steps);
    }

    /// <summary>Every combination, in a fixed order: page mode on and off, no / a small / a
    /// large page scale, a real source and each kind of empty one, a zoom under the 0.1
    /// floor, 1 and above 1, no hint and a hint, a bounded, an unbounded and a zero height.</summary>
    private static IEnumerable<(bool, double, (double, double), double, double, Size)> Steps() =>
        from pageMode in new[] { true, false }
        from pageScale in new[] { 0.0, 0.5, 1.25 }
        from source in new[] { (1200.0, 180.0), (0.0, 0.0), (1200.0, 0.0), (0.0, 180.0), (640.0, 96.0) }
        from zoom in new[] { 0.05, 1.0, 1.75 }
        from hint in new[] { 0.0, 160.0 }
        from available in new[]
        {
            new Size(800, 200), new Size(800, double.PositiveInfinity),
            new Size(double.PositiveInfinity, double.PositiveInfinity), new Size(800, 0)
        }
        select (pageMode, pageScale, source, zoom, hint, available);

    /// <summary>Page mode with a display scale: the source times that scale, and the
    /// scale is what <c>Render</c> maps source pixels with. Measured unbounded, as the
    /// page's vertical scroller measures it (a bounded measure clamps the desired size).</summary>
    [Theory]
    [MemberData(nameof(Overlays))]
    public void PageMode_IsTheSourceTimesThePageScale(string name)
    {
        var overlay = Create(name);
        overlay.SetPageMode(true, 0.8);
        overlay.SetMetrics(1000, 200);

        overlay.Measure(Size.Infinity);

        Assert.Equal(new Size(800, 160), overlay.DesiredSize);
        Assert.Equal(0.8, overlay.Scale, 12);
    }

    /// <summary>The strip fits the strip's height times the zoom, and the width follows
    /// the source at that scale. Zoomed in, the strip scrolls vertically, so the height
    /// arrives unbounded and the view's hint stands in for it.</summary>
    [Theory]
    [MemberData(nameof(Overlays))]
    public void StripMode_FitsTheAvailableHeightTimesTheZoom(string name)
    {
        var overlay = Create(name);
        overlay.SetPageMode(false, 0);
        overlay.SetMetrics(1000, 200);
        overlay.ZoomFactor = 1.5;
        overlay.ViewportHeightHint = 100;

        overlay.Measure(Size.Infinity);

        Assert.Equal(new Size(750, 150), overlay.DesiredSize);
        Assert.Equal(0.75, overlay.Scale, 12);
    }

    [Theory]
    [InlineData(Key.OemPlus, true, 1)]
    [InlineData(Key.Add, true, 1)]
    [InlineData(Key.OemMinus, true, -1)]
    [InlineData(Key.Subtract, true, -1)]
    [InlineData(Key.D5, false, 0)]
    public void TheDurationKeys_StepTheNoteValue(Key key, bool handled, int step)
    {
        Assert.Equal(handled, SheetMusicViewInput.TryGetDurationStep(key, out var actual));
        Assert.Equal(step, actual);
    }

    [Theory]
    [InlineData(Key.D0, 0)]
    [InlineData(Key.D9, 9)]
    [InlineData(Key.NumPad0, 0)]
    [InlineData(Key.NumPad7, 7)]
    [InlineData(Key.A, -1)]
    [InlineData(Key.OemPlus, -1)]
    public void TheDigitKeys_TypeAFret(Key key, int digit)
    {
        Assert.Equal(digit >= 0, SheetMusicViewInput.TryGetDigit(key, out var actual));
        Assert.Equal(digit, actual);
    }

    /// <summary>The overlays' page-mode state and measure as each of the three carried it
    /// before #655, copied verbatim from <c>CursorOverlay</c>.</summary>
    private sealed class LegacyOverlay : Control
    {
        private double _sourceHeight;
        private double _scale = 1.0;
        private bool _pageMode;
        private double _pageScale;

        public double SourceWidth { get; private set; }
        public double ZoomFactor { get; set; } = 1.0;
        public double ViewportHeightHint { get; set; }
        public double Scale => _scale;

        public void SetPageMode(bool pageMode, double pageScale)
        {
            _pageMode = pageMode;
            _pageScale = pageScale;
            InvalidateMeasure();
            InvalidateVisual();
        }

        public void SetMetrics(double sourceWidth, double sourceHeight)
        {
            SourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_pageMode && SourceWidth > 0 && _sourceHeight > 0)
            {
                _scale = _pageScale > 0 ? _pageScale : 1.0;
                return new Size(SourceWidth * _scale, _sourceHeight * _scale);
            }

            var parentHeight = availableSize.Height;
            if (double.IsInfinity(parentHeight) || parentHeight <= 0)
                parentHeight = ViewportHeightHint > 0 ? ViewportHeightHint : _sourceHeight;
            var targetHeight = parentHeight * Math.Max(ZoomFactor, 0.1);
            if (SourceWidth <= 0 || _sourceHeight <= 0)
                return new Size(0, targetHeight);
            var scale = targetHeight / _sourceHeight;
            if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
            _scale = scale;
            return new Size(SourceWidth * scale, targetHeight);
        }
    }
}
