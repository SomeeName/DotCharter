// ReSharper disable InconsistentNaming -- private static color/brush fields use _camelCase to pair with same-named public PascalCase properties.
using Avalonia;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.UI.Controls.Layers;

public class EventLayer : ITimelineRenderLayer
{
    private readonly PinnedLabelHitRegistry _pinnedRegistry;

    /// <summary>The viewport whose pixel space this layer's pinned rects are
    /// in. The registry is a DI SINGLETON and there is one EventLayer per
    /// ChartRenderer (main timeline + one per stacked row), so entries have to
    /// be keyed by their owner or the last renderer to draw wipes the rest and
    /// its rects get hit-tested against another control's pointer.</summary>
    private readonly object _owner;

    public EventLayer(PinnedLabelHitRegistry pinnedRegistry, object owner)
    {
        _pinnedRegistry = pinnedRegistry;
        _owner = owner;
    }

    // 0.14.3 B ribbon polish, snapshotted once per frame: the polish toggle
    // and the start time of the section the playhead is currently inside
    // (drives the active-section chip emphasis). Sticky-prefix snapshot
    // entries make the walk below see the active section even when its
    // start scrolled out of the window.
    private bool _polish;
    private double _activeSectionTime = double.NegativeInfinity;

    // This frame's pinned (sticky) chip per row, as an index into the snapshot, or -1.
    // Found BEFORE the in-flow chips draw, so the pinned event's own chip (still partly on
    // screen at the left edge) is not drawn a second time a few pixels from its pinned copy.
    private int _pinTone = -1;
    private int _pinSection = -1;
    private int _pinPhrase = -1;

    // This frame's ribbon: its density decides the chip height and inset,
    // so every chip, hit rect and drop-line start reads it from here.
    private RibbonLayout _ribbon = RibbonLayout.Default;

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        if (!rc.DrawLabels) return;

        _ribbon = rc.Ribbon;
        _polish = Timeline2DVisuals.PolishEnabled;
        _activeSectionTime = FindActiveSectionTime(rc);
        FindPins(rc);

