// ReSharper disable InconsistentNaming -- private static color/brush fields use _camelCase to pair with same-named public PascalCase properties.
using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers;

/// <summary>
///     Draws the playhead (current-time vertical line + time badge) as the very
///     last rendering step so it's always visible above notes, hand shapes,
///     fret anchors and every other world-space element.
/// </summary>
public class PlayheadLayer : ITimelineRenderLayer
{
    // Theme color is read once per frame from ChartTheme.PlayheadTimeBgColor.

    // 0.14.3 B scrub-trail state. Pure per-instance scalars (each
    // ChartRenderer owns its own layer list) — updated only while a scrub
    // pins the continuous render loop, zeroed the moment it ends.
    private double _lastTimeMs;
    private float _trailPx;

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        if (!rc.DrawLabels) return;

        var playheadX = (float)rc.PlayheadScreenX;

        // With the viewport panned away from the playhead (Follow off) the
        // line can sit outside the visible window — skip the whole draw
        // (glow flanks + badge reach ~120 px past the line at most).
        if (playheadX < -120f || playheadX > rc.Width + 120f)
        {
            _lastTimeMs = rc.CurrentTimeMs;
            _trailPx = 0f;
            return;
        }

        var height = (float)rc.Height;
        var col = ChartTheme.PlayheadTimeBgColor;
        var polish = Timeline2DVisuals.PolishEnabled;

        // Soft horizontal glow flanking the playhead line. Gives the playhead
        // a premium, "alive" feel without obscuring underlying notes.
        // 0.14.3 B: during playback the glow widens and brightens (playback
        // frames render continuously anyway, so the state change costs no
        // extra invalidation); paused keeps the subtle resting glow.
        var playbackBoost = polish && rc.IsPlaybackActive;
        var glowAlpha = (byte)Math.Min(playbackBoost ? 104 : 72, col.A);
        var glowColor = Color.FromArgb(glowAlpha, col.R, col.G, col.B);
        var transparent = Color.FromArgb(0, col.R, col.G, col.B);
        var glowHalfWidth = playbackBoost ? 18f : 11f;
        batcher.AddQuadGradient(
            playheadX - glowHalfWidth, 0, playheadX - glowHalfWidth, height,
            playheadX, height, playheadX, 0,
            transparent, transparent, glowColor, glowColor);
        batcher.AddQuadGradient(
            playheadX, 0, playheadX, height,
            playheadX + glowHalfWidth, height, playheadX + glowHalfWidth, 0,
            glowColor, glowColor, transparent, transparent);

        // Scrub trail: while an interactive scrub pins the continuous loop,
        // a short wake stretches toward where the playhead came from, scaled
        // by the smoothed seek velocity. One gradient quad; rc.IsScrubbing
        // is false on the very first frame after the interaction ends, so
        // the frozen frame carries no residue (damage discipline).
        if (polish && rc.IsScrubbing)
        {
            var deltaPx = (float)((rc.CurrentTimeMs - _lastTimeMs) * rc.CurrentZoom);
            // Fast attack, smooth release — the trail follows a flick
            // immediately and eases out over a few frames of slower motion.
            _trailPx = Math.Abs(deltaPx) > Math.Abs(_trailPx)
                ? deltaPx
                : _trailPx * 0.82f + deltaPx * 0.18f;
            var mag = Math.Min(90f, Math.Abs(_trailPx) * 5f);
            if (mag > 4f)
            {
                // Trail extends opposite the seek direction.
                var trailEndX = _trailPx >= 0 ? playheadX - mag : playheadX + mag;
                var trailAlpha = (byte)Math.Min(88, (int)col.A);
                var trailEdge = Color.FromArgb(trailAlpha, col.R, col.G, col.B);
                batcher.AddQuadGradient(
                    playheadX, 0, playheadX, height,
                    trailEndX, height, trailEndX, 0,
                    trailEdge, trailEdge, transparent, transparent);
            }
        }
        else
        {
            _trailPx = 0f;
        }

