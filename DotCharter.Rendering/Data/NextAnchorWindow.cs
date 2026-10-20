using DotCharter.Rendering.Data.Render;

namespace DotCharter.Rendering.Data;

/// <summary>
///     Result of the upcoming-anchor lookup (0.14.2 B — gameplay-readability
///     aids). <see cref="FadeFraction" /> ramps 0 → 1 as the change
///     approaches so the preview fades in.
/// </summary>
public readonly struct AnchorPreviewInfo
{
    public bool HasUpcoming { get; init; }
    public double AnchorTimeMs { get; init; }
    public byte Fret { get; init; }
    public byte Width { get; init; }

    /// <summary>0 when the change is a full lead window away, 1 at the
    /// change itself. Linear.</summary>
    public float FadeFraction { get; init; }

    public static readonly AnchorPreviewInfo None = default;
}

/// <summary>
///     Pure lookup for the upcoming-anchor preview: the next VISIBLE hand
///     position change (an anchor whose fret/width actually differs from the
///     position playing right before it — back-to-back anchors restating the
///     same position are not a shift and are skipped) within the lead window.
///     No engine/scene deps so it is unit-testable —
///     <c>NextAnchorWindowTests</c>.
/// </summary>
public static class NextAnchorWindow
{
    /// <summary>Fret/width used when no anchor is active yet (mirrors the
    /// camera + fret-number defaults).</summary>
    private const byte DefaultFret = 1;

    private const byte DefaultWidth = 4;

    public static AnchorPreviewInfo Find(RenderAnchor[] anchors, int anchorCount, double timeMs, int leadMs)
    {
        if (leadMs <= 0 || anchorCount <= 0) return AnchorPreviewInfo.None;

        // The position active right now (the last anchor at or before the
        // playhead) — the baseline a "change" is measured against.
        var runningFret = DefaultFret;
        var runningWidth = DefaultWidth;
        for (var i = anchorCount - 1; i >= 0; i--)
        {
            if (anchors[i].Time <= timeMs)
            {
                runningFret = anchors[i].Fret;
                runningWidth = anchors[i].Width;
                break;
            }
        }

        var windowEnd = timeMs + leadMs;
        for (var i = 0; i < anchorCount; i++)
        {
            var anchor = anchors[i];
            if (anchor.Time <= timeMs) continue;
            if (anchor.Time > windowEnd) break;

            if (anchor.Fret != runningFret || anchor.Width != runningWidth)
            {
                return new AnchorPreviewInfo
                {
                    HasUpcoming = true,
                    AnchorTimeMs = anchor.Time,
                    Fret = anchor.Fret,
                    Width = anchor.Width,
                    FadeFraction = (float)Math.Clamp(1.0 - (anchor.Time - timeMs) / leadMs, 0.0, 1.0)
                };
            }

            // Same position restated — not a shift; keep scanning from it.
            runningFret = anchor.Fret;
            runningWidth = anchor.Width;
        }

        return AnchorPreviewInfo.None;
    }
}
