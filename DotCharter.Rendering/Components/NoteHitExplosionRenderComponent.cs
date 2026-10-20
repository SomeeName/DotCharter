using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

/// <summary>
/// Spawns short-lived particle bursts when notes/chords pass the playhead.
/// Direct port of RsCharter's Preview3DGuitarSoundsDrawer.drawNoteHit:
/// 100 small particles per hit drifting outward over 500 ms, colour curve
/// yellow → red → fade. For open notes the particles are spawned at random
/// positions across the full FHP anchor width, so the burst covers the
/// whole open-note bar.
/// </summary>
public class NoteHitExplosionRenderComponent : IRenderable
{
    private const double LifetimeMs = 500.0;
    private const int ParticlesPerHit = 100;
    // Soft round point sprite via existing radial-glow shader path.
    private const float RadialGlowType = 8.0f;
    private const float ParticleHalfSize = 0.04f;

    private readonly List<Hit> _hits = [];
    private double _lastTimeMs;
    private bool _hasLastTime;

    private struct Hit
    {
        public double SpawnTimeMs;
        public float X;            // origin (anchor-min for open notes, fret-middle for fretted)
        public float Y;
        public float Z;
        public float Length;       // 0 for fretted, anchor width for open notes
        public int Seed;
    }

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context)
    {
        // Suppressed while Live Play runs: these bursts fire on every note the
        // playhead crosses regardless of player input, which would read as
        // false "hit" feedback — LivePlayFeedbackRenderComponent takes over.
        if (!context.Snapshot.EnableNoteHitExplosions || context.Snapshot.LivePlayActive)
        {
            _hits.Clear();
            return;
        }

        var nowMs = context.TimeMs;
        DetectNewHits(context, nowMs);

        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (nowMs - _hits[i].SpawnTimeMs > LifetimeMs || nowMs < _hits[i].SpawnTimeMs)
                _hits.RemoveAt(i);
        }

        if (_hits.Count == 0) return;
        for (var i = 0; i < _hits.Count; i++)
            DrawHit(context, _hits[i], nowMs);
    }

    private static void DrawSoftQuad(RenderContext context, float cx, float cy, float cz, Color color)
    {
        const float h = ParticleHalfSize;
        var n = new Vector3(0, 0, -1);
        context.QuadBatcher.AddQuad(
            new Vector3(cx - h, cy + h, cz),
            new Vector3(cx + h, cy + h, cz),
            new Vector3(cx + h, cy - h, cz),
            new Vector3(cx - h, cy - h, cz),
            n, color, color, color, color, RadialGlowType);
    }

    private static void DrawHit(RenderContext context, Hit hit, double nowMs)
    {
        var t = (float)((nowMs - hit.SpawnTimeMs) / LifetimeMs);
        if (t is < 0f or > 1f) return;

        // RsCharter colour curve: yellow → red → black, alpha fades together.
        //   r = (1 - t) * 255
        //   g = (1 - min(1, t*2)) * 255    (green burns out at half-life)
        //   b = 0
        //   a = (1 - t) * 255
        var rr = (byte)(255 * (1f - t));
        var gg = (byte)(255 * (1f - Math.Min(1f, t * 2f)));
        var aa = (byte)(255 * (1f - t));
        var col = Color.FromArgb(aa, rr, gg, 0);

        var rngState = unchecked((uint)(hit.Seed | 1));
        for (var p = 0; p < ParticlesPerHit; p++)
        {
            // Inline xorshift32 (allocation-free; replaces `new Random(seed)` per hit per frame
            // which was the dominant managed-heap allocator on the render thread and was the
            // root cause of audio dropouts during playback — heap grew at ~2.5 MB/s).
            rngState ^= rngState << 13; rngState ^= rngState >> 17; rngState ^= rngState << 5;
            var r0 = (rngState & 0xFFFFFFu) / (float)0x1000000;
            rngState ^= rngState << 13; rngState ^= rngState >> 17; rngState ^= rngState << 5;
            var r1 = (rngState & 0xFFFFFFu) / (float)0x1000000;
            rngState ^= rngState << 13; rngState ^= rngState >> 17; rngState ^= rngState << 5;
            var r2 = (rngState & 0xFFFFFFu) / (float)0x1000000;
            rngState ^= rngState << 13; rngState ^= rngState >> 17; rngState ^= rngState << 5;
            var r3 = (rngState & 0xFFFFFFu) / (float)0x1000000;

            var dx = (r0 - 0.5f) * 2f;
            var dy = r1;
            var dz = (r2 - 0.5f) * 0.5f;
            var spawnAlongBar = r3 * hit.Length;

            var px = hit.X + spawnAlongBar + dx * t;
            var py = hit.Y + dy * t - t * t;
            var pz = hit.Z + dz * t;

            DrawSoftQuad(context, px, py, pz, col);
        }
    }

    private void DetectNewHits(RenderContext context, double nowMs)
    {
        var snap = context.Snapshot;
        if (!_hasLastTime || nowMs < _lastTimeMs || nowMs - _lastTimeMs > 250.0)
        {
            _hasLastTime = true; _lastTimeMs = nowMs; return;
        }
        var lo = _lastTimeMs;
        var hi = nowMs;
        _lastTimeMs = nowMs;

        var stringCount = snap.StringCount;

        for (var i = 0; i < snap.NoteCount; i++)
        {
            var note = snap.Notes[i];
            if (note.Time <= lo || note.Time > hi) continue;
            if ((note.Flags & NoteFlags.LinkPrevious) != 0) continue;
            SpawnAt(context, note.Time, note.StringIndex, note.Fret, stringCount);
        }
        for (var i = 0; i < snap.ChordCount; i++)
        {
            var chord = snap.Chords[i];
            if (chord.Time <= lo || chord.Time > hi) continue;
            if (chord.NoteCount == 0)
            {
                SpawnAt(context, chord.Time, (byte)(stringCount / 2), 0, stringCount);
                continue;
            }
            for (var k = 0; k < chord.NoteCount; k++)
            {
                var cn = snap.ChordNotes[chord.NoteOffset + k];
                if ((cn.Flags & NoteFlags.LinkPrevious) != 0) continue;
                SpawnAt(context, chord.Time, cn.StringIndex, cn.Fret, stringCount);
            }
        }
    }

    private void SpawnAt(RenderContext context, double timeMs, byte stringIndex, byte fret, int stringCount)
    {
        var y = FretboardConstants.BottomStringPosition +
                FretboardConstants.StringLaneIndex(stringIndex, stringCount, context.Snapshot.InvertStrings) *
                FretboardConstants.StringDistance;
        var z = 0.0f;

        float originX;
        float length;
        // `IsOpenFret`, not `fret == 0`: a capoed note draws as the anchor-wide
        // open bar, so its particles must spawn across that bar.
        if (NoteRenderMath.IsOpenFret(context, fret))
        {
            // Open note: origin at the leftmost fret of the active FHP, length
            // spanning to the rightmost — particles spawn uniformly across the
            // whole open-note bar (RsCharter parity).
            var (x0, x1) = NoteRenderMath.GetAnchorSpan(context, timeMs);
            originX = x0;
            length = MathF.Max(0f, x1 - x0);
        }
        else
        {
            originX = FretboardConstants.GetLinearFretMiddle(fret);
            length = 0f;
        }

        _hits.Add(new Hit
        {
            SpawnTimeMs = timeMs,
            X = originX, Y = y, Z = z,
            Length = length,
            Seed = unchecked(((int)(timeMs * 1000) * 397) ^ (stringIndex * 31) ^ (fret * 17))
        });
    }
}

