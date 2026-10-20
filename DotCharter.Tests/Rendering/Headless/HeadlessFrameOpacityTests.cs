using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     A rendered 3D frame must be OPAQUE — every pixel, alpha 255.
///     <para>
///         This is the guard for the owner-reported "box vignet thing" of
///         2026-08-01: a large hard-edged dark rectangle spanning
///         x, y ∈ [0.16, 0.84] of the highway, and a faint translucent rectangle
///         around every note head. Both were one bug — the scene pipelines blended
///         alpha with <c>(One, Zero)</c>, so every translucent draw OVERWROTE the
///         frame's alpha with its own instead of accumulating coverage. The
///         vignette's four border quads stamped their 70→0 alpha across the
///         border bands and left the middle at 255; every note glow quad stamped
///         its own near-zero alpha over its whole rectangle. Composited, that is
///         exactly the two symptoms, and the owner's screenshot reconstructs to
///         the last unit as <c>rgb + backdrop·(1 − a)</c> over a headless frame
///         of the same scene.
///     </para>
///     <para>
///         <b>Why the existing goldens were blind:</b> every helper in
///         <see cref="GoldenFrame" /> read B, G and R and skipped the alpha byte,
///         and the colour output is byte-identical with either blend — so the
///         whole GPU suite passed throughout. <see cref="GoldenFrame.NonOpaqueBounds" />
///         is the helper that closes that hole.
///     </para>
///     <para>
///         GPU-gated on purpose: the claim is about what the real Vulkan
///         pipelines write into a real framebuffer, and only a rendered frame can
///         prove it. The blend STATE itself is pinned GPU-free in
///         <c>FrameAlphaPolicyTests</c> — it takes both, exactly as with the
///         chord-name pair.
///     </para>
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessFrameOpacityTests
{
    private const int Width = 960;
    private const int Height = 540;

    /// <summary>A 6-string guitar chart with chords, hand shapes, sustains,
    /// beats and HUD text — i.e. every class of translucent quad the highway
    /// draws, plus the vignette and the crowd backdrop that frame it.</summary>
    private static FrameSnapshot BuildGuitarScene()
    {
        var s = new FrameSnapshot
        {
            StringCount = 6, HighwayScrollSpeed = 1.3f, Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = 5, Width = 4 }],
            AnchorCount = 1
        };

        var beats = new List<RenderBeat>();
        for (var i = 0; i < 48; i++)
        {
            beats.Add(new RenderBeat
            {
                Time = i * 500, IsMeasure = i % 4 == 0, Bpm = 120,
                BeatsInMeasure = 4, NoteDenominator = 4, MeasureNumber = i / 4
            });
        }

        s.Beats = beats.ToArray();
        s.BeatCount = beats.Count;

        s.Templates = [new RenderChordTemplate { Id = 0, Name = "A7" }];
        s.TemplateCount = 1;

        var notes = new List<RenderNote>();
        for (var i = 0; i < 10; i++)
        {
            notes.Add(new RenderNote
            {
                Time = 200 + i * 260, Sustain = i % 3 == 0 ? 220 : 0,
                StringIndex = (byte)(i % 6), Fret = (byte)(5 + i % 4)
            });
        }

        s.Notes = notes.ToArray();
        s.NoteCount = notes.Count;

        s.Chords =
        [
            new RenderChord { Time = 250, TemplateId = 0, NoteOffset = 0, NoteCount = 3 },
            new RenderChord { Time = 1500, TemplateId = 0, NoteOffset = 0, NoteCount = 3 }
        ];
        s.ChordCount = 2;
        s.ChordNotes =
        [
            new RenderChordNote { StringIndex = 3, Fret = 5 },
            new RenderChordNote { StringIndex = 4, Fret = 7 },
            new RenderChordNote { StringIndex = 5, Fret = 7 }
        ];
        s.ChordNoteCount = 3;

        s.HandShapes =
        [
            new RenderHandShape { Time = 250, EndTime = 900, TemplateId = 0 },
            new RenderHandShape { Time = 1500, EndTime = 2100, TemplateId = 0 }
        ];
        s.HandShapeCount = 2;

        s.EventPoints = [new RenderEventPoint { Time = 0, Section = "Verse", Phrase = "riff1" }];
        s.EventPointCount = 1;
        s.ActiveToneName = "clean";

        return s;
    }

    /// <summary>
    ///     The whole frame is opaque — with the vignette on (the border quads
    ///     that produced the rectangle) and off (so the note/highway glows are
    ///     covered on their own and the guard cannot be satisfied by the
    ///     vignette alone).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRenderedFrameIsFullyOpaque(bool vignette)
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        var snapshot = BuildGuitarScene();
        snapshot.EnableVignette = vignette;

        var frame = r.Render(snapshot, Width, Height, timeMs: 0);
        GoldenFrame.Dump(frame, Width, Height, $"vk_opacity_vignette_{vignette}");

        var holes = GoldenFrame.NonOpaqueBounds(frame, Width, Height);
        if (holes is not null)
        {
            // Dump what a compositor would actually show, not the raw frame —
            // the raw RGB looks correct, which is the entire trap.
            GoldenFrame.Attach(frame, Width, Height, $"vk_opacity_raw_vignette_{vignette}");
            GoldenFrame.Attach(GoldenFrame.CompositeOverBackdrop(frame, 20, 15, 14), Width, Height,
                $"vk_opacity_composited_vignette_{vignette}");
        }

        Assert.True(holes is null,
            holes is null
                ? string.Empty
                : $"{holes.Value.Pixels} of {Width * Height} pixels are not opaque " +
                  $"(box x {holes.Value.X0}..{holes.Value.X1}, y {holes.Value.Y0}..{holes.Value.Y1}). " +
                  "A scene pipeline is writing its own alpha into the frame — see FrameAlphaPolicy.");
    }

    /// <summary>
    ///     Compositing an opaque frame over any backdrop must be the identity.
    ///     This is the direct statement of the user-visible bug: with the broken
    ///     blend the same operation lifted the four vignette bands by the
    ///     backdrop's colour and left the middle untouched, which is the
    ///     hard-edged rectangle the owner saw.
    /// </summary>
    [Fact]
    public void CompositingTheFrameOverABackdropChangesNothing()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        var frame = r.Render(BuildGuitarScene(), Width, Height, timeMs: 0);
        var composited = GoldenFrame.CompositeOverBackdrop(frame, 20, 15, 14);

        var box = GoldenFrame.DifferenceBounds(frame, composited, Width, Height, perChannelTol: 0);
        if (box is not null)
            GoldenFrame.Attach(composited, Width, Height, "vk_opacity_composited");

        Assert.True(box is null,
            box is null
                ? string.Empty
                : $"Compositing moved {box.Value.Pixels} pixels in x {box.Value.X0}..{box.Value.X1}, " +
                  $"y {box.Value.Y0}..{box.Value.Y1} — the frame is not opaque.");
    }
}
