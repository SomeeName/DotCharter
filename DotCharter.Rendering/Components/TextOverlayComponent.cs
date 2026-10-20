using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

public class TextOverlayComponent : IRenderable
{
    // Constant active-anchor fret hue hoisted out of the per-frame path.
    private static readonly Color ActiveFretColor = Color.Parse("#FFAA00");

    // The inlay/marker frets Rocksmith labels along the whole neck (single- and
    // double-dot positions). Rendered dimly full-length so a player always has a
    // positional reference; the active FHP span overrides them with bright labels.
    private static readonly int[] MarkerFrets = [3, 5, 7, 9, 12, 15, 17, 19, 21, 24];

    private static readonly string[] FretStrings;

    static TextOverlayComponent()
    {
        FretStrings = new string[30];
        for (var i = 0; i < 30; i++) FretStrings[i] = i.ToString();
    }

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context)
    {
        RenderFretNumbers(context);
        RenderUpcomingAnchorPreview(context);
    }

    /// <summary>
    ///     0.14.2 B: when the next hand-position change is inside the lead
    ///     window, draw the UPCOMING anchor's fret numbers dimmed at the
    ///     strike line alongside the current ones, fading in as the change
    ///     approaches — the player pre-positions the fretting hand. Frets
    ///     already labelled by the CURRENT anchor are skipped (they would
    ///     just double-draw under the bright numbers).
    /// </summary>
    private void RenderUpcomingAnchorPreview(RenderContext context)
    {
        if (!context.Snapshot.EnableAnchorPreview) return;
        var preview = context.AnchorPreview;
        if (!preview.HasUpcoming) return;

        // Current anchor range = the frets that already carry bright numbers.
        RenderAnchor? currentAnchor = null;
        for (var i = context.Snapshot.AnchorCount - 1; i >= 0; i--)
        {
            if (context.Snapshot.Anchors[i].Time <= context.TimeMs)
            {
                currentAnchor = context.Snapshot.Anchors[i];
                break;
            }
        }

        var (curMin, curMax) = AnchorFretSpan.FretRange(
            currentAnchor?.Fret ?? 1, currentAnchor?.Width ?? AnchorFretSpan.DefaultWidth);

        // ≈35% alpha at full fade-in, scaled by the fade fraction.
        var alpha = (byte)Math.Clamp((int)(0.35f * preview.FadeFraction * 255f), 0, 255);
        if (alpha == 0) return;
        var previewColor = Color.FromArgb(alpha, 255, 170, 0);

        var y = FretboardConstants.ChartboardYPosition + 0.01f;
        var (fMin, fMax) = AnchorFretSpan.FretRange(preview.Fret, preview.Width);

        for (var fret = fMin; fret <= fMax; fret++)
        {
            if (fret >= curMin && fret <= curMax) continue;
            var x = FretboardConstants.GetLinearFretMiddle(fret);
            var transform = Matrix4x4.CreateTranslation(x, y, 0);
            context.TextBatcher.DrawText3D(FretStrings[fret], transform, previewColor, 0.004f);
        }
    }

    private void RenderFretNumbers(RenderContext context)
    {
        RenderAnchor? currentAnchor = null;
        for (var i = context.Snapshot.AnchorCount - 1; i >= 0; i--)
        {
            if (context.Snapshot.Anchors[i].Time <= context.TimeMs)
            {
                currentAnchor = context.Snapshot.Anchors[i];
                break;
            }
        }

        var y = FretboardConstants.ChartboardYPosition + 0.01f;
        var activeColor = ActiveFretColor;

        // AnchorFretSpan: the rail must name exactly the frets the lane fill
        // and the chord box cover. Clamping only the lower bound printed three
        // numbers over a four-fret span on a fret-0 anchor.
        var (fMin, fMax) = AnchorFretSpan.FretRange(
            currentAnchor?.Fret ?? 1, currentAnchor?.Width ?? AnchorFretSpan.DefaultWidth);

        // Persistent dim marker-fret numbers along the WHOLE neck (Rocksmith
        // parity): every inlay-marker fret carries a faint label so there is
        // always a positional reference, while the active FHP span (below) draws
        // its frets bright. Dim = the active hue token at low alpha (the existing
        // dimmed-preview convention), so it follows the renderer's palette rather
        // than inventing a colour. Markers inside the active span are skipped —
        // the bright pass covers them.
        var dimMarker = Color.FromArgb(80, activeColor.R, activeColor.G, activeColor.B);
        foreach (var fret in MarkerFrets)
        {
            if (fret >= fMin && fret <= fMax) continue;
            var mx = FretboardConstants.GetLinearFretMiddle(fret);
            var mTransform = Matrix4x4.CreateTranslation(mx, y, 0);
            context.TextBatcher.DrawText3D(FretStrings[fret], mTransform, dimMarker, 0.005f);
        }

        for (var fret = fMin; fret <= fMax; fret++)
        {
            var x = FretboardConstants.GetLinearFretMiddle(fret);
            var transform = Matrix4x4.CreateTranslation(x, y, 0);
            context.TextBatcher.DrawText3D(FretStrings[fret], transform, activeColor, 0.005f);
        }

        for (var i = 0; i < context.Snapshot.AnchorCount; i++)
        {
            var a = context.Snapshot.Anchors[i];
            if (a.Time <= context.TimeMs || a.Time >= context.TimeMs + context.HorizonMs) continue;

            var z = context.GetZDepthForTime(a.Time, context.TimeMs);
            if (z > context.FadeEndZ) continue;

            var baseColor = Color.FromArgb(128, 0, 255, 255);
            if (z < context.ZForMs(250f))
            {
                var alpha = Math.Max(0,
                    (int)(baseColor.A * (z - context.ZForMs(50f)) /
                          (context.ZForMs(250f) - context.ZForMs(50f))));
                baseColor = Color.FromArgb((byte)alpha, baseColor.R, baseColor.G, baseColor.B);
            }

            var (fMinZ, fMaxZ) = AnchorFretSpan.FretRange(a.Fret, a.Width);

            for (var fret = fMinZ; fret <= fMaxZ; fret++)
            {
                var x = FretboardConstants.GetLinearFretMiddle(fret);
                var transform = Matrix4x4.CreateTranslation(x, y, z);
                context.TextBatcher.DrawText3D(FretStrings[fret], transform, baseColor, 0.003f);
            }
        }
    }
}
