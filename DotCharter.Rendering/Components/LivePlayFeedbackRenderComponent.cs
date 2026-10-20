using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

/// <summary>
/// Rocksmith-style Live Play feedback markers. Judged sounds arrive through
/// the frame snapshot (drained from the Live Play verdict queue by the
/// canvas) and are drawn as transient effects at the note's lane position:
/// an expanding glow flash for hits (orange-tinted when the technique was
/// wrong) and a red X that fades while drifting down for misses. The
/// component keeps its own marker list across frames — each snapshot carries
/// only NEWLY judged sounds — and the per-event sequence id makes a
/// re-rendered snapshot (failed staging update) harmless.
/// </summary>
public class LivePlayFeedbackRenderComponent : IRenderable
{
    private const double HitLifetimeMs = 400.0;
    private const double MissLifetimeMs = 600.0;
    // Soft round sprite via the existing radial-glow shader path
    // (same quad type the note-hit explosion particles use).
    private const float RadialGlowType = 8.0f;

    private const byte VerdictHitWrongTechnique = 1;
    private const byte VerdictMiss = 2;

    private const byte DirectionNone = 0;

    private readonly List<Marker> _markers = [];
    private long _lastSeq;

    private struct Marker
    {
        public double SpawnMs;   // judged-at transport time; drives the animation
        public float X;
        public float Y;
        public float Z;          // signed early/late depth nudge; 0 when untimed
        public float HalfWidth;  // horizontal half-extent (anchor span for open notes/chords)
        public float HalfHeight;
        public byte Verdict;
        public bool IsChord;
        public Color Tint;
    }

    /// <summary>How far a maximally early/late hit's flash is nudged in depth.</summary>
    public const float MaxTimingNudgeZ = 0.6f;

    /// <summary>The delta at which the nudge saturates (ms).</summary>
    public const float TimingNudgeSaturationMs = 150f;