        _lastTimeMs = rc.CurrentTimeMs;

        // The crisp playhead line itself.
        batcher.AddVerticalLineAa(playheadX, 0, ChartEditorConstants.RenderMetrics.PlayheadThickness, height, col);

        // Rounded time badge auto-sized to the actual text width. Flat chip
        // language (fill + hairline darker-shade border, no shadow, no inner
        // highlight) — matches the event-ribbon chips and measure pills; the
        // old two-stage drop shadow was the last skeuomorphic holdout on the
        // timeline.
        var timeTxt = RenderStringCache.MmSsFf(rc.CurrentTimeMs);
        var textW = batcher.MeasureTextWidth(timeTxt);
        const float padX = 8f;
        const float badgeH = 18f;
        const float radius = 4f;
        var badgeW = textW + padX * 2;
        var badgeX = BadgeX(playheadX, badgeW, rc.Width);
        // Docked into the RULER strip where the eye already reads time — it
        // would cover chart content if it floated at the top of the lane area.
        // It overdraws the static ruler labels right next to
        // the playhead, which is fine: the badge IS the precise version of
        // those labels. See BadgeY for why it is placed from the bottom.
        var badgeY = BadgeY(height, badgeH);
        batcher.AddRoundRect(badgeX, badgeY, badgeW, badgeH, radius, col);
        var border = Color.FromArgb(140, (byte)(col.R * 0.55f), (byte)(col.G * 0.55f), (byte)(col.B * 0.55f));
        batcher.AddRect(badgeX + radius, badgeY, badgeW - radius * 2f, 1f, border);
        batcher.AddRect(badgeX + radius, badgeY + badgeH - 1f, badgeW - radius * 2f, 1f, border);
        batcher.AddRect(badgeX, badgeY + radius, 1f, badgeH - radius * 2f, border);
        batcher.AddRect(badgeX + badgeW - 1f, badgeY + radius, 1f, badgeH - radius * 2f, border);

        batcher.AddText(timeTxt, badgeX + padX, badgeY + badgeH * 0.5f, ChartTheme.WhiteColor, centerY: true);
    }

    /// <summary>Inset kept between the time chip and either edge of the
    /// timeline viewport.</summary>
    public const float BadgeEdgeInset = 2f;

    /// <summary>
    ///     Left edge of the playhead time chip: CENTRED on the line, clamped
    ///     into the viewport.
    ///     <para>
    ///         Centred because the chip is the precise reading of the ruler
    ///         label the playhead is standing on (`Chart Editor.dc.html` draws
    ///         it that way) — hung off one side, as it was through 0.51, it
    ///         reads as the time a moment ahead of the line. Clamped because
    ///         the playhead reaches both edges when the viewport is panned away
    ///         from it, and half a chip is not a time.
    ///     </para>
    ///     <para>
    ///         Pure so it is tested in the DEFAULT gate — the layer itself needs
    ///         a batcher with a live font atlas (<c>PlayheadTimeChipTests</c>).
    ///     </para>
    /// </summary>
    /// <summary>
    ///     The chip's top: centred in the time ruler, which is the bottom
    ///     <see cref="Input.TimelineZones.TimeRulerHeightPx" /> of the viewport. It must not sit a
    ///     fixed 20 px below the lanes, which would put it in the FHP band once the bottom margin grows
    ///     the SHAPES and FHP bands above the ruler — where it would cover FHP fret chips.
    /// </summary>
    public static float BadgeY(double viewportHeight, float badgeH) =>
        (float)(viewportHeight - Input.TimelineZones.TimeRulerHeightPx +
                (Input.TimelineZones.TimeRulerHeightPx - badgeH) * 0.5);

    public static float BadgeX(float playheadX, float badgeW, double viewportWidth)
    {
        var rightMost = Math.Max(BadgeEdgeInset, (float)viewportWidth - badgeW - BadgeEdgeInset);
        return Math.Clamp(playheadX - badgeW * 0.5f, BadgeEdgeInset, rightMost);
    }
}
