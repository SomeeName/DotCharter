// ReSharper disable InconsistentNaming -- private static color/brush fields use _camelCase to pair with same-named public PascalCase properties.
using Avalonia.Media;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers;

public class NoteLayer : ITimelineRenderLayer
{
    private readonly List<DeferredLabel> _deferredLabels = new(256);

    // Per note and per chord note: where the next head on the same string starts and its fret
    // (NextHeadScan), so slide boxes and bend labels keep off it. Grown, never shrunk; the two
    // per-string arrays are scratch.
    private float[] _noteNextX = new float[256];
    private byte[] _noteNextFret = new byte[256];
    private float[] _chordNoteNextX = new float[256];
    private byte[] _chordNoteNextFret = new byte[256];
    private readonly double[] _nextTimeOnString = new double[16];
    private readonly byte[] _nextFretOnString = new byte[16];

    /// <summary>Sizes the next-head arrays to this frame's snapshot and fills them (NextHeadScan).</summary>
    private void ScanNextHeads(ChartRenderContext rc)
    {
        var s = rc.Snapshot;
        var chordNoteCount = s.ChordCount > 0 ? s.Chords[s.ChordCount - 1].NoteOffset + s.Chords[s.ChordCount - 1].NoteCount : 0;
        if (_noteNextX.Length < s.NoteCount)
        {
            var size = Math.Max(s.NoteCount, _noteNextX.Length * 2);
            _noteNextX = new float[size];
            _noteNextFret = new byte[size];
        }

        if (_chordNoteNextX.Length < chordNoteCount)
        {
            var size = Math.Max(chordNoteCount, _chordNoteNextX.Length * 2);
            _chordNoteNextX = new float[size];
            _chordNoteNextFret = new byte[size];
        }

        NextHeadScan.Fill(s.Notes, s.NoteCount, s.Chords, s.ChordCount, s.ChordNotes, rc.ViewStartMs, rc.CurrentZoom,
            _noteNextX, _noteNextFret, _chordNoteNextX, _chordNoteNextFret, _nextTimeOnString, _nextFretOnString);
    }

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        _deferredLabels.Clear();

        // Pre-bake chord-panel color variants once per frame (4 allocs total
        // instead of per-chord) — picks up theme changes the next frame.
        var linker = ChartTheme.ChordLinkerColor;
        var lightGray = ChartTheme.LightGrayColor;
        var white = ChartTheme.WhiteColor;
        var panelDefault = Color.FromArgb(70, linker.R, linker.G, linker.B);
        var panelPreview = Color.FromArgb(35, linker.R, linker.G, linker.B);
        var panelHovered = Color.FromArgb(110, lightGray.R, lightGray.G, lightGray.B);
        var panelSelected = Color.FromArgb(150, white.R, white.G, white.B);

        // 1. Draw Chord Background Panels (skipped at Marks LOD — at that
        // density the panels fuse into a translucent smear behind the marks).
        if (rc.NoteLod != NoteLevelOfDetail.Marks)
        {
            for (var i = 0; i < rc.Snapshot.ChordCount; i++)
            {
                var chord = rc.Snapshot.Chords[i];
                if (chord.NoteCount > 1)
                {
                    DrawChordPanel(batcher, rc, chord, chord.NoteOffset, rc.Snapshot.ChordNotes, false,
                        panelDefault, panelPreview, panelHovered, panelSelected);
                }
            }

            for (var i = 0; i < rc.Snapshot.PreviewChordCount; i++)
            {
                var chord = rc.Snapshot.PreviewChords[i];
                if (chord.NoteCount > 1)
                {
                    DrawChordPanel(batcher, rc, chord, chord.NoteOffset, rc.Snapshot.PreviewChordNotes, true,
                        panelDefault, panelPreview, panelHovered, panelSelected);
                }
            }
        }

        // 1b. The bulk-edit preview's brackets, at every density: they say
        // WHICH chords an edit would change, and at Marks LOD there is no panel.
        for (var i = 0; i < rc.Snapshot.ChordCount; i++)
        {
            var chord = rc.Snapshot.Chords[i];
            if ((chord.Flags & ChordFlags.EditTarget) != 0 && chord.NoteCount > 0)
                DrawEditTargetBrackets(batcher, rc, chord, rc.Snapshot.ChordNotes);
        }