    /// <summary>
    ///     Where a hit's flash lands in depth: the note travels toward the
    ///     player, so a hit struck EARLY happened while the note was still out
    ///     there (+z) and a LATE one after it had gone past (−z). Deliberately
    ///     NOT the physically exact <c>GetZDepthForTime</c> — the late hit
    ///     window is 250 ms, which at the real time→Z scale is −5 world units,
    ///     i.e. behind the camera (base distance −2.5). So the signed delta is
    ///     saturated and mapped into ±<see cref="MaxTimingNudgeZ"/>: it still
    ///     conveys magnitude, but can never leave the board.
    ///     Returns 0 for <c>direction == None</c>, where <c>JudgedAtMs</c> is a
    ///     window expiry rather than a real detection.
    /// </summary>
    public static float TimingNudgeZ(byte direction, double noteTimeMs, double judgedAtMs)
    {
        if (direction == DirectionNone) return 0f;
        var deltaMs = judgedAtMs - noteTimeMs; // positive = late
        var clamped = Math.Clamp(deltaMs, -TimingNudgeSaturationMs, TimingNudgeSaturationMs);
        return (float)(-clamped / TimingNudgeSaturationMs) * MaxTimingNudgeZ;
    }

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context)
    {
        var snap = context.Snapshot;
        if (!snap.LivePlayActive)
        {
            _markers.Clear();
            return;
        }

        for (var i = 0; i < snap.LivePlayFeedbackCount; i++)
        {
            var ev = snap.LivePlayFeedback[i];
            if (ev.Seq <= _lastSeq) continue;
            _lastSeq = ev.Seq;
            _markers.Add(CreateMarker(context, ev));
        }

        var nowMs = context.TimeMs;
        for (var i = _markers.Count - 1; i >= 0; i--)
        {
            var lifetime = _markers[i].Verdict == VerdictMiss ? MissLifetimeMs : HitLifetimeMs;
            if (nowMs - _markers[i].SpawnMs > lifetime || nowMs < _markers[i].SpawnMs)
                _markers.RemoveAt(i);
        }

        for (var i = 0; i < _markers.Count; i++)
        {
            var marker = _markers[i];
            if (marker.Verdict == VerdictMiss) DrawMiss(context, marker, nowMs);
            else DrawHit(context, marker, nowMs);
        }
    }

    private static Marker CreateMarker(RenderContext context, in RenderLivePlayFeedback ev)
    {
        var snap = context.Snapshot;
        var stringCount = snap.StringCount;

        float y;
        float halfHeight;
        if (ev.IsChord)
        {
            // Chords flash across the middle of the string stack.
            y = FretboardConstants.BottomStringPosition +
                (stringCount - 1) * FretboardConstants.StringDistance * 0.5f;
            halfHeight = (stringCount - 1) * FretboardConstants.StringDistance * 0.5f +
                         FretboardConstants.StringDistance;
        }
        else
        {
            var stringIndex = Math.Clamp(ev.StringIndex, 0, stringCount - 1);
            y = FretboardConstants.BottomStringPosition +
                FretboardConstants.StringLaneIndex(stringIndex, stringCount, snap.InvertStrings) *
                FretboardConstants.StringDistance;
            halfHeight = FretboardConstants.StringDistance * 1.3f;
        }

        float x;
        float halfWidth;
        // `IsOpenFret`, not `Fret <= 0`: under a capo the judged note is drawn
        // as the anchor-wide open bar, so its verdict marker belongs there too.
        if (ev.IsChord || NoteRenderMath.IsOpenFret(context, ev.Fret))
        {
            // Open notes and chords span the active FHP/anchor range, same
            // as the open-note bar and the chord box.
            var (x0, x1) = NoteRenderMath.GetAnchorSpan(context, ev.NoteTimeMs);
            x = (x0 + x1) * 0.5f;
            halfWidth = MathF.Max(FretboardConstants.NoteHalfWidth, (x1 - x0) * 0.5f);
        }
        else
        {
            x = FretboardConstants.GetLinearFretMiddle(ev.Fret);
            halfWidth = FretboardConstants.NoteHalfWidth;
        }

        Color tint;
        if (ev.Verdict == VerdictHitWrongTechnique)
            tint = Color.FromArgb(255, 255, 170, 0); // pitch right, technique wrong
        else if (ev.IsChord || ev.StringIndex < 0)
            tint = Color.FromArgb(255, 240, 248, 255);
        else
            tint = FretboardConstants.StringColorFor(ev.StringIndex, context.Snapshot.StringCount);

        return new Marker
        {
            SpawnMs = ev.JudgedAtMs,
            X = x, Y = y,
            Z = TimingNudgeZ(ev.Direction, ev.NoteTimeMs, ev.JudgedAtMs),
            HalfWidth = halfWidth, HalfHeight = halfHeight,
            Verdict = ev.Verdict,
            IsChord = ev.IsChord,
            Tint = tint
        };
    }

    private static void DrawHit(RenderContext context, in Marker m, double nowMs)
    {
        var t = (float)((nowMs - m.SpawnMs) / HitLifetimeMs);
        if (t is < 0f or > 1f) return;
        var fade = 1f - t;

        // Expanding halo plus a shrinking bright core reads as a hit flash. The
        // flash sits where the note WAS when the player struck it — ahead of the
        // strike plane if they rushed, behind it if they dragged.
        var haloScale = 0.55f + 1.05f * t;
        var halo = Color.FromArgb((byte)(200 * fade), m.Tint.R, m.Tint.G, m.Tint.B);
        DrawGlowQuad(context, m.X, m.Y, m.Z, m.HalfWidth * haloScale, m.HalfHeight * haloScale, halo);

        var coreFade = fade * fade;
        var core = Color.FromArgb((byte)(255 * coreFade),
            (byte)((m.Tint.R + 255) / 2), (byte)((m.Tint.G + 255) / 2), (byte)((m.Tint.B + 255) / 2));
        DrawGlowQuad(context, m.X, m.Y, m.Z, m.HalfWidth * 0.55f * (1f - 0.3f * t),
            m.HalfHeight * 0.55f * (1f - 0.3f * t), core);
    }

    private static void DrawMiss(RenderContext context, in Marker m, double nowMs)
    {
        var t = (float)((nowMs - m.SpawnMs) / MissLifetimeMs);
        if (t is < 0f or > 1f) return;

        var color = Color.FromArgb((byte)(230 * (1f - t)), 255, 48, 48);
        var cy = m.Y - t * 0.25f; // fade while drifting slightly down
        var s = m.IsChord ? 0.5f : 0.16f;
        var thickness = m.IsChord ? 0.1f : 0.06f;
        const float z = 0f;

        // Two crossing diagonal bars with the thickness applied along X —
        // the mute-X quad recipe from ChordRenderer.
        context.QuadBatcher.AddQuad(new Vector3(m.X - s, cy - s, z), new Vector3(m.X + s, cy + s, z),
            new Vector3(m.X + s - thickness, cy + s, z), new Vector3(m.X - s - thickness, cy - s, z),
            Vector3.UnitZ, color);
        context.QuadBatcher.AddQuad(new Vector3(m.X - s, cy + s, z), new Vector3(m.X + s, cy - s, z),
            new Vector3(m.X + s - thickness, cy - s, z), new Vector3(m.X - s - thickness, cy + s, z),
            Vector3.UnitZ, color);
    }

    private static void DrawGlowQuad(RenderContext context, float cx, float cy, float z, float hw, float hh,
        Color color)
    {
        var n = new Vector3(0, 0, -1);
        context.QuadBatcher.AddQuad(
            new Vector3(cx - hw, cy + hh, z),
            new Vector3(cx + hw, cy + hh, z),
            new Vector3(cx + hw, cy - hh, z),
            new Vector3(cx - hw, cy - hh, z),
            n, color, RadialGlowType);
    }
}
