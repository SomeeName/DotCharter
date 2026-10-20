using DotCharter.UI.Controls.Sheet;
using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.UI;

/// <summary>
///     The sheet strip's time → measure lookup is a MONOTONIC CURSOR, and it
///     answers exactly what the scan it replaced answered.
///
///     <para>
///         <c>SheetMusicView.UpdateCursor</c> runs on every coalesced playhead
///         publish — up to 60 Hz for the whole of playback while the strip is
///         visible — and the mapping used to be spelled
///         <c>for (idx = 0; idx &lt; spans.Count - 1; idx++)</c> in FOUR places,
///         restarting at bar 1 on every call, over every measure of the exported
///         level. The playhead is monotone during playback, so a cursor costs
///         one step per bar crossed instead.
///     </para>
///     <para>
///         Modelled on <c>TimelineScanEquivalenceTests</c>: the cursor is swept
///         against a verbatim copy of the loop it replaced, at every boundary and
///         between every pair, forwards, backwards and from a deliberately wrong
///         hint — because a cursor that is right only when walked in order is a
///         cursor that breaks on the first seek.
///     </para>
/// </summary>
public sealed class SheetCursorSpanScanTests
{
    /// <summary>Verbatim copy of the scan that was there, so the comparison is
    /// against the real previous behaviour rather than a restatement of the new
    /// one.</summary>
    private static int ScanFromZero(IReadOnlyList<AlphaTexExporter.MeasureSpan> spans, double timeMs)
    {
        var idx = 0;
        for (; idx < spans.Count - 1; idx++)
        {
            if (timeMs < spans[idx].EndMs)
                break;
        }

        return idx;
    }

    private static List<AlphaTexExporter.MeasureSpan> Bars(int count, double barMs = 2000, double startMs = 0)
    {
        var spans = new List<AlphaTexExporter.MeasureSpan>(count);
        for (var i = 0; i < count; i++)
            spans.Add(new AlphaTexExporter.MeasureSpan(startMs + i * barMs, startMs + (i + 1) * barMs));
        return spans;
    }

    /// <summary>Played straight through, the cursor agrees with the scan at
    /// every sampled time — including both sides of every bar line.</summary>
    [Fact]
    public void PlayedForward_TheCursorAgreesWithTheScanEverywhere()
    {
        var spans = Bars(64);
        var hint = 0;
        var checkedTimes = 0;

        for (var t = -500.0; t <= 64 * 2000 + 500; t += 37.0)
        {
            hint = SheetMusicView.SpanIndexAt(spans, t, hint);
            Assert.Equal(ScanFromZero(spans, t), hint);
            checkedTimes++;
        }

        // Not vacuous: the sweep really crossed the whole score.
        Assert.True(checkedTimes > 3000, $"only {checkedTimes} samples");
    }

    /// <summary>Every exact bar boundary, which is where an off-by-one lives.</summary>
    [Fact]
    public void AtEveryBarBoundary_TheCursorAgreesWithTheScan()
    {
        var spans = Bars(64);
        var hint = 0;

        foreach (var span in spans)
        {
            foreach (var t in new[] { span.StartMs - 0.001, span.StartMs, span.EndMs - 0.001, span.EndMs })
            {
                hint = SheetMusicView.SpanIndexAt(spans, t, hint);
                Assert.Equal(ScanFromZero(spans, t), hint);
            }
        }
    }

    /// <summary>
    ///     A SEEK: the cursor is asked for a time far from where it sits, in both
    ///     directions, from every possible stale hint. This is the property the
    ///     scan had for free and a cursor has to earn.
    /// </summary>
    [Fact]
    public void FromAnyStaleHint_TheCursorStillFindsTheScansAnswer()
    {
        var spans = Bars(48);

        for (var hint = -5; hint <= spans.Count + 5; hint++)
        {
            foreach (var t in new[] { -1000.0, 0.0, 1.0, 15_999.0, 16_000.0, 40_000.0, 95_999.0, 96_000.0, 500_000.0 })
                Assert.Equal(ScanFromZero(spans, t), SheetMusicView.SpanIndexAt(spans, t, hint));
        }
    }

    /// <summary>Degenerate scores: none, one, and a pickup that does not start
    /// at zero.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OnAShortScore_TheCursorAgreesWithTheScan(int barCount)
    {
        var spans = Bars(barCount, startMs: 750);

        foreach (var t in new[] { -1.0, 0.0, 749.0, 750.0, 1000.0, 2750.0, 4750.0, 99_999.0 })
        {
            var cursor = SheetMusicView.SpanIndexAt(spans, t, 0);
            if (spans.Count == 0) Assert.Equal(0, cursor);
            else Assert.Equal(ScanFromZero(spans, t), cursor);
        }
    }

    /// <summary>An irregular grid — meter changes make bars of different
    /// lengths, and a cursor that assumed a constant bar would pass every case
    /// above.</summary>
    [Fact]
    public void AcrossAMeterChange_TheCursorAgreesWithTheScan()
    {
        var lengths = new[] { 2000.0, 2000, 1500, 1500, 3000, 500, 500, 4000, 2000 };
        var spans = new List<AlphaTexExporter.MeasureSpan>();
        var at = 0.0;
        foreach (var length in lengths)
        {
            spans.Add(new AlphaTexExporter.MeasureSpan(at, at + length));
            at += length;
        }

        var hint = 0;
        for (var t = -100.0; t <= at + 100; t += 13.0)
        {
            hint = SheetMusicView.SpanIndexAt(spans, t, hint);
            Assert.Equal(ScanFromZero(spans, t), hint);
        }
    }
}
