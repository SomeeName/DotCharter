using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Text;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 2D timeline draws a chord the export ships WITHOUT chord notes (Rocksmith's repeat box) as the
///     chord it repeats, DIMMED (#564): heads, fret numbers and sustains at <see cref="RepeatChordInk.Dim" />
///     of their brightness (still OPAQUE — a translucent head let a selected panel wash its number out) and
///     the panel at that fraction of its opacity, so a run of strums reads "same chord again", while the
///     technique glyphs (mutes, accent, harmonic, HOPO) and the selection ink keep their full colour, so what
///     differs between two strums stays readable. Before, #487 drew an empty frame with no heads at all —
///     a row of white boxes. The layer reads <see cref="ChordFlags.Repeat" /> — data the snapshot copies
///     from the export's own rule — so these tests set the flag directly;
///     <see cref="RepeatChordParityTests" /> pins where it comes from.
///     <para>
///         No GPU: <see cref="TimelineBatcher" /> is a plain CPU vertex-buffer builder. Gem bodies are
///         circle SPRITES (type 1) and fret numbers are glyph quads (type 3) in the general buffer; panels,
///         tails and technique strokes are solid triangles. Each vertex carries its colour, so "dimmed" is
///         a brightness read off the buffers: the brightest channel times the alpha (<see cref="Ink" />).
///     </para>
/// </summary>
public sealed class NoteLayerRepeatChordTests
{
    private const double ChordTimeMs = 400;
    private const float LaneHeight = 60f;
    private const float TopMargin = 20f;
    private const float SpriteType = 1f;
    private const float RingType = 2f;
    private const float GlyphType = 3f;

    // ── a repeat draws the chord, dimmed ─────────────────────────────────────

    [Fact]
    public void AFullChord_DrawsAFilledPanel_AndItsHeadsAndNumbers_AtFullAlpha()
    {
        var batcher = Render(repeat: false);

        Assert.True(SolidCovers(batcher, PanelCentreX, PanelMiddleY), "a full chord's panel is filled");
        Assert.Equal(1f, MaxAlpha(batcher, SpriteType), 2);
        Assert.Equal(1f, MaxInk(batcher, GlyphType), 2);
    }

    [Fact]
    public void ARepeatChord_DrawsTheSameHeadsAndNumbers_Dimmed()
    {
        var full = Render(repeat: false);
        var repeat = Render(repeat: true);

        // The SAME geometry — heads, numbers, panel — not a frame-only stand-in.
        Assert.Equal(full.GeneralVertexCount, repeat.GeneralVertexCount);
        Assert.Equal(full.SolidVertexCount, repeat.SolidVertexCount);
        Assert.True(Count(repeat, GlyphType) > 0, "a repeat chord prints its fret numbers");

        // Visibly dimmer than the chord it repeats, and still there…
        AssertDimmed(MaxInk(full, SpriteType), MaxInk(repeat, SpriteType), "heads");
        AssertDimmed(MaxInk(full, GlyphType), MaxInk(repeat, GlyphType), "fret numbers");
        Assert.Equal(RepeatChordInk.Dim, MaxInk(repeat, GlyphType), 2);
        // …and still OPAQUE: darker ink, not see-through ink.
        Assert.Equal(1f, MaxAlpha(repeat, SpriteType), 2);
        Assert.Equal(1f, MaxAlpha(repeat, GlyphType), 2);
    }

    /// <summary>The dim this feature promises: at most 3/4 of the normal alpha and at least 1/4, so the
    /// strum reads "again" without vanishing on the dark lanes.</summary>
    private static void AssertDimmed(float full, float repeat, string what)
    {
        Assert.True(full > 0, $"control: the full chord draws its {what}");
        Assert.InRange(repeat / full, 0.25f, 0.75f);
    }

    [Fact]
    public void ARepeatChord_FillsItsPanel_Dimmed()
    {
        var full = TopmostAt(Render(repeat: false), PanelCentreX, PanelMiddleY);
        var repeat = TopmostAt(Render(repeat: true), PanelCentreX, PanelMiddleY);

        Assert.True(repeat.A > 0, "the repeat's panel is filled — no longer an empty frame");
        AssertDimmed(full.A, repeat.A, "panel");
        Assert.Equal(Rgb(full), Rgb(repeat));
        Assert.Equal((byte)(full.A * RepeatChordInk.Dim), repeat.A);
    }

    [Fact]
    public void ARepeatChord_DrawsItsSustain_Dimmed()
    {
        // Every chord note rings 300 ms — 300 px to the right of the panel.
        var x = PanelRightX + 100f;
        var y = LaneMiddleY(2) + 2f; // the lower tube half: the highlight line runs in the upper one
        var full = TopmostAt(Render(repeat: false, sustainMs: 300), x, y);
        var repeat = TopmostAt(Render(repeat: true, sustainMs: 300), x, y);

        Assert.True(full.A > 0, "control: a full chord draws its tail");
        Assert.True(repeat.A > 0, "a repeat chord draws its tail too");
        AssertDimmed(Ink(full), Ink(repeat), "tail");
        Assert.Equal(full.A, repeat.A);
    }

    // ── techniques stay at full contrast ─────────────────────────────────────

    [Theory]
    [InlineData(MuteType.Palm)]
    [InlineData(MuteType.Full)]
    public void ARepeatChordsMuteTicks_KeepFullAlpha(MuteType mute)
    {
        var s = ChartEditorConstants.RenderMetrics.BaseNoteRadius * ChartEditorConstants.RenderMetrics.MuteIconScale;
        var (from, to) = HighwayNoteHeadRenderer.MuteTickSpan(s);
        var onTick = (from + to) / 2f;
        var x = (float)ChordTimeMs + onTick;
        var y = LaneMiddleY(1) + onTick;

        var full = TopmostAt(Render(repeat: false, mute: mute), x, y);
        var repeat = TopmostAt(Render(repeat: true, mute: mute), x, y);

        Assert.Equal(255, full.A);
        Assert.Equal(full, repeat);
    }

    [Fact]
    public void ARepeatChordsAccentRing_KeepsFullAlpha()
    {
        var repeat = Render(repeat: true, accent: true);

        Assert.True(Count(repeat, RingType) > 0, "the accent ring is drawn");
        Assert.Equal(1f, MaxAlpha(repeat, RingType), 2);
    }

    [Fact]
    public void ARepeatChordsHammerOnGlyph_KeepsFullAlpha()
    {
        // HighwayNoteHeadRenderer.DrawHopoIcon: an up-triangle whose tip sits 6 px above the head.
        var x = (float)ChordTimeMs;
        var y = LaneMiddleY(1) - ChartEditorConstants.RenderMetrics.BaseNoteRadius - 6f - 3f;

        var full = TopmostAt(Render(repeat: false, hopo: HopoType.HammerOn), x, y);
        var repeat = TopmostAt(Render(repeat: true, hopo: HopoType.HammerOn), x, y);

        Assert.Equal(255, full.A);
        Assert.Equal(full, repeat);
    }

    [Fact]
    public void ARepeatChordsHarmonicDiamond_KeepsFullAlpha()
    {
        // The diamond IS the harmonic's glyph: a point on its upper-left rim.
        var r = ChartEditorConstants.RenderMetrics.BaseNoteRadius
                + ChartEditorConstants.RenderMetrics.NoteStrokeThickness / 2f;
        var x = (float)ChordTimeMs - r / 2f;
        var y = LaneMiddleY(1) - r / 2f;

        var full = TopmostAt(Render(repeat: false, harmonic: HarmonicType.Normal), x, y);
        var repeat = TopmostAt(Render(repeat: true, harmonic: HarmonicType.Normal), x, y);

        Assert.Equal(255, full.A);
        Assert.Equal(full, repeat);
    }

    // ── selection, hover and edit target still read ──────────────────────────

    [Fact]
    public void ASelectedRepeatChord_ShowsTheFullSelection()
    {
        var plain = Render(repeat: true);
        var selected = Render(repeat: true, selected: true);

        // The selected panel is the normal one, not dimmed.
        var panel = TopmostAt(selected, PanelCentreX, PanelMiddleY);
        Assert.Equal(150, panel.A);
        Assert.Equal(Rgb(ChartTheme.WhiteColor), Rgb(panel));

        // The heads wear the full white rim and the selection halo, and the numbers print full white:
        // a dimmed number on the selection's light core could not be read (GPU capture, #564).
        Assert.Equal(1f, MaxInk(selected, SpriteType), 2);
        Assert.True(MaxInk(plain, SpriteType) < 0.75f, "control: a resting repeat's heads are dimmed");
        Assert.True(Count(selected, RingType) > 0, "the selection halo is drawn");
        Assert.Equal(0, Count(plain, RingType));
        Assert.Equal(1f, MaxInk(selected, GlyphType), 2);
    }

    [Fact]
    public void AHoveredRepeatChord_ShowsTheFullHover()
    {
        var hovered = Render(repeat: true, hovered: true);

        Assert.Equal(110, TopmostAt(hovered, PanelCentreX, PanelMiddleY).A);
        var gray = ChartTheme.LightGrayColor;
        Assert.Equal(Math.Max(gray.R, Math.Max(gray.G, gray.B)) / 255f, MaxInk(hovered, SpriteType), 2);
    }

    [Fact]
    public void ARepeatEditTarget_WearsTheFullBrackets()
    {
        var batcher = Render(repeat: true, editTarget: true);
        var left = (float)ChordTimeMs - ChartEditorConstants.RenderMetrics.ChordPanelOffset - NoteLayer.EditTargetPad;
        var top = TopMargin + LaneHeight - NoteLayer.EditTargetPad;

        Assert.Equal(ChartTheme.EditTargetColor, TopmostAt(batcher, left + 1f, top + 1f));
    }

    // ── what does not change ─────────────────────────────────────────────────

    [Fact]
    public void AtMarksLod_ARepeatChordDrawsExactlyLikeAFullChord()
    {
        var full = Render(repeat: false, lod: NoteLevelOfDetail.Marks, sustainMs: 300);
        var repeat = Render(repeat: true, lod: NoteLevelOfDetail.Marks, sustainMs: 300);

        Assert.True(repeat.SolidVertexCount > 0, "a repeat keeps its marks");
        Assert.Equal(full.SolidVertexCount, repeat.SolidVertexCount);
        Assert.Equal(full.GeneralVertexCount, repeat.GeneralVertexCount);
        Assert.True(full.SolidBuffer.AsSpan(0, full.SolidVertexCount * 6)
            .SequenceEqual(repeat.SolidBuffer.AsSpan(0, repeat.SolidVertexCount * 6)));
    }

    [Fact]
    public void RenderingARepeatChordFrame_AllocatesNothing()
    {
        var layer = new NoteLayer();
        var rc = Context(Snapshot(repeat: true, sustainMs: 300, mute: MuteType.Palm, accent: true),
            NoteLevelOfDetail.Full);
        var batcher = new TimelineBatcher { FontAtlas = Atlas.Value };
        layer.Render(batcher, rc);

        AllocationProbe.AssertAllocationFree("warm repeat-chord frame", () =>
        {
            batcher.Reset();
            layer.Render(batcher, rc);
        });
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static float PanelLeftX => (float)ChordTimeMs - ChartEditorConstants.RenderMetrics.ChordPanelOffset;

    private static float PanelRightX => PanelLeftX + ChartEditorConstants.RenderMetrics.ChordPanelWidth;

    private static float PanelCentreX => PanelLeftX + ChartEditorConstants.RenderMetrics.ChordPanelWidth / 2f;

    private static float LaneMiddleY(int stringIndex) => TopMargin + stringIndex * LaneHeight + LaneHeight / 2f;

    /// <summary>Between the chord's two outer strings (1 and 3) and away from
    /// the middle string's head, so only the panel FILL covers it.</summary>
    private static float PanelMiddleY => (LaneMiddleY(1) + LaneMiddleY(2)) / 2f;

    private static TimelineBatcher Render(bool repeat, double sustainMs = 0,
        NoteLevelOfDetail lod = NoteLevelOfDetail.Full, MuteType mute = MuteType.None, bool accent = false,
        HopoType hopo = HopoType.None, HarmonicType harmonic = HarmonicType.None, bool selected = false,
        bool hovered = false, bool editTarget = false)
    {
        var batcher = new TimelineBatcher { FontAtlas = Atlas.Value };
        new NoteLayer().Render(batcher,
            Context(Snapshot(repeat, sustainMs, mute, accent, hopo, harmonic, selected, hovered, editTarget), lod));
        batcher.FinalizeFrame();
        return batcher;
    }

    private static FrameSnapshot Snapshot(bool repeat, double sustainMs = 0, MuteType mute = MuteType.None,
        bool accent = false, HopoType hopo = HopoType.None, HarmonicType harmonic = HarmonicType.None,
        bool selected = false, bool hovered = false, bool editTarget = false)
    {
        var chordFlags = (repeat ? ChordFlags.Repeat : ChordFlags.None)
                         | (accent ? ChordFlags.Accent : ChordFlags.None)
                         | (selected ? ChordFlags.IsSelected : ChordFlags.None)
                         | (hovered ? ChordFlags.IsHovered : ChordFlags.None)
                         | (editTarget ? ChordFlags.EditTarget : ChordFlags.None);
        var noteFlags = (selected ? NoteFlags.IsSelected : NoteFlags.None)
                        | (hovered ? NoteFlags.IsHovered : NoteFlags.None);

        // The technique sits on string 1 (the chord's top lane); strings 2 and 3 stay plain.
        RenderChordNote Note(int s, byte fret, bool technique) => new()
        {
            StringIndex = (byte)s, Fret = fret, Sustain = sustainMs, Flags = noteFlags,
            Mute = (byte)mute,
            Hopo = technique ? (byte)hopo : (byte)0,
            Harmonic = technique ? (byte)harmonic : (byte)0
        };

        return new FrameSnapshot
        {
            StringCount = 6,
            ChordNotes = [Note(1, 3, true), Note(2, 5, false), Note(3, 5, false)],
            ChordNoteCount = 3,
            Chords = [new RenderChord { Time = ChordTimeMs, NoteOffset = 0, NoteCount = 3, TemplateId = 1, Flags = chordFlags }],
            ChordCount = 1
        };
    }

    private static ChartRenderContext Context(FrameSnapshot snapshot, NoteLevelOfDetail lod) => new()
    {
        Width = 900,
        Height = 400,
        CurrentZoom = 1.0,
        ViewStartMs = 0,
        ViewEndMs = 900,
        TopMargin = TopMargin,
        BottomMargin = 20f,
        LaneAreaHeight = 360f,
        LaneHeight = LaneHeight,
        StringCount = 6,
        DrawLabels = true,
        NoteLod = lod,
        Snapshot = snapshot
    };

    private static readonly Lazy<ProbeAtlas> Atlas = new(() =>
    {
        var atlas = new ProbeAtlas();
        atlas.SetChartFont("Default", 14f, isBold: false);
        atlas.SetRenderScale(1f);
        atlas.RebuildIfNeeded();
        return atlas;
    });

    private static (byte R, byte G, byte B) Rgb(Color c) => (c.R, c.G, c.B);

    /// <summary>How many general-buffer vertices of one primitive type the frame holds.</summary>
    private static int Count(TimelineBatcher batcher, float type)
    {
        var n = 0;
        for (var v = 0; v < batcher.GeneralVertexCount; v++)
        {
            if (batcher.GeneralBuffer[v * 9 + 8] == type)
                n++;
        }

        return n;
    }

    /// <summary>How bright a colour reads: its brightest channel times its opacity, 0..1.</summary>
    private static float Ink(Color c) => Math.Max(c.R, Math.Max(c.G, c.B)) / 255f * (c.A / 255f);

    /// <summary>The brightest <see cref="Ink" /> among general-buffer vertices of one primitive type.</summary>
    private static float MaxInk(TimelineBatcher batcher, float type)
    {
        var max = 0f;
        for (var v = 0; v < batcher.GeneralVertexCount; v++)
        {
            var i = v * 9;
            if (batcher.GeneralBuffer[i + 8] != type) continue;
            var b = batcher.GeneralBuffer;
            max = Math.Max(max, Math.Max(b[i + 2], Math.Max(b[i + 3], b[i + 4])) * b[i + 5]);
        }

        return max;
    }

    /// <summary>The highest alpha among general-buffer vertices of one primitive type.</summary>
    private static float MaxAlpha(TimelineBatcher batcher, float type)
    {
        var max = 0f;
        for (var v = 0; v < batcher.GeneralVertexCount; v++)
        {
            var i = v * 9;
            if (batcher.GeneralBuffer[i + 8] == type)
                max = Math.Max(max, batcher.GeneralBuffer[i + 5]);
        }

        return max;
    }

    /// <summary>True when any solid triangle the layer emitted covers the point.</summary>
    private static bool SolidCovers(TimelineBatcher batcher, float x, float y) => TopmostAt(batcher, x, y).A > 0;

    /// <summary>The colour of the LAST solid triangle covering the point — what the frame shows there.
    /// Transparent black when nothing covers it.</summary>
    private static Color TopmostAt(TimelineBatcher batcher, float x, float y)
    {
        var buf = batcher.SolidBuffer;
        var found = Colors.Transparent;
        for (var v = 0; v + 2 < batcher.SolidVertexCount; v += 3)
        {
            var i = v * 6;
            if (!InTriangle(x, y, buf[i], buf[i + 1], buf[i + 6], buf[i + 7], buf[i + 12], buf[i + 13])) continue;
            found = Color.FromArgb(ToByte(buf[i + 5]), ToByte(buf[i + 2]), ToByte(buf[i + 3]), ToByte(buf[i + 4]));
        }

        return found;
    }

    private static byte ToByte(float channel) => (byte)Math.Round(channel * 255f);

    private static bool InTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        var d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        var d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        var d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    /// <summary>Upload-free atlas: the base owns every metric the batcher reads.</summary>
    private sealed class ProbeAtlas : ChartFontAtlas
    {
        protected override void UploadAtlas(SkiaSharp.SKBitmap bitmap)
        {
        }

        public override void Dispose()
        {
        }
    }
}
