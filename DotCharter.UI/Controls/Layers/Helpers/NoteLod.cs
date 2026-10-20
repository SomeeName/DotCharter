using DotCharter.Rendering.Data.Render;
using DotCharter.UI.Constants;

namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     Level of detail for note rendering, decided once per frame from the
///     on-screen note density (0.19 B1 — LOD thinning).
/// </summary>
public enum NoteLevelOfDetail
{
    /// <summary>Full gems, icons and labels.</summary>
    Full,

    /// <summary>Full gems and icons, but no note-attached text (fret /
    /// slide / bend labels) — they overlap into mush below ~a glyph width
    /// per note.</summary>
    Compact,

    /// <summary>Sub-pixel-ish density: notes render as small lane-colored
    /// marks and sustains as thin strips (minimap-style), skipping icons,
    /// chord panels and labels entirely.</summary>
    Marks
}

/// <summary>
///     Pure LOD decision, unit-tested in <c>NoteLodTests</c>. "Column" = one
///     x-position on the timeline (a note or a whole chord).
/// </summary>
public static class NoteLod
{
    /// <summary>
    ///     <paramref name="windowSpanMs" /> and <paramref name="noteColumnCount" />
    ///     must both describe the VIEWPORT.
    ///     <para>
    ///         They must NOT describe the snapshot window instead — the view span
    ///         plus both ±5 s culling buffers — which is self-consistent but answers
    ///         the wrong question. At <c>PixelsPerMs = 1.0</c> that window is
    ///         ~11,920 ms of which ~1,920 ms is visible, so 84 % of the LOD input
    ///         would come from chart the user cannot see: a sparse bridge on screen
    ///         followed by a shred section 2 s off-screen would drop the VISIBLE notes
    ///         to Compact or Marks for no reason the user could perceive, and a dense
    ///         passage surrounded by silence would render at Full and mush.
    ///     </para>
    /// </summary>
    public static NoteLevelOfDetail Compute(double windowSpanMs, double pixelsPerMs, int noteColumnCount)
    {
        if (noteColumnCount <= 0 || windowSpanMs <= 0 || pixelsPerMs <= 0) return NoteLevelOfDetail.Full;

        var pxPerColumn = windowSpanMs * pixelsPerMs / noteColumnCount;
        if (pxPerColumn < ChartEditorConstants.RenderMetrics.NoteLodMarksMaxPxPerColumn)
            return NoteLevelOfDetail.Marks;
        if (pxPerColumn < ChartEditorConstants.RenderMetrics.NoteLodCompactMaxPxPerColumn)
            return NoteLevelOfDetail.Compact;
        return NoteLevelOfDetail.Full;
    }

    /// <summary>
    ///     The per-frame decision, WITH hysteresis against the level the previous
    ///     frame drew. Without it a constant-density passage flipped style on
    ///     every scroll step that straddled a threshold: the visible column count
    ///     alternates N / N+1 as notes enter on the right and leave on the left,
    ///     and when <c>width / N</c> and <c>width / (N+1)</c> sit either side of
    ///     a threshold the notes blinked between gems and marks ~13 times a
    /// second. Each threshold is now a band
    ///     (<see cref="ChartEditorConstants.RenderMetrics.NoteLodHysteresis" />
    ///     either side): moving to a finer level needs px/column to clear the
    ///     threshold's upper edge, moving to a coarser one its lower edge.
    ///     Still pure — the caller owns the one field that remembers the level.
    /// </summary>
    public static NoteLevelOfDetail Compute(double windowSpanMs, double pixelsPerMs, int noteColumnCount,
        NoteLevelOfDetail previous)
    {
        if (noteColumnCount <= 0 || windowSpanMs <= 0 || pixelsPerMs <= 0) return NoteLevelOfDetail.Full;

        var pxPerColumn = windowSpanMs * pixelsPerMs / noteColumnCount;
        const double h = ChartEditorConstants.RenderMetrics.NoteLodHysteresis;
        // A threshold the previous level sits BELOW (coarser side) is raised, so
        // leaving it takes a clear move up; one it sits above is lowered.
        var marksMax = ChartEditorConstants.RenderMetrics.NoteLodMarksMaxPxPerColumn *
                       (previous == NoteLevelOfDetail.Marks ? 1 + h : 1 - h);
        var compactMax = ChartEditorConstants.RenderMetrics.NoteLodCompactMaxPxPerColumn *
                         (previous == NoteLevelOfDetail.Full ? 1 - h : 1 + h);
        if (pxPerColumn < marksMax) return NoteLevelOfDetail.Marks;
        if (pxPerColumn < compactMax) return NoteLevelOfDetail.Compact;
        return NoteLevelOfDetail.Full;
    }

    /// <summary>
    ///     Columns whose ONSET falls inside <c>[startMs, endMs]</c>. The snapshot
    ///     arrays are time-ascending, and the 2D timeline's x is linear in time, so
    ///     this is the same set as "x inside [0, Width]" — the visible columns the
    ///     LOD decision is actually about. Allocation-free; runs once per frame over
    ///     a window that is already bounded by the snapshot builder.
    /// </summary>
    public static int CountInWindow(ReadOnlySpan<RenderNote> notes, double startMs, double endMs)
    {
        var count = 0;
        foreach (ref readonly var note in notes)
        {
            if (note.Time > endMs) break; // ascending — nothing later can qualify
            if (note.Time >= startMs) count++;
        }

        return count;
    }

    /// <inheritdoc cref="CountInWindow(System.ReadOnlySpan{DotCharter.Rendering.Data.Render.RenderNote},double,double)" />
    public static int CountInWindow(ReadOnlySpan<RenderChord> chords, double startMs, double endMs)
    {
        var count = 0;
        foreach (ref readonly var chord in chords)
        {
            if (chord.Time > endMs) break;
            if (chord.Time >= startMs) count++;
        }

        return count;
    }
}
