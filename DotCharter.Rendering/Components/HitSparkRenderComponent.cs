using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

/// <summary>
///     0.15.6 / plan 5.7: STRING-COLORED spark burst on every note/chord hit,
///     layered under the existing yellow-red particle explosions. Entirely
///     STATELESS (the <see cref="HitSparkMath" /> pattern of
///     <c>EnhancedHitFeedbackRenderComponent</c>): sparks re-derive from the
///     snapshot's visible notes each frame — the 3D look-behind comfortably
///     covers the burst lifetime, so seeks and re-renders need no bookkeeping.
///     Drawn through the existing radial-glow vertex mode 8 — no shader
///     change. Suppressed during Live Play for the same reason as the
///     explosions: time-based bursts would read as false "hit" feedback.
/// </summary>
public class HitSparkRenderComponent : IRenderable
{
    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context)
    {
        var snap = context.Snapshot;
        if (!snap.EnableHitSparks || snap.LivePlayActive) return;

        var now = context.TimeMs;
        var stringCount = snap.StringCount;

        for (var i = 0; i < snap.NoteCount; i++)
        {
            var note = snap.Notes[i];
            if ((note.Flags & NoteFlags.LinkPrevious) != 0) continue;
            DrawBurst(context, note.Time, note.StringIndex, note.Fret, stringCount, now);
        }

        for (var i = 0; i < snap.ChordCount; i++)
        {
            var chord = snap.Chords[i];
            for (var k = 0; k < chord.NoteCount; k++)
            {
                var cn = snap.ChordNotes[chord.NoteOffset + k];
                if ((cn.Flags & NoteFlags.LinkPrevious) != 0) continue;
                DrawBurst(context, chord.Time, cn.StringIndex, cn.Fret, stringCount, now);
            }
        }
    }

    private static void DrawBurst(RenderContext context, double timeMs, byte stringIndex, byte fret,
        int stringCount, double now)
    {
        var age = now - timeMs;
        if (age is < 0 or > HitSparkMath.LifetimeMs) return;
        var t = (float)(age / HitSparkMath.LifetimeMs);

        var y = FretboardConstants.BottomStringPosition +
                FretboardConstants.StringLaneIndex(stringIndex, stringCount, context.Snapshot.InvertStrings) *
                FretboardConstants.StringDistance;
        float x;
        // `IsOpenFret`, not `fret == 0`: a capoed note draws as the anchor-wide
        // open bar, so its sparks belong across that bar.
        if (NoteRenderMath.IsOpenFret(context, fret))
        {
            var (x0, x1) = NoteRenderMath.GetAnchorSpan(context, timeMs);
            x = (x0 + x1) * 0.5f;
        }
        else
        {
            x = FretboardConstants.GetLinearFretMiddle(fret);
        }

        var sc = FretboardConstants.StringColorFor(stringIndex, context.Snapshot.StringCount);
        // Young sparks burn white-hot, cooling into the string colour.
        var heat = MathF.Max(0f, 1f - t * 3f);
        var r = (byte)(sc.R + (255 - sc.R) * heat);
        var g = (byte)(sc.G + (255 - sc.G) * heat);
        var b = (byte)(sc.B + (255 - sc.B) * heat);

        var seed = HitSparkMath.Seed(timeMs, stringIndex, fret);
        var normal = new Vector3(0, 0, -1);
        for (var s = 0; s < HitSparkMath.SparksPerHit; s++)
        {
            var spark = HitSparkMath.Evaluate(seed, s, t);
            var color = Color.FromArgb((byte)(220 * spark.Alpha), r, g, b);
            var cx = x + spark.Dx;
            var cy = y + spark.Dy;
            var cz = spark.Dz;
            var h = spark.HalfSize;
            context.QuadBatcher.AddQuad(
                new Vector3(cx - h, cy + h, cz),
                new Vector3(cx + h, cy + h, cz),
                new Vector3(cx + h, cy - h, cz),
                new Vector3(cx - h, cy - h, cz),
                normal, color, 8.0f);
        }
    }
}
