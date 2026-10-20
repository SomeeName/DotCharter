namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
///     The geometry behind the cabinet mic placement (<c>RigMicStage</c>,
///     <c>RigMicSideView</c>): where on the speaker a capsule sits, how far from the
///     grille, and what a player hears there. It writes the REAL mic model — the
///     native <c>micpos</c> unit's <c>position</c> (0 dust cap … 1 cone edge) and
///     <c>distanceCm</c> (0 … 30) — never a filter dressed up as a mic.
///     Pure, so every mapping is pinned in the default gate.
/// </summary>
public static class MicPlacementMath
{
    /// <summary>The farthest the native model places a capsule, in cm.</summary>
    public const double MaxDistanceCm = 30.0;

    /// <summary>The dust cap's radius as a fraction of the cone's.</summary>
    public const double DustCapFraction = 0.30;

    /// <summary>
    ///     A press at (<paramref name="dx" />, <paramref name="dy" />) relative to the
    ///     speaker's centre, in units of the cone's radius: the capsule's POSITION is
    ///     its distance from the centre (0 on the dust cap's centre, 1 at the cone's
    ///     edge), and its BEARING — the angle around the cone, which a round speaker
    ///     cannot hear — is where the press was, so the mic stays under the pointer.
    ///     A press past the edge clamps to it.
    /// </summary>
    public static (double Position, double BearingDegrees) Place(double dx, double dy)
    {
        var r = Math.Sqrt(dx * dx + dy * dy);
        var bearing = r < 1e-6 ? DefaultBearingDegrees : NormaliseDegrees(Math.Atan2(dx, -dy) * 180.0 / Math.PI);
        return (Math.Clamp(r, 0.0, 1.0), bearing);
    }

    /// <summary>Where a capsule at <paramref name="position" /> and
    /// <paramref name="bearingDegrees" /> is drawn, relative to the centre in cone
    /// radii (y down).</summary>
    public static (double X, double Y) Point(double position, double bearingDegrees)
    {
        var a = bearingDegrees * Math.PI / 180.0;
        var r = Math.Clamp(position, 0.0, 1.0);
        return (r * Math.Sin(a), -r * Math.Cos(a));
    }

    /// <summary>The bearing a fresh mic takes: up and to the right, the way an
    /// engineer angles a mic stand in from the side.</summary>
    public const double DefaultBearingDegrees = 60.0;

    /// <summary>The side view: a press at <paramref name="fraction" /> of the way
    /// from the grille (0) to the far end of the ruler (1) is that distance.</summary>
    public static double DistanceForFraction(double fraction) => Math.Clamp(fraction, 0.0, 1.0) * MaxDistanceCm;

    /// <summary>Inverse of <see cref="DistanceForFraction" />.</summary>
    public static double FractionForDistance(double distanceCm) => Math.Clamp(distanceCm / MaxDistanceCm, 0.0, 1.0);

    /// <summary>How big the capsule is drawn at a distance: right on the grille it
    /// is full size, at 30 cm it has fallen back to 60 %. The front view's only
    /// depth cue besides the shadow.</summary>
    public static double PerspectiveScale(double distanceCm) => 1.0 - 0.4 * FractionForDistance(distanceCm);

    /// <summary>How far the capsule's shadow falls from it, as a fraction of its
    /// size: touching the grille it has none.</summary>
    public static double ShadowOffset(double distanceCm) => 0.05 + 0.35 * FractionForDistance(distanceCm);

    /// <summary>
    ///     The zone a position is in, as a player names it: 0 on the dust cap (the
    ///     brightest, most aggressive), 1 where the cap meets the cone (the classic
    ///     spot), 2 mid-cone (balanced) and 3 toward the edge (the darkest).
    /// </summary>
    public static int Zone(double position) => position switch
    {
        < 0.18 => 0,
        < 0.40 => 1,
        < 0.72 => 2,
        _ => 3
    };

    /// <summary>How close the capsule is, as a player names it: 0 right on the
    /// grille (proximity bass), 1 a few centimetres off, 2 back (floor bounce).</summary>
    public static int DistanceZone(double distanceCm) => distanceCm switch
    {
        < 2.5 => 0,
        < 12.0 => 1,
        _ => 2
    };

    /// <summary>Keyboard nudge: arrows move along the radius (±0.05, ±0.01 fine).</summary>
    public static double Nudge(double position, double delta) => Math.Clamp(position + delta, 0.0, 1.0);

    private static double NormaliseDegrees(double degrees)
    {
        var d = degrees % 360.0;
        return d < 0 ? d + 360.0 : d;
    }
}