        // 2. Draw Note and Chord Sustains
        ScanNextHeads(rc);

        for (var i = 0; i < rc.Snapshot.NoteCount; i++)
        {
            var n = rc.Snapshot.Notes[i];
            if (n.Sustain > 0)
            {
                var data = new NoteDrawData(n.Time, n.Sustain, n.StringIndex, n.Fret, n.Mute, n.Hopo, n.Harmonic,
                    n.SlideTo, n.Flags, false, n.BendOffset, n.BendCount, n.BassPicking);
                HighwaySustainRenderer.Draw(batcher, rc, in data, _deferredLabels, _noteNextX[i], _noteNextFret[i]);
            }
        }

        for (var i = 0; i < rc.Snapshot.ChordCount; i++)
        {
            var chord = rc.Snapshot.Chords[i];
            var dimmed = IsRepeat(chord);
            for (var n = 0; n < chord.NoteCount; n++)
            {
                var cn = rc.Snapshot.ChordNotes[chord.NoteOffset + n];
                if (cn.Sustain > 0)
                {
                    var data = new NoteDrawData(chord.Time, cn.Sustain, cn.StringIndex, cn.Fret, cn.Mute, cn.Hopo,
                        cn.Harmonic, cn.SlideTo, cn.Flags, false, cn.BendOffset, cn.BendCount, cn.BassPicking,
                        dimmed);
                    HighwaySustainRenderer.Draw(batcher, rc, in data, _deferredLabels,
                        _chordNoteNextX[chord.NoteOffset + n], _chordNoteNextFret[chord.NoteOffset + n]);
                }
            }
        }

        // 3. Draw Preview Sustains
        for (var i = 0; i < rc.Snapshot.PreviewNoteCount; i++)
        {
            var n = rc.Snapshot.PreviewNotes[i];
            if (n.Sustain > 0)
            {
                var data = new NoteDrawData(n.Time, n.Sustain, n.StringIndex, n.Fret, n.Mute, n.Hopo, n.Harmonic,
                    n.SlideTo, n.Flags, true, n.BendOffset, n.BendCount, n.BassPicking);
                HighwaySustainRenderer.Draw(batcher, rc, in data, _deferredLabels);
            }
        }

        for (var i = 0; i < rc.Snapshot.PreviewChordCount; i++)
        {
            var chord = rc.Snapshot.PreviewChords[i];
            for (var n = 0; n < chord.NoteCount; n++)
            {
                var cn = rc.Snapshot.PreviewChordNotes[chord.NoteOffset + n];
                if (cn.Sustain > 0)
                {
                    var data = new NoteDrawData(chord.Time, cn.Sustain, cn.StringIndex, cn.Fret, cn.Mute, cn.Hopo,
                        cn.Harmonic, cn.SlideTo, cn.Flags, true, cn.BendOffset, cn.BendCount, cn.BassPicking);
                    HighwaySustainRenderer.Draw(batcher, rc, in data, _deferredLabels);
                }
            }
        }

        // 4. Draw Note and Chord Heads
        for (var i = 0; i < rc.Snapshot.NoteCount; i++)
        {
            var n = rc.Snapshot.Notes[i];
            var data = new NoteDrawData(n.Time, n.Sustain, n.StringIndex, n.Fret, n.Mute, n.Hopo, n.Harmonic, n.SlideTo,
                n.Flags, false, n.BendOffset, n.BendCount, n.BassPicking);
            HighwayNoteHeadRenderer.Draw(batcher, rc, in data, _deferredLabels, _noteNextX[i]);
        }

