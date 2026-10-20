using Avalonia.Media;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Rendering.Data;

/// <summary>
///     What the stage lights show at one song time. <c>Haze</c> is the fog
///     colour (horizon haze and ambient wash, <c>HazeLevel</c> 0 with no fog);
///     <c>Beam</c> colours the cones, floor pools and lamp heads, and
///     <c>BeamLevel</c> is 1 while beams are on, 0 while they are off (the heads
///     then idle dim and no cone is drawn); <c>Plus</c> is 1 for a BEAMS_*_PLUS
///     beam; <c>Lasers</c> 0..1 fades the laser fans, drawn in <c>Laser</c>.
///     Produced by <see cref="StageLights.Resolve(FrameSnapshot, double)" />; the
///     GPU side (<c>Components/StageLightsRenderer</c>) only turns it into quads.
/// </summary>
public readonly record struct StageLightsState(
    bool Visible,
    Color Haze,
    float HazeLevel,
    Color Beam,
    float BeamLevel,
    float Plus,
    Color Laser,
    float Lasers,
    float Brightness)
{
    /// <summary>Cone, pool and head gain: a plus beam is 35 % brighter.</summary>
    public float BeamGain => Brightness * (1f + StageLights.PlusBoost * Plus);
}

/// <summary>
///     The pure note/event → stage-lights mapping. Everything is a function of the
///     SONG time handed in — never the wall clock — so the lights freeze with the
///     transport and a scrub lands on the same picture every time.
/// </summary>
public static class StageLights
{
    /// <summary>A new event's colour takes over within this window, so the change
    /// point is visible while charting.</summary>
    public const double TransitionMs = 150;

    /// <summary>How much brighter a plus beam's cones, pools and heads are.</summary>
    public const float PlusBoost = 0.35f;

    /// <summary>Lasers with no beam colour to follow are drawn in this cyan.</summary>
    public static readonly Color DefaultLaser = Color.FromArgb(255, 0x2C, 0xE6, 0xFF);

    /// <summary>The lamp heads' colour while the beams are off: a cool grey.</summary>
    public static readonly Color IdleHead = Color.FromArgb(255, 0x9A, 0xA4, 0xB8);

    /// <summary>The resting room energy maps to brightness 1; a dead room dims the
    /// lights to 0.8, a cheering one lifts them to 1.3.</summary>
    public static float EnergyBrightness(float energy) => 0.8f + 0.5f * Math.Clamp(energy, 0f, 1f);

    public static StageLightsState Resolve(FrameSnapshot snap, double timeMs) => Resolve(
        snap.Showlights, snap.ShowlightCount, timeMs,
        snap.CrowdEnergy, snap.EnableShowlightFog, snap.EnableShowlightBeams);

    public static StageLightsState Resolve(
        RenderShowlight[] showlights, int showlightCount,
        double timeMs, float energy, bool fogEnabled, bool beamsEnabled)
    {
        var count = Math.Min(showlightCount, showlights.Length);
        var (fog, prevFog) = fogEnabled
            ? ResolveLane(showlights, count, RenderShowlightLane.Fog, timeMs)
            : (null, null);
        var (beam, prevBeam) = beamsEnabled
            ? ResolveLane(showlights, count, RenderShowlightLane.Beams, timeMs)
            : (null, null);
        var (laser, prevLaser) = beamsEnabled
            ? ResolveLane(showlights, count, RenderShowlightLane.Lasers, timeMs)
            : (null, null);

        // With the beams toggle off but fog on, the rig still hangs there: dim heads.
        if (fog is null && beam is null) return default;

        // Each lane fades from its previous event over TransitionMs. The first
        // event of a lane has nothing to fade from and shows at once.
        var fogW = Progress(fog, prevFog, timeMs);
        var beamW = Progress(beam, prevBeam, timeMs);
        var laserW = Progress(laser, prevLaser, timeMs);

        var haze = fog is { } f ? ShowlightPalette.FogColor(f.Note) : Colors.Black;
        var hazeFrom = prevFog is { } pf ? ShowlightPalette.FogColor(pf.Note) : haze;
        var hazeLevel = fog is null ? 0f : 1f;

        var prevB = prevBeam ?? beam;
        var beamColour = Mix(BeamColour(prevB), BeamColour(beam), beamW);
        var beamLevel = Lerp(IsBeamOn(prevB) ? 1f : 0f, IsBeamOn(beam) ? 1f : 0f, beamW);
        var plus = Lerp(IsPlus(prevB) ? 1f : 0f, IsPlus(beam) ? 1f : 0f, beamW);

        var laserTint = IsBeamOn(beam)
            ? Mix(ShowlightPalette.BeamColor(beam!.Value.Note), Colors.White, 0.25f)
            : DefaultLaser;
        var prevL = prevLaser ?? laser;
        var lasers = Lerp(IsLaserOn(prevL) ? 1f : 0f, IsLaserOn(laser) ? 1f : 0f, laserW);

        return new StageLightsState(true, Mix(hazeFrom, haze, fogW), hazeLevel, beamColour, beamLevel, plus,
            laserTint, lasers, EnergyBrightness(energy));
    }

    /// <summary>The latest event of <paramref name="lane" /> at or before
    /// <paramref name="timeMs" />, and the one before it. The projection is
    /// time-sorted (sticky pre-window entries first), so one forward walk does.</summary>
    public static (RenderShowlight? Current, RenderShowlight? Previous) ResolveLane(
        RenderShowlight[] showlights, int count, RenderShowlightLane lane, double timeMs)
    {
        RenderShowlight? current = null, previous = null;
        for (var i = 0; i < count; i++)
        {
            var sl = showlights[i];
            if (sl.Time > timeMs) break;
            if (sl.Lane != lane) continue;
            previous = current;
            current = sl;
        }

        return (current, previous);
    }

    /// <summary>0 → 1 over <see cref="TransitionMs" /> after the current event,
    /// smoothstepped; 1 when there is nothing to fade from.</summary>
    public static float Progress(RenderShowlight? current, RenderShowlight? previous, double timeMs)
    {
        if (current is null || previous is null) return 1f;
        var t = (float)Math.Clamp((timeMs - current.Value.Time) / TransitionMs, 0, 1);
        return t * t * (3f - 2f * t);
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static bool IsBeamOn(RenderShowlight? beam) =>
        beam is { } b && b.Note != ShowlightPalette.BeamsOff;

    private static bool IsPlus(RenderShowlight? beam) =>
        IsBeamOn(beam) && ShowlightPalette.IsBeamPlus(beam!.Value.Note);

    private static bool IsLaserOn(RenderShowlight? laser) =>
        laser is { Note: ShowlightPalette.LasersOn };

    private static Color BeamColour(RenderShowlight? beam) =>
        IsBeamOn(beam) ? ShowlightPalette.BeamColor(beam!.Value.Note) : IdleHead;
}
