using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

/// <summary>
///     0.14.2 C: enhanced note-hit feedback on top of the particle explosions —
///     a brighter two-layer hit flash (white-hot core inside a string-coloured
///     halo) at the strike line, and a soft string-coloured shimmer that keeps
///     pulsing at the strike point while a sustain rings. Entirely STATELESS:
///     both effects re-derive from the snapshot's visible notes each frame
///     (the 3D look-behind comfortably covers the flash lifetime), so seeks
///     and re-renders need no bookkeeping. Uses the existing radial-glow
///     vertex mode — no shader change. Suppressed during Live Play for the
///     same reason as the explosions: time-based effects would read as false
///     "hit" feedback (`LivePlayFeedbackRenderComponent` takes over).
/// </summary>
public class EnhancedHitFeedbackRenderComponent : IRenderable
{
    private const double FlashLifetimeMs = 160.0;
    private const double MinShimmerSustainMs = 150.0;
    private const float RadialGlowType = 8.0f;

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context)
    {
        var snap = context.Snapshot;
        if (!snap.EnableEnhancedHitFeedback || snap.LivePlayActive) return;

        var now = context.TimeMs;
        var stringCount = snap.StringCount;

        for (var i = 0; i < snap.NoteCount; i++)
        {
            var note = snap.Notes[i];
            DrawFeedback(context, note.Time, note.Sustain, note.StringIndex, note.Fret, note.Flags,
                stringCount, now);
        }

        for (var i = 0; i < snap.ChordCount; i++)
        {
            var chord = snap.Chords[i];
            for (var k = 0; k < chord.NoteCount; k++)
            {
                var cn = snap.ChordNotes[chord.NoteOffset + k];
                DrawFeedback(context, chord.Time, cn.Sustain, cn.StringIndex, cn.Fret, cn.Flags,
                    stringCount, now);
            }
        }
    }

    private static void DrawFeedback(RenderContext context, double timeMs, double sustainMs,
        byte stringIndex, byte fret, NoteFlags flags, int stringCount, double now)
    {
        var age = now - timeMs;
        if (age < 0) return;

        var flashing = age <= FlashLifetimeMs && (flags & NoteFlags.LinkPrevious) == 0;
        var shimmering = sustainMs >= MinShimmerSustainMs && age <= sustainMs;
        if (!flashing && !shimmering) return;

        var y = FretboardConstants.BottomStringPosition +
                FretboardConstants.StringLaneIndex(stringIndex, stringCount, context.Snapshot.InvertStrings) *
                FretboardConstants.StringDistance;

        float x, halfW;
        // `IsOpenFret`, not `fret == 0`: under a capo the note head itself is
        // drawn as the anchor-wide open bar, so the flash has to sit on it.
        if (NoteRenderMath.IsOpenFret(context, fret))
        {
            // Open note: span the active anchor zone like the open-note bar.
            var (x0, x1) = NoteRenderMath.GetAnchorSpan(context, timeMs);
            x = (x0 + x1) * 0.5f;
            halfW = MathF.Max(0.4f, (x1 - x0) * 0.5f);
        }
        else
        {
            x = FretboardConstants.GetLinearFretMiddle(fret);
            halfW = 0.55f;
        }

        var sc = FretboardConstants.StringColorFor(stringIndex, context.Snapshot.StringCount);

        if (flashing)
        {
            // Expanding halo in the string colour with a white-hot core that
            // burns out first — reads as a much punchier hit than the
            // particles alone.
            var t = (float)(age / FlashLifetimeMs);
            var fadeOut = 1f - t;
            var grow = 1f + 1.2f * t;

            var halo = Color.FromArgb((byte)(190 * fadeOut), sc.R, sc.G, sc.B);
            DrawRadial(context, x, y, halfW * grow, 0.42f * grow, halo);

            var coreFade = MathF.Max(0f, 1f - t * 1.8f);
            var core = Color.FromArgb((byte)(220 * coreFade), 255, 255, 255);
            DrawRadial(context, x, y, halfW * 0.45f * grow, 0.2f * grow, core);
        }

        if (shimmering)
        {
            // Slow sine shimmer, phase-offset per string so chords sparkle
            // instead of breathing in lockstep.
            var phase = (float)(now * 0.018) + stringIndex * 1.3f;
            var s = 0.5f + 0.5f * MathF.Sin(phase);

            var glow = Color.FromArgb((byte)(46 + 44 * s), sc.R, sc.G, sc.B);
            DrawRadial(context, x, y, halfW * (0.8f + 0.15f * s), 0.24f + 0.06f * s, glow);
        }
    }

    private static void DrawRadial(RenderContext context, float cx, float cy, float halfW, float halfH, Color color)
    {
        var normal = new Vector3(0, 0, -1);
        context.QuadBatcher.AddQuad(
            new Vector3(cx - halfW, cy + halfH, 0f),
            new Vector3(cx + halfW, cy + halfH, 0f),
            new Vector3(cx + halfW, cy - halfH, 0f),
            new Vector3(cx - halfW, cy - halfH, 0f),
            normal, color, RadialGlowType);
    }
}
