using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Data;

namespace DotCharter.Rendering.Components;

/// <summary>
///     The 3D highway's show lights: concert stage lights. Drawn by
///     <c>Fretboard3DEngine</c> in the screen-space background pass, right after
///     the crowd gradient, so the whole 3D scene (board, notes) layers over it.
///     Thin on purpose: WHAT shows comes from the pure <see cref="StageLights" />
///     mapping, WHERE from <see cref="StageLightsShape" />; this only turns them
///     into vertex-coloured quads (shader mode 1, every edge faded to zero alpha so
///     nothing reads as a hard line, and mode 8 radial glow for the pools and lamp
///     heads). Nothing here reads the wall clock.
/// </summary>
public sealed class StageLightsRenderer
{
    private const int HazeColumns = 96;
    private const int ConeRows = 8;
    private const int LaserSegments = 6;

    // Cross-section samples of a cone, in half-widths from its axis.
    private static readonly float[] ConeXs = [-1.3f, -0.9f, -0.5f, -0.2f, 0f, 0.2f, 0.5f, 0.9f, 1.3f];

    // Haze rows, in σ from the band centre.
    private static readonly float[] HazeRows = [-2.8f, -1.6f, -0.8f, 0f, 0.8f, 1.6f, 2.8f];

    private static readonly Vector3 Normal = new(0, 0, -1);
    private const float Z = 1f; // → depth 0 under the background pass's ortho matrix

    private const float HazeAlpha = 0.42f;
    private const float WashAlpha = 0.05f;
    private const float PoolAlpha = 0.75f;
    private const float LaserAlpha = 0.9f;

    /// <summary>
    ///     Stages the lights. <paramref name="highway" /> is the highway's edges on
    ///     screen (the haze notches out over it);
    ///     <paramref name="horizonY" /> the screen row of the horizon, or null when
    ///     it is off-screen.
    /// </summary>
    public void Draw(QuadBatcher batcher, in StageLightsState s, double timeMs, int width, int height,
        in HighwaySpan highway, float? horizonY)
    {
        if (!s.Visible || width <= 0 || height <= 0) return;
        var tSec = timeMs / 1000.0;
        var hz = horizonY is { } hy && hy > 0 && hy < height ? hy / height : StageLightsShape.DefaultHorizonV;

        if (s.HazeLevel > 0)
        {
            DrawWash(batcher, s, width, height, hz);
            DrawHaze(batcher, s, tSec, width, height, hz, highway);
        }

        if (s.BeamLevel > 0)
        {
            for (var i = 0; i < StageLightsShape.Lamps; i++) DrawCone(batcher, s, i, tSec, width, height, hz);
        }

        if (s.Lasers > 0) DrawLasers(batcher, s, tSec, width, height, hz);
        DrawHeads(batcher, s, tSec, width, height);
    }

    private static void DrawWash(QuadBatcher batcher, in StageLightsState s, int width, int height, float hz)
    {
        // A faint fog-coloured lift over the whole sky, strongest at the horizon.
        var top = Rgba(s.Haze, WashAlpha * 0.4f * s.HazeLevel * s.Brightness);
        var mid = Rgba(s.Haze, WashAlpha * s.HazeLevel * s.Brightness);
        var clear = Rgba(s.Haze, 0f);
        float y0 = 0, y1 = hz * height, y2 = Math.Min(1f, hz + 0.03f) * height;
        batcher.AddQuad(new Vector3(0, y0, Z), new Vector3(width, y0, Z), new Vector3(width, y1, Z),
            new Vector3(0, y1, Z), Normal, top, top, mid, mid);
        batcher.AddQuad(new Vector3(0, y1, Z), new Vector3(width, y1, Z), new Vector3(width, y2, Z),
            new Vector3(0, y2, Z), Normal, mid, mid, clear, clear);
    }