        for (var i = 0; i < rc.Snapshot.ChordCount; i++)
        {
            var chord = rc.Snapshot.Chords[i];
            var dimmed = IsRepeat(chord);
            var chordAccent = (chord.Flags & ChordFlags.Accent) != 0;
            for (var n = 0; n < chord.NoteCount; n++)
            {
                var cn = rc.Snapshot.ChordNotes[chord.NoteOffset + n];
                var flags = cn.Flags;
                if (chordAccent) flags |= NoteFlags.Accent;
                var data = new NoteDrawData(chord.Time, cn.Sustain, cn.StringIndex, cn.Fret, cn.Mute, cn.Hopo,
                    cn.Harmonic, cn.SlideTo, flags, false, cn.BendOffset, cn.BendCount, cn.BassPicking, dimmed);
                HighwayNoteHeadRenderer.Draw(batcher, rc, in data, _deferredLabels, _chordNoteNextX[chord.NoteOffset + n]);
            }
        }

        // 5. Draw Preview Heads
        for (var i = 0; i < rc.Snapshot.PreviewNoteCount; i++)
        {
            var n = rc.Snapshot.PreviewNotes[i];
            var data = new NoteDrawData(n.Time, n.Sustain, n.StringIndex, n.Fret, n.Mute, n.Hopo, n.Harmonic, n.SlideTo,
                n.Flags, true, n.BendOffset, n.BendCount, n.BassPicking);
            HighwayNoteHeadRenderer.Draw(batcher, rc, in data, _deferredLabels);
        }

        for (var i = 0; i < rc.Snapshot.PreviewChordCount; i++)
        {
            var chord = rc.Snapshot.PreviewChords[i];
            var chordAccent = (chord.Flags & ChordFlags.Accent) != 0;
            for (var n = 0; n < chord.NoteCount; n++)
            {
                var cn = rc.Snapshot.PreviewChordNotes[chord.NoteOffset + n];
                var flags = cn.Flags;
                if (chordAccent) flags |= NoteFlags.Accent;
                var data = new NoteDrawData(chord.Time, cn.Sustain, cn.StringIndex, cn.Fret, cn.Mute, cn.Hopo,
                    cn.Harmonic, cn.SlideTo, flags, true, cn.BendOffset, cn.BendCount, cn.BassPicking);
                HighwayNoteHeadRenderer.Draw(batcher, rc, in data, _deferredLabels);
            }
        }

        // 6. Draw Ghost Notes/Chords (other arrangements as overlay)
        for (var i = 0; i < rc.Snapshot.GhostNoteCount; i++)
        {
            var n = rc.Snapshot.GhostNotes[i];
            if (n.Sustain > 0)
            {
                var data = new NoteDrawData(n.Time, n.Sustain, n.StringIndex, n.Fret, n.Mute, n.Hopo, n.Harmonic,
                    n.SlideTo, n.Flags, false, n.BendOffset, n.BendCount, n.BassPicking);
                HighwaySustainRenderer.Draw(batcher, rc, in data, _deferredLabels);
            }
        }

        for (var i = 0; i < rc.Snapshot.GhostChordCount; i++)
        {
            var chord = rc.Snapshot.GhostChords[i];
            for (var n = 0; n < chord.NoteCount; n++)
            {
                var cn = rc.Snapshot.GhostChordNotes[chord.NoteOffset + n];
                if (cn.Sustain > 0)
                {
                    var data = new NoteDrawData(chord.Time, cn.Sustain, cn.StringIndex, cn.Fret, cn.Mute, cn.Hopo,
                        cn.Harmonic, cn.SlideTo, cn.Flags, false, cn.BendOffset, cn.BendCount, cn.BassPicking);
                    HighwaySustainRenderer.Draw(batcher, rc, in data, _deferredLabels);
                }
            }
        }

        for (var i = 0; i < rc.Snapshot.GhostNoteCount; i++)
        {
            var n = rc.Snapshot.GhostNotes[i];
            var data = new NoteDrawData(n.Time, n.Sustain, n.StringIndex, n.Fret, n.Mute, n.Hopo, n.Harmonic, n.SlideTo,
                n.Flags, false, n.BendOffset, n.BendCount, n.BassPicking);
            HighwayNoteHeadRenderer.Draw(batcher, rc, in data, _deferredLabels);
        }