        if (rc.Ribbon.ShowTones) DrawTones(batcher, rc);
        DrawEventsAndPhrases(batcher, rc);
        if (rc.Ribbon.ShowLyrics) DrawLyrics(batcher, rc);
        DrawPinnedLabels(batcher, rc);
        // NOTE: the playhead is drawn by PlayheadLayer at the very end so
        // it renders on top of notes, hand shapes, and fret anchors.
    }

    private static double FindActiveSectionTime(ChartRenderContext rc)
    {
        for (var i = rc.Snapshot.EventPointCount - 1; i >= 0; i--)
        {
            var ep = rc.Snapshot.EventPoints[i];
            if (ep.Time <= rc.CurrentTimeMs && !string.IsNullOrEmpty(ep.Section)) return ep.Time;
        }

        return double.NegativeInfinity;
    }

    /// <summary>
    ///     The latest tone, section and phrase at or before the viewport start: each is shown by its
    ///     pinned chip at the left edge (<see cref="DrawPinnedLabels" />) instead of its in-flow one.
    /// </summary>
    private void FindPins(ChartRenderContext rc)
    {
        _pinTone = _pinSection = _pinPhrase = -1;

        if (rc.Ribbon.ShowTones)
        {
            for (var i = rc.Snapshot.ToneCount - 1; i >= 0; i--)
            {
                if (rc.Snapshot.Tones[i].Time <= rc.ViewStartMs)
                {
                    _pinTone = i;
                    break;
                }
            }
        }

        for (var i = rc.Snapshot.EventPointCount - 1; i >= 0; i--)
        {
            var ep = rc.Snapshot.EventPoints[i];
            if (ep.Time > rc.ViewStartMs) continue;
            if (_pinSection < 0 && rc.Ribbon.ShowSections && !string.IsNullOrEmpty(ep.Section)) _pinSection = i;
            if (_pinPhrase < 0 && rc.Ribbon.ShowPhrases && !string.IsNullOrEmpty(ep.Phrase)) _pinPhrase = i;
            if ((_pinSection >= 0 || !rc.Ribbon.ShowSections) && (_pinPhrase >= 0 || !rc.Ribbon.ShowPhrases)) break;
        }
    }

    private void DrawTones(TimelineBatcher batcher, ChartRenderContext rc)
    {
        for (var i = 0; i < rc.Snapshot.ToneCount; i++)
        {
            var t = rc.Snapshot.Tones[i];
            var x = (float)((t.Time - rc.ViewStartMs) * rc.CurrentZoom);

            if (x < -ChartEditorConstants.RenderMetrics.EventCullBufferPx ||
                x > rc.Width + ChartEditorConstants.RenderMetrics.EventCullBufferPx)
            {
                continue;
            }

            DrawDashedVerticalLine(batcher, x,
                rc.Ribbon.BarBottom(rc.Ribbon.ToneY),
                (float)rc.Height,
                ChartEditorConstants.RenderMetrics.EventLineThickness,
                ToneLineCol);

            if (i == _pinTone) continue; // shown by its pinned chip

            var emph = t.IsSelected || t.IsHovered;
            var bg = emph ? ToneFillHover : ToneFill;
            DrawBadge(batcher, t.ToneName, x, rc.Ribbon.ToneY, bg, OnColorText, emph);
        }
    }

    private void DrawEventsAndPhrases(TimelineBatcher batcher, ChartRenderContext rc)
    {
        for (var i = 0; i < rc.Snapshot.EventPointCount; i++)
        {
            DrawEventPoint(batcher, rc, rc.Snapshot.EventPoints[i], false,
                pinnedSection: i == _pinSection, pinnedPhrase: i == _pinPhrase);
        }

        for (var i = 0; i < rc.Snapshot.PreviewEventPointCount; i++)
            DrawEventPoint(batcher, rc, rc.Snapshot.PreviewEventPoints[i], true, false, false);

        // A pending change's NEW markers (the AI chat's sections & phrases review), in the
        // EditTarget colour and LAST, so a rename reads as "this becomes that" over the live chip,
        // which is drawn faded.
        for (var i = 0; i < rc.Snapshot.ProposedEventPointCount; i++)
            DrawEventPoint(batcher, rc, rc.Snapshot.ProposedEventPoints[i], false, false, false, proposal: true);
    }

    /// <summary>A live marker a pending change replaces is drawn at this alpha, as a replaced
    /// anchor is (<see cref="FretAnchorLayer.ReplacedAlpha" />).</summary>
    private static Color Replaced(Color c) => Color.FromArgb((byte)(c.A * FretAnchorLayer.ReplacedAlpha), c.R, c.G, c.B);

    private static Color Proposal(byte alpha)
        => Color.FromArgb(alpha, ChartTheme.EditTargetColor.R, ChartTheme.EditTargetColor.G, ChartTheme.EditTargetColor.B);

    private void DrawLyrics(TimelineBatcher batcher, ChartRenderContext rc)
    {
        // Lyrics use a dedicated path (not DrawBadge) because each syllable is
        // sized to its actual duration on the timeline rather than to the text
        // width, and word parts get a continuous connector that visually
        // groups syllables of one word. LyricPillLayout sizes each pill and
        // stops it before the next syllable, so short syllables never overlap.
        const float defaultEndMs = (float)LyricPillMetrics.DefaultDurationMs;
        var height = rc.Ribbon.Bar;

        for (var i = 0; i < rc.Snapshot.LyricPhraseCount; i++)
        {
            var phrase = rc.Snapshot.LyricPhrases[i];
            if (phrase.StartTime > rc.ViewEndMs || phrase.EndTime < rc.ViewStartMs) continue;

            DrawPhraseRegion(batcher, rc, i, phrase);

            for (var w = 0; w < phrase.WordCount; w++)
            {
                var word = rc.Snapshot.LyricWords[phrase.WordOffset + w];
                var endTime = word.EndTime > word.Time ? word.EndTime : word.Time + defaultEndMs;
                if (word.Time > rc.ViewEndMs || endTime < rc.ViewStartMs) continue;

                var startX = (float)((word.Time - rc.ViewStartMs) * rc.CurrentZoom);
                var endX = (float)((endTime - rc.ViewStartMs) * rc.CurrentZoom);
                var pill = LyricPillLayout.Layout(startX, endX, NextSyllableX(rc, i, w),
                    batcher.MeasureTextWidth(word.Text));
                var width = pill.Width;
                var pillY = rc.Ribbon.BarTop(rc.Ribbon.LyricY);

                if (word.IsWordPart && w + 1 < phrase.WordCount)
                {
                    var next = rc.Snapshot.LyricWords[phrase.WordOffset + w + 1];
                    var nextX = (float)((next.Time - rc.ViewStartMs) * rc.CurrentZoom);
                    if (nextX > startX + width)
                    {
                        // 2-px solid connector at the chip's vertical centre,
                        // colored to match the lyric chip — visually links
                        // syllables of one word into a single phrase.
                        var connY = pillY + height * 0.5f - 1f;
                        batcher.AddRect(startX + width, connY, nextX - (startX + width), 2f, LyricConnector);
                    }
                }

                var fill = word.IsSelected || word.IsHovered ? LyricFillEmph : LyricFill;
                DrawChip(batcher, startX, rc.Ribbon.LyricY, width, fill, word.IsSelected || word.IsHovered);
                // Left out when even a tight inset cannot hold it (LyricPillLayout); a
                // syllable that began off-screen left keeps its text on the part showing.
                if (pill.ShowText)
                    batcher.AddText(word.Text, pill.TextX, pillY + height * 0.5f, OnColorText, centerY: true);
            }
        }
    }

    /// <summary>The limit the pill of word <paramref name="w" /> of phrase <paramref name="p" /> is laid
    /// out against: the next syllable's start in the same phrase, else the next phrase's region edge
    /// (<see cref="LyricPillLayout.LastPillLimit" />), else +infinity.</summary>
    private static float NextSyllableX(ChartRenderContext rc, int p, int w)
    {
        var phrase = rc.Snapshot.LyricPhrases[p];
        if (w + 1 < phrase.WordCount)
            return ScreenX(rc, rc.Snapshot.LyricWords[phrase.WordOffset + w + 1].Time);
        var next = NextPhraseFirstX(rc, p);
        return float.IsPositiveInfinity(next) ? next : LyricPillLayout.LastPillLimit(next);
    }

    /// <summary>Where the first syllable of the next phrase with words starts, or +infinity.</summary>
    private static float NextPhraseFirstX(ChartRenderContext rc, int p)
    {
        for (var q = p + 1; q < rc.Snapshot.LyricPhraseCount; q++)
        {
            var next = rc.Snapshot.LyricPhrases[q];
            if (next.WordCount > 0) return ScreenX(rc, rc.Snapshot.LyricWords[next.WordOffset].Time);
        }

        return float.PositiveInfinity;
    }

    /// <summary>
    ///     A vocal phrase as ONE region: a rounded, tinted band behind its syllables with a short
    ///     accent at its start (not "[" / "]" bracket
    ///     ticks — those look dated, and the closing one sits at the phrase's end TIME, inside
    ///     or short of a last pill widened to fit its text). The band follows the pills actually drawn
    ///     (<see cref="LyricPillLayout.PhraseRegion" />) and stays inside the lyric row: a full-height
    ///     line means a section or a guitar phrase, and a vocal phrase must not read as one.
    /// </summary>
    private static void DrawPhraseRegion(TimelineBatcher batcher, ChartRenderContext rc, int p, RenderLyricPhrase phrase)
    {
        if (phrase.WordCount == 0) return;

        var last = phrase.WordCount - 1;
        var lastWord = rc.Snapshot.LyricWords[phrase.WordOffset + last];
        var lastEnd = lastWord.EndTime > lastWord.Time ? lastWord.EndTime : lastWord.Time + LyricPillMetrics.DefaultDurationMs;
        var lastStartX = ScreenX(rc, lastWord.Time);
        var lastPill = LyricPillLayout.Layout(lastStartX, ScreenX(rc, lastEnd), NextSyllableX(rc, p, last),
            batcher.MeasureTextWidth(lastWord.Text));

        var (x0, x1) = LyricPillLayout.PhraseRegion(ScreenX(rc, phrase.StartTime), ScreenX(rc, phrase.EndTime),
            lastStartX + lastPill.Width, NextPhraseFirstX(rc, p));
        if (x1 < -ChartEditorConstants.RenderMetrics.EventCullBufferPx ||
            x0 > rc.Width + ChartEditorConstants.RenderMetrics.EventCullBufferPx)
        {
            return;
        }
        // Squeezed to nothing by the next phrase (LyricPillLayout.PhraseRegion): no band, and no accent.
        if (x1 <= x0) return;

        // The whole row less a half-pixel inset each side: at normal density that is
        // exactly the chip grown by 2 px each way, and it stays inside a compact row.
        var y = rc.Ribbon.LyricY + 0.5f;
        var h = rc.Ribbon.RowPitch - 1f;
        const float radius = 5f;
        batcher.AddRoundRect(x0, y, x1 - x0, h, radius, LyricPhraseRegionFill);
        batcher.AddRect(x0 + 1f, y + 4f, 2f, h - 8f, LyricPhraseRegionAccent);
    }

    // Category palette — muted modern flat fills tuned to sit alongside the
    // Avalonia button/input palette without screaming. Alpha ≈ 215 keeps
    // them slightly translucent so the underlying ribbon banding shows
    // through and the chips feel embedded in the timeline rather than
    // floating on top of it.
    private static readonly Color ToneFill = Color.FromArgb(215, 22, 130, 96);    // emerald, dimmed
    private static readonly Color ToneFillHover = Color.FromArgb(240, 34, 156, 117);
    // The 3D view's phrase colour, and a step lighter when hovered (HudEventBadges).
    private static readonly Color PhraseFill = HudEventBadges.PhraseColor;
    private static readonly Color PhraseFillHover = Color.FromArgb(255, 198, 134, 66);
    internal static readonly Color LyricFill = Color.FromArgb(215, 64, 76, 96);     // slate, dimmed
    private static readonly Color LyricFillEmph = Color.FromArgb(240, 88, 102, 124);
    private static readonly Color LyricConnector = Color.FromArgb(200, 88, 102, 124);
    // Vocal-phrase region: an indigo-leaning wash behind the syllables — a slate
    // band would read as the same colour as the slate pills and vanish;
    // the hue shift makes the gaps between one phrase's
    // syllables read as one band — and a
    // start accent in the slate lifted by the same +70..80 per channel the pinned
    // stripe / active underline accents use. No edge line: an outline at the two
    // ends is exactly the bracket look this avoids.
    internal static readonly Color LyricPhraseRegionFill = Color.FromArgb(150, 60, 72, 132);
    internal static readonly Color LyricPhraseRegionAccent = Color.FromArgb(235, 139, 151, 171);

    private static readonly Color ToneLineCol = Color.FromArgb(150, 22, 130, 96);
    private static readonly Color PhraseLineCol = Color.FromArgb(150, 179, 118, 54);

    /// <summary>The lane a chip sits on. A chip is drawn OPAQUE (its fill pre-blended over this) so
    /// its anti-aliased corners can overlap its body without darkening.</summary>
    internal static readonly Color LaneColor = Color.FromRgb(24, 24, 27);

    // Text colours — slate-50 / slate-100 rather than pure white. Pure
    // white at 100% on coloured chips reads as harsh in dark themes;
    // slate-100 keeps the contrast WCAG-AA on every fill above without
    // the eye-strain of full white.
    private static readonly Color OnColorText = Color.FromArgb(245, 240, 244, 250);
    private static readonly Color OnPhraseText = Color.FromArgb(255, 24, 28, 38);     // amber needs dark text

    // Pinned (sticky) labels: the most recent tone/section/phrase before the
    // current viewport start gets drawn at a fixed screen X so it's always
    // visible, until the next chip of its row pushes it out (StickyLabel).
    // We record the rect for each so EventInteractionTool can route clicks on
    // them directly to the event dialog.
    private void DrawPinnedLabels(TimelineBatcher batcher, ChartRenderContext rc)
    {
        _pinnedRegistry.Clear(_owner);
        var fixedX = ChartEditorConstants.RenderMetrics.ActiveLabelFixedX;

        if (_pinTone >= 0)
        {
            var t = rc.Snapshot.Tones[_pinTone];
            var next = _pinTone + 1 < rc.Snapshot.ToneCount
                ? ScreenX(rc, rc.Snapshot.Tones[_pinTone + 1].Time)
                : float.PositiveInfinity;
            var w = BadgeWidth(batcher, t.ToneName);
            var x = StickyLabel.PinnedX(fixedX, w, next);
            if (StickyLabel.IsVisible(x, w))
            {
                var y = rc.Ribbon.ToneY;
                // The in-flow chip is suppressed while pinned, so this copy carries its selected /
                // hovered look — it was drawn plain, and a selection vanished when it pinned.
                var emph = t.IsSelected || t.IsHovered;
                DrawBadge(batcher, t.ToneName, x, y, emph ? ToneFillHover : ToneFill, OnColorText, emph,
                    pinned: true);
                _pinnedRegistry.Add(_owner, new PinnedLabelHitRegistry.Entry(
                    PinnedRect(x, y, w), t.Time, PinnedLabelHitRegistry.PinnedLabelKind.Tone));
            }
        }

        if (_pinSection >= 0)
        {
            var ep = rc.Snapshot.EventPoints[_pinSection];
            var text = RenderStringCache.Upper(ep.Section);
            var w = BadgeWidth(batcher, text);
            var x = StickyLabel.PinnedX(fixedX, w, NextEventX(rc, _pinSection, section: true));
            if (StickyLabel.IsVisible(x, w))
            {
                var y = rc.Ribbon.SectionY;
                // The pinned section is usually the ACTIVE one: give it the
                // same emphasis as its in-flow chip.
                var isActive = _polish && ep.Time == _activeSectionTime;
                var emph = ep.IsSelected || ep.IsHovered;
                DrawBadge(batcher, text, x, y, SectionColors.ChipFill(emph || isActive),
                    OnColorText, emph, pinned: true);
                _pinnedRegistry.Add(_owner, new PinnedLabelHitRegistry.Entry(
                    PinnedRect(x, y, w), ep.Time, PinnedLabelHitRegistry.PinnedLabelKind.Section));
            }
        }

        if (_pinPhrase >= 0)
        {
            var ep = rc.Snapshot.EventPoints[_pinPhrase];
            var w = BadgeWidth(batcher, ep.Phrase);
            var x = StickyLabel.PinnedX(fixedX, w, NextEventX(rc, _pinPhrase, section: false));
            if (StickyLabel.IsVisible(x, w))
            {
                var y = rc.Ribbon.PhraseY;
                var emph = ep.IsSelected || ep.IsHovered;
                DrawBadge(batcher, ep.Phrase, x, y, emph ? PhraseFillHover : PhraseFill, OnPhraseText, emph,
                    pinned: true);
                _pinnedRegistry.Add(_owner, new PinnedLabelHitRegistry.Entry(
                    PinnedRect(x, y, w), ep.Time, PinnedLabelHitRegistry.PinnedLabelKind.Phrase));
            }
        }
    }

    private static float ScreenX(ChartRenderContext rc, double timeMs) =>
        (float)((timeMs - rc.ViewStartMs) * rc.CurrentZoom);

    /// <summary>Where the next section (or phrase) chip after event <paramref name="index" /> starts:
    /// the chip that pushes the pinned one out.</summary>
    private static float NextEventX(ChartRenderContext rc, int index, bool section)
    {
        for (var i = index + 1; i < rc.Snapshot.EventPointCount; i++)
        {
            var ep = rc.Snapshot.EventPoints[i];
            if (!string.IsNullOrEmpty(section ? ep.Section : ep.Phrase)) return ScreenX(rc, ep.Time);
        }

        return float.PositiveInfinity;
    }

    /// <summary>
    ///     THE screen box of a pinned badge: exactly what <see cref="DrawChip" />
    ///     paints for the same <paramref name="rowY" /> and <paramref name="w" />.
    ///     <para>
    ///         It must not be restated at each of the three call sites as
    ///         <c>new Rect(x, rowY, MeasureTextWidth(text) + 12, BarHeight)</c> and
    ///         both numbers would be wrong: the chip is drawn at
    ///         <see cref="RibbonLayout.BarTop" /> (rowY + 2.5, the row's own
    ///         centring inset), and <c>DrawBadge</c> pads the text by
    ///         9 px per SIDE, so that rect would sit 2.5 px above the badge and stop
    ///         6 px short of its right edge. Clicking the bottom-right of a
    ///         visible sticky section/phrase/tone chip would fall through to the row
    ///         handler and create an event instead of opening the one clicked.
    ///     </para>
    /// </summary>
    private Rect PinnedRect(float x, float rowY, float w)
        => new(x, _ribbon.BarTop(rowY), w, _ribbon.Bar);

    private void DrawEventPoint(TimelineBatcher batcher, ChartRenderContext rc, RenderEventPoint ev, bool isPreview,
        bool pinnedSection, bool pinnedPhrase, bool proposal = false)
    {
        var x = (float)((ev.Time - rc.ViewStartMs) * rc.CurrentZoom);

        if (x < -ChartEditorConstants.RenderMetrics.EventCullBufferPx ||
            x > rc.Width + ChartEditorConstants.RenderMetrics.EventCullBufferPx)
        {
            return;
        }

        var emph = ev.IsSelected || ev.IsHovered;

        if (!string.IsNullOrEmpty(ev.Section))
        {
            if (isPreview && ev.Section == "+ Add Tone")
            {
                if (!rc.Ribbon.ShowTones) return;
                DrawDashedVerticalLine(batcher, x,
                    rc.Ribbon.BarBottom(rc.Ribbon.ToneY),
                    (float)rc.Height,
                    ChartEditorConstants.RenderMetrics.EventLineThickness, ToneLineCol);
                var previewBg = Color.FromArgb(150, ToneFill.R, ToneFill.G, ToneFill.B);
                DrawBadge(batcher, ev.Section, x, rc.Ribbon.ToneY, previewBg, OnColorText);
            }
            else
            {
                if (!rc.Ribbon.ShowSections) goto AfterSection;
                // Coloured by NAME, like the song map (SectionColors).
                DrawDashedVerticalLine(batcher, x,
                    rc.Ribbon.BarBottom(rc.Ribbon.SectionY),
                    (float)rc.Height,
                    ChartEditorConstants.RenderMetrics.EventLineThickness,
                    proposal ? Proposal(150) : SectionColors.DropLine());
                // Active-section emphasis: the section the playhead is inside
                // gets the brighter fill. It deliberately wears no underline: a 2 px pink
                // one read as a stray line.
                var isActive = _polish && !isPreview && ev.Time == _activeSectionTime;
                var sectionFill = SectionColors.ChipFill(emphasised: false);
                var bg = proposal ? Proposal(235)
                    : isPreview
                        ? Color.FromArgb(150, sectionFill.R, sectionFill.G, sectionFill.B)
                        : SectionColors.ChipFill(emph || isActive);
                if (ev.SectionIsEditTarget && !proposal) bg = Replaced(bg);
                if (!pinnedSection) // shown by its pinned chip
                {
                    DrawBadge(batcher, RenderStringCache.Upper(ev.Section), x, rc.Ribbon.SectionY,
                        bg, OnColorText, emph && !isPreview);
                }
            }
        }
    AfterSection:

        if (!string.IsNullOrEmpty(ev.Phrase) && rc.Ribbon.ShowPhrases)
        {
            DrawDashedVerticalLine(batcher, x,
                rc.Ribbon.BarBottom(rc.Ribbon.PhraseY),
                (float)rc.Height,
                ChartEditorConstants.RenderMetrics.EventLineThickness, proposal ? Proposal(150) : PhraseLineCol);
            var bg = proposal ? Proposal(235)
                : isPreview
                    ? Color.FromArgb(150, PhraseFill.R, PhraseFill.G, PhraseFill.B)
                    : emph ? PhraseFillHover : PhraseFill;
            if (ev.PhraseIsEditTarget && !proposal) bg = Replaced(bg);
            if (!pinnedPhrase) // shown by its pinned chip
                DrawBadge(batcher, ev.Phrase, x, rc.Ribbon.PhraseY, bg, proposal ? OnColorText : OnPhraseText, emph && !isPreview);
        }
    }

    /// <summary>
    /// Event drop-line: one gradient quad fading from the badge's color at
    /// the top to fully transparent at the bottom. Replaces the old dashed
    /// loop (~25 separate quads per event line) with a single quad — about
    /// 25× cheaper and visually richer (subtle "ray" effect that anchors
    /// the badge to the timeline without competing with note rendering).
    /// </summary>
    private void DrawDashedVerticalLine(TimelineBatcher batcher, float x, float startY, float endY,
        float thickness, Color color)
    {
        var halfThickness = thickness * 0.5f;
        var bottomFade = Color.FromArgb(0, color.R, color.G, color.B);
        var midFade = Color.FromArgb((byte)(color.A / 3), color.R, color.G, color.B);
        var midY = (startY + endY) * 0.5f;
        // Two vertically-stacked gradient quads: top half color→mid (steady
        // brightness near the badge), bottom half mid→transparent (fades
        // into the playfield). Order: TL, BL, BR, TR.
        batcher.AddQuadGradient(
            x - halfThickness, startY, x - halfThickness, midY,
            x + halfThickness, midY, x + halfThickness, startY,
            color, midFade, midFade, color);
        batcher.AddQuadGradient(
            x - halfThickness, midY, x - halfThickness, endY,
            x + halfThickness, endY, x + halfThickness, midY,
            midFade, bottomFade, bottomFade, midFade);
    }

    /// <summary>
    /// Modern flat chip badge — matches the Avalonia theme's button /
    /// input language (CornerRadius=4, muted fill, hairline inner border).
    /// Vertically centered inside its ribbon row. Selection is shown with a
    /// 1.5-px brighter border in the chip's own colour family rather than a
    /// white ring so it stays inside the theme. The polish pass
    /// adds a soft 1.5px drop shadow under every chip, a 2px left accent
    /// stripe on pinned (sticky) badges, and a brighter fill on the ACTIVE
    /// section chip. Returns the chip width.
    /// </summary>
    private float DrawBadge(TimelineBatcher batcher, string text, float x, float rowY, Color bgBrush, Color textBrush)
        => DrawBadge(batcher, text, x, rowY, bgBrush, textBrush, false);

    private float DrawBadge(TimelineBatcher batcher, string text, float x, float rowY, Color bgBrush, Color textBrush,
        bool selected, bool pinned = false)
    {
        var w = BadgeWidth(batcher, text);
        DrawChip(batcher, x, rowY, w, bgBrush, selected);
        var y = _ribbon.BarTop(rowY);
        batcher.AddText(text, x + BadgeHPad, y + _ribbon.Bar * 0.5f, textBrush, centerY: true);
        return w;
    }

    private const float BadgeHPad = 9f;

    /// <summary>The width a badge chip takes: its text plus <see cref="BadgeHPad" /> each side.</summary>
    private static float BadgeWidth(TimelineBatcher batcher, string text) =>
        batcher.MeasureTextWidth(text) + BadgeHPad * 2f;

    /// <summary>
    /// Core chip primitive: a flat, opaque fill with anti-aliased 4-px corners. A selected chip
    /// wears a ring 1.5 px outside it in a lifted shade of its own fill. It carries no
    /// offset shadow, no hairline border drawn as four straight strips (which notch the corners)
    /// and no light stripe on pinned badges; at 20 px high all three read as smudge, not depth.
    /// </summary>
    private void DrawChip(TimelineBatcher batcher, float x, float rowY, float w, Color fill, bool selected)
    {
        var h = _ribbon.Bar;
        const float radius = 4f;
        var y = _ribbon.BarTop(rowY);
        var solid = OnLane(fill);

        if (selected)
            batcher.AddRoundRectAa(x - 1.5f, y - 1.5f, w + 3f, h + 3f, radius + 1.5f, Lift(solid, 60));

        batcher.AddRoundRectAa(x, y, w, h, radius, solid);
    }

    /// <summary><paramref name="c" /> pre-blended over <see cref="LaneColor" />: what a translucent
    /// fill looked like on the lane, as an opaque colour.</summary>
    internal static Color OnLane(Color c)
    {
        var a = c.A / 255f;
        return Color.FromRgb(
            (byte)MathF.Round(c.R * a + LaneColor.R * (1 - a)),
            (byte)MathF.Round(c.G * a + LaneColor.G * (1 - a)),
            (byte)MathF.Round(c.B * a + LaneColor.B * (1 - a)));
    }

    private static Color Lift(Color c, int by) => Color.FromRgb((byte)Math.Min(255, c.R + by),
        (byte)Math.Min(255, c.G + by), (byte)Math.Min(255, c.B + by));

}