    private static void DrawHaze(QuadBatcher batcher, in StageLightsState s, double tSec, int width, int height,
        float hz, in HighwaySpan highway)
    {
        var centreV = hz + StageLightsShape.HazeOffset;
        var gain = HazeAlpha * s.HazeLevel * s.Brightness;
        for (var c = 0; c < HazeColumns; c++)
        {
            float u0 = c / (float)HazeColumns, u1 = (c + 1) / (float)HazeColumns;
            var k0 = gain * StageLightsShape.HazeNoise(u0, tSec);
            var k1 = gain * StageLightsShape.HazeNoise(u1, tSec);
            for (var r = 0; r < HazeRows.Length - 1; r++)
            {
                var va = centreV + HazeRows[r] * StageLightsShape.HazeSigmaFor(HazeRows[r]);
                var vb = centreV + HazeRows[r + 1] * StageLightsShape.HazeSigmaFor(HazeRows[r + 1]);
                var pa = r == 0 ? 0f : StageLightsShape.HazeProfile(va, hz);
                var pb = r + 1 == HazeRows.Length - 1 ? 0f : StageLightsShape.HazeProfile(vb, hz);
                var (la, ra) = highway.At(va);
                var (lb, rb) = highway.At(vb);
                batcher.AddQuad(
                    new Vector3(u0 * width, va * height, Z), new Vector3(u1 * width, va * height, Z),
                    new Vector3(u1 * width, vb * height, Z), new Vector3(u0 * width, vb * height, Z), Normal,
                    Rgba(s.Haze, k0 * pa * StageLightsShape.HighwayNotch(u0, la, ra)),
                    Rgba(s.Haze, k1 * pa * StageLightsShape.HighwayNotch(u1, la, ra)),
                    Rgba(s.Haze, k1 * pb * StageLightsShape.HighwayNotch(u1, lb, rb)),
                    Rgba(s.Haze, k0 * pb * StageLightsShape.HighwayNotch(u0, lb, rb)));
            }
        }
    }

    private static void DrawCone(QuadBatcher batcher, in StageLightsState s, int lamp, double tSec, int width,
        int height, float hz)
    {
        var breathe = StageLightsShape.Breathing(lamp, tSec);
        var level = s.BeamLevel * s.BeamGain * breathe;
        var headU = StageLightsShape.HeadU(lamp);
        var poolU = StageLightsShape.PoolU(lamp, tSec);
        var poolV = StageLightsShape.PoolV(hz);

        for (var r = 0; r < ConeRows; r++)
        {
            float s0 = r / (float)ConeRows, s1 = (r + 1) / (float)ConeRows;
            var (x0, y0, hw0) = ConeRow(s0);
            var (x1, y1, hw1) = ConeRow(s1);
            var a0 = level * StageLightsShape.ConeAlong(s0);
            var a1 = level * StageLightsShape.ConeAlong(s1);
            for (var c = 0; c < ConeXs.Length - 1; c++)
            {
                var xa = ConeXs[c];
                var xb = ConeXs[c + 1];
                var pa = StageLightsShape.ConeAcross(xa);
                var pb = StageLightsShape.ConeAcross(xb);
                batcher.AddQuad(
                    new Vector3((x0 + xa * hw0) * width, y0 * height, Z),
                    new Vector3((x0 + xb * hw0) * width, y0 * height, Z),
                    new Vector3((x1 + xb * hw1) * width, y1 * height, Z),
                    new Vector3((x1 + xa * hw1) * width, y1 * height, Z), Normal,
                    Rgba(s.Beam, a0 * pa), Rgba(s.Beam, a0 * pb), Rgba(s.Beam, a1 * pb), Rgba(s.Beam, a1 * pa));
            }
        }

        // The pool: a wide soft ellipse and a tighter brighter core.
        var px = poolU * width;
        var py = poolV * height;
        var hw = StageLightsShape.ConeFloorHalfWidth * width;
        Glow(batcher, px, py, hw * 2.2f, height * 0.075f, Rgba(s.Beam, PoolAlpha * 0.7f * level));
        Glow(batcher, px, py, hw * 1.4f, height * 0.04f, Rgba(s.Beam, PoolAlpha * level));
        return;

        (float X, float Y, float HalfWidth) ConeRow(float t) => (
            headU + (poolU - headU) * t,
            StageLightsShape.HeadV + (poolV - StageLightsShape.HeadV) * t,
            StageLightsShape.ConeTopHalfWidth +
            (StageLightsShape.ConeFloorHalfWidth - StageLightsShape.ConeTopHalfWidth) * t);
    }