        for (var i = 0; i < rc.Snapshot.GhostChordCount; i++)
        {
            var chord = rc.Snapshot.GhostChords[i];
            for (var n = 0; n < chord.NoteCount; n++)
            {
                var cn = rc.Snapshot.GhostChordNotes[chord.NoteOffset + n];
                var data = new NoteDrawData(chord.Time, cn.Sustain, cn.StringIndex, cn.Fret, cn.Mute, cn.Hopo,
                    cn.Harmonic, cn.SlideTo, cn.Flags, false, cn.BendOffset, cn.BendCount, cn.BassPicking);
                HighwayNoteHeadRenderer.Draw(batcher, rc, in data, _deferredLabels);
            }
        }

        // 7. Draw all deferred labels at the very end
        foreach (var label in _deferredLabels)
        {
            if (label.HasBox)
            {
                var tw = TextRenderHelper.MeasureScaledTextWidth(batcher, label.Text, label.Scale);
                var th = (batcher.FontAtlas?.RowHeight ?? ChartEditorConstants.RenderMetrics.DefaultFontSize) *
                         label.Scale;
                batcher.AddRect(label.X, label.Y, tw + 4f, th + 2f, label.BgColor);

                if (label.OutlineColor.A > 0)
                {
                    batcher.AddThickLine(label.X, label.Y, label.X + tw + 4f, label.Y, 1.5f, label.OutlineColor);
                    batcher.AddThickLine(label.X, label.Y + th + 2f, label.X + tw + 4f, label.Y + th + 2f, 1.5f,
                        label.OutlineColor);
                    batcher.AddThickLine(label.X, label.Y, label.X, label.Y + th + 2f, 1.5f, label.OutlineColor);
                    batcher.AddThickLine(label.X + tw + 4f, label.Y, label.X + tw + 4f, label.Y + th + 2f, 1.5f,
                        label.OutlineColor);
                }

                TextRenderHelper.DrawScaledText(batcher, label.Text, label.X + 2f, label.Y + label.TextYOffset,
                    label.Scale, label.TextColor);
            }
            else
            {
                TextRenderHelper.DrawScaledText(batcher, label.Text, label.X, label.Y, label.Scale, label.TextColor);
            }
        }
    }

    private void DrawChordPanel(TimelineBatcher batcher, ChartRenderContext rc, RenderChord chord, int noteOffset,
        RenderChordNote[] noteArray, bool isPreview,
        Color panelDefault, Color panelPreview, Color panelHovered, Color panelSelected)
    {
        var x = (float)((chord.Time - rc.ViewStartMs) * rc.CurrentZoom);
        var px = x - ChartEditorConstants.RenderMetrics.ChordPanelOffset;
        var pw = ChartEditorConstants.RenderMetrics.ChordPanelWidth;

        // Screen-X cull, like every other 2D layer — see NoteCulling.
        if (NoteCulling.IsOffScreen(px, pw, rc.Width)) return;

        var minString = 99;
        var maxString = -1;
        for (var n = 0; n < chord.NoteCount; n++)
        {
            var cn = noteArray[noteOffset + n];
            minString = Math.Min(minString, cn.StringIndex);
            maxString = Math.Max(maxString, cn.StringIndex);
        }

        // Lefty flip swaps which chord note sits on top — order after mapping.
        var yMax = rc.LaneTopFor(maxString) + rc.LaneHeight / 2f;
        var yMin = rc.LaneTopFor(minString) + rc.LaneHeight / 2f;
        var topY = Math.Min(yMax, yMin);
        var bottomY = Math.Max(yMax, yMin);

        var isSelected = (chord.Flags & ChordFlags.IsSelected) != 0;
        var isHovered = (chord.Flags & ChordFlags.IsHovered) != 0;

        // A repeat's resting panel is dimmed with its heads; selected and
        // hovered panels keep their full alpha so the state reads on a repeat too.
        Color color;
        if (isSelected) color = panelSelected;
        else if (isHovered) color = panelHovered;
        else if (isPreview) color = panelPreview;
        else color = RepeatChordInk.Fade(panelDefault, IsRepeat(chord));

        var ph = bottomY - topY;

        // Rounded panel with subtle 1px highlight on top edge for selected/
        // hovered states. Reads as a discrete "card" linking the chord notes
        // instead of a flat slab.
        batcher.AddRoundRect(px, topY, pw, ph, 3f, color);
        if (isSelected || isHovered)
            batcher.AddRect(px + 2f, topY + 1f, pw - 4f, 1f, ChordPanelHighlight);
    }

    private static readonly Color ChordPanelHighlight = Color.FromArgb(70, 255, 255, 255);

    // ── Bulk-edit preview ─────────────────────────────────────────
    //
    // A chord a pending bulk chord edit would change wears four corner brackets
    // in the EditTarget colour around its notes: a SHAPE cue as well as a hue,
    // and unlike the selection's filled panel.
    // The decision is ChordFlags.EditTarget — the chord panel's resolver, as
    // data; nothing here decides what an edit reaches.

    /// <summary>How far the brackets sit outside the chord panel, in px.</summary>
    internal const float EditTargetPad = 3f;

    internal const float EditTargetStroke = 2f;

    /// <summary>Longest leg of a corner bracket, in px.</summary>
    internal const float EditTargetLeg = 6f;

    private static void DrawEditTargetBrackets(TimelineBatcher batcher, ChartRenderContext rc, RenderChord chord,
        RenderChordNote[] noteArray)
    {
        var x = (float)((chord.Time - rc.ViewStartMs) * rc.CurrentZoom)
                - ChartEditorConstants.RenderMetrics.ChordPanelOffset - EditTargetPad;
        var w = ChartEditorConstants.RenderMetrics.ChordPanelWidth + EditTargetPad * 2f;
        if (NoteCulling.IsOffScreen(x, w, rc.Width)) return;

        var minString = 99;
        var maxString = -1;
        for (var n = 0; n < chord.NoteCount; n++)
        {
            var cn = noteArray[chord.NoteOffset + n];
            minString = Math.Min(minString, cn.StringIndex);
            maxString = Math.Max(maxString, cn.StringIndex);
        }

        var a = rc.LaneTopFor(minString);
        var b = rc.LaneTopFor(maxString);
        var y = Math.Min(a, b) - EditTargetPad;
        var h = Math.Abs(a - b) + rc.LaneHeight + EditTargetPad * 2f;
        DrawCornerBrackets(batcher, x, y, w, h, ChartTheme.EditTargetColor);
    }

    /// <summary>Four L-shaped corners of the rectangle, each leg at most
    /// <see cref="EditTargetLeg" /> and never past the middle of its side.</summary>
    internal static void DrawCornerBrackets(TimelineBatcher batcher, float x, float y, float w, float h, Color color)
    {
        const float t = EditTargetStroke;
        var lx = Math.Min(EditTargetLeg, w / 2f);
        var ly = Math.Min(EditTargetLeg, h / 2f);
        batcher.AddRect(x, y, lx, t, color);
        batcher.AddRect(x, y, t, ly, color);
        batcher.AddRect(x + w - lx, y, lx, t, color);
        batcher.AddRect(x + w - t, y, t, ly, color);
        batcher.AddRect(x, y + h - t, lx, t, color);
        batcher.AddRect(x, y + h - ly, t, ly, color);
        batcher.AddRect(x + w - lx, y + h - t, lx, t, color);
        batcher.AddRect(x + w - t, y + h - ly, t, ly, color);
    }

    // ── Repeat chords ───────────────────────────────────────
    //
    // A chord the export ships WITHOUT chord notes is Rocksmith's frame-only
    // repeat box. The 3D preview draws that box; the EDITOR draws the chord it
    // repeats, dimmed (RepeatChordInk): panel, heads, numbers and sustains at
    // RepeatChordInk.Dim, technique glyphs and selection/hover at full strength, so
    // what differs between two strums stays readable. The decision is
    // ChordFlags.Repeat — data from the export's own rule (Services'
    // RepeatChords), never derived here. Marks LOD draws a repeat like any chord.

    private static bool IsRepeat(RenderChord chord) => (chord.Flags & ChordFlags.Repeat) != 0;
}