    private static void DrawLasers(QuadBatcher batcher, in StageLightsState s, double tSec, int width, int height,
        float hz)
    {
        var aspect = width / (float)height;
        var px = height / 900f;
        for (var side = 0; side < 2; side++)
        {
            for (var b = 0; b < StageLightsShape.FanBeams; b++)
            {
                var (from, to) = StageLightsShape.LaserLine(side, b, tSec, hz, aspect);
                var f = new Vector2(from.X * width, from.Y * height);
                var t = new Vector2(to.X * width, to.Y * height);
                var d = Vector2.Normalize(t - f);
                var n = new Vector2(-d.Y, d.X);
                for (var g = 0; g < LaserSegments; g++)
                {
                    float s0 = g / (float)LaserSegments, s1 = (g + 1) / (float)LaserSegments;
                    var p0 = Vector2.Lerp(f, t, s0);
                    var p1 = Vector2.Lerp(f, t, s1);
                    var a0 = LaserAlpha * s.Lasers * s.Brightness * StageLightsShape.LaserFade(s0);
                    var a1 = LaserAlpha * s.Lasers * s.Brightness * StageLightsShape.LaserFade(s1);
                    SoftLine(batcher, p0, p1, n, 9f * px, s.Laser, a0 * 0.45f, a1 * 0.45f);
                    SoftLine(batcher, p0, p1, n, 2.4f * px, Lighten(s.Laser), a0, a1);
                }
            }
        }
    }

    private static void DrawHeads(QuadBatcher batcher, in StageLightsState s, double tSec, int width, int height)
    {
        var px = height / 900f;
        var on = s.BeamLevel;
        for (var i = 0; i < StageLightsShape.Lamps; i++)
        {
            var x = StageLightsShape.HeadU(i) * width;
            var y = StageLightsShape.HeadV * height;
            var level = (0.4f + 0.6f * on * StageLightsShape.Breathing(i, tSec)) *
                        (on > 0 ? s.BeamGain : s.Brightness);
            var halo = 26f * px * (1f + 0.15f * s.Plus);
            Glow(batcher, x, y, halo, halo, Rgba(s.Beam, Math.Min(1f, 0.85f * level)));
            Glow(batcher, x, y, 8f * px, 8f * px, Rgba(Lighten(s.Beam), Math.Min(1f, level)));
        }
    }

    private static void SoftLine(QuadBatcher batcher, Vector2 p0, Vector2 p1, Vector2 n, float halfWidth,
        Color colour, float a0, float a1)
    {
        // Two strips, opaque along the axis and clear at both edges.
        var c0 = Rgba(colour, a0);
        var c1 = Rgba(colour, a1);
        var clear = Rgba(colour, 0f);
        var o = n * halfWidth;
        batcher.AddQuad(new Vector3(p0 - o, Z), new Vector3(p1 - o, Z), new Vector3(p1, Z), new Vector3(p0, Z),
            Normal, clear, clear, c1, c0);
        batcher.AddQuad(new Vector3(p0, Z), new Vector3(p1, Z), new Vector3(p1 + o, Z), new Vector3(p0 + o, Z),
            Normal, c0, c1, clear, clear);
    }

    private static void Glow(QuadBatcher batcher, float x, float y, float rx, float ry, Color c) =>
        batcher.AddQuad(new Vector3(x - rx, y - ry, Z), new Vector3(x + rx, y - ry, Z),
            new Vector3(x + rx, y + ry, Z), new Vector3(x - rx, y + ry, Z), Normal, c, 8.0f);

    private static Color Lighten(Color c) => StageLights.Mix(c, Colors.White, 0.6f);

    private static Color Rgba(Color c, float a) =>
        Color.FromArgb((byte)(Math.Clamp(a, 0f, 1f) * 255f), c.R, c.G, c.B);
}
