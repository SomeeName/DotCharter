namespace DotCharter.Core.Graphics;

/// <summary>
///     The highway's two viewing knobs, and everything derived from them. Pure
///     and unit-tested — every renderer reads its geometry through here rather
///     than a compile-time constant, which is what makes scroll speed
///     user-adjustable at all.
///
///     <para><b>Scroll speed is in Rocksmith units, where LOWER IS FASTER.</b>
///     This is counter-intuitive but it is the format's convention: a CDLC's
///     manifest stores <c>ScrollSpeed</c> (13 = 1.3), DLC Builder's own tooltip
///     reads "the lower the value, the faster the scroll speed", and honouring
///     it means an imported chart scrolls the way its charter authored it. The
///     value scales how many MILLISECONDS of music fit into the board's fixed
///     length: a low value packs less time into the same distance, so notes are
///     spread further apart and fly at the player faster.</para>
///
///     <para>The two knobs MULTIPLY into the time horizon
///     (<see cref="HorizonMs"/> = drawDistance × scrollSpeed × 50), so a long
///     board at a slow speed can demand many seconds of chart. Anything that
///     feeds the renderer must size itself from <see cref="HorizonMs"/> rather
///     than assume the historical 1750 ms, or notes simply will not exist when
///     they should already be on screen.</para>
/// </summary>
public static class HighwaySpeedPolicy
{
    /// <summary>Rocksmith's own default. A chart that carries no scroll speed
    /// is treated as this, so DotCharter and Rocksmith agree on an RS import.</summary>
    public const float DefaultScrollSpeed = 1.3f;

    /// <summary>Fastest (notes furthest apart).</summary>
    public const float MinScrollSpeed = 0.5f;

    /// <summary>Slowest (notes most tightly packed).</summary>
    public const float MaxScrollSpeed = 3.0f;

    /// <summary>The board's length in world units — how FAR the player can see.
    /// 35 is the historical hardcoded <c>FadeEndZ</c>.</summary>
    public const float DefaultDrawDistanceZ = 35f;

    public const float MinDrawDistanceZ = 20f;
    public const float MaxDrawDistanceZ = 80f;

    /// <summary>World units per millisecond at scroll speed 1.0. The historical
    /// <c>TimeToZScale</c>.</summary>
    public const float BaseTimeToZScale = 0.02f;

    /// <summary>The fog/fade-in ramp as a fraction of the board (historically
    /// FadeStartZ 20 / FadeEndZ 35). Kept PROPORTIONAL so that lengthening the
    /// board lengthens the ramp with it — a fixed 20-unit ramp on an 80-unit
    /// board would leave notes popping into existence far out.</summary>
    public const float FadeRampRatio = 20f / 35f;

    public static float ClampScrollSpeed(float scrollSpeed) =>
        float.IsFinite(scrollSpeed)
            ? Math.Clamp(scrollSpeed, MinScrollSpeed, MaxScrollSpeed)
            : DefaultScrollSpeed;

    public static float ClampDrawDistanceZ(float drawDistanceZ) =>
        float.IsFinite(drawDistanceZ)
            ? Math.Clamp(drawDistanceZ, MinDrawDistanceZ, MaxDrawDistanceZ)
            : DefaultDrawDistanceZ;

    /// <summary>World units per millisecond of chart. Note the DIVISION: a lower
    /// scroll speed yields a bigger scale, i.e. notes spread out and move fast.</summary>
    public static float TimeToZScale(float scrollSpeed) => BaseTimeToZScale / ClampScrollSpeed(scrollSpeed);

    /// <summary>How many milliseconds of chart are visible. This is the number
    /// that governs note density, and it was never named before — the renderers
    /// culled against a raw Z and a stale 2500 ms constant that disagreed with
    /// each other.</summary>
    public static float HorizonMs(float scrollSpeed, float drawDistanceZ) =>
        ClampDrawDistanceZ(drawDistanceZ) / TimeToZScale(scrollSpeed);

    /// <summary>Where the distance fade / depth fog begins.</summary>
    public static float FadeStartZ(float drawDistanceZ) => ClampDrawDistanceZ(drawDistanceZ) * FadeRampRatio;

    /// <summary>The widest horizon any setting combination can ask for — what a
    /// chart feed must be able to cover.</summary>
    public static float MaxHorizonMs => HorizonMs(MaxScrollSpeed, MaxDrawDistanceZ);

    /// <summary>
    ///     The single source of truth for which scroll speed a run actually uses.
    ///     Precedence, most specific first:
    ///     <list type="number">
    ///         <item><paramref name="perRun"/> — what the player typed on the pre-song
    ///               card for THIS playthrough only.</item>
    ///         <item><paramref name="globalOverride"/> — "always use my speed",
    ///               from Settings; null when the user has not enabled it.</item>
    ///         <item><paramref name="chartValue"/> — what the chart's author intended
    ///               (a CDLC's manifest <c>ScrollSpeed</c>).</item>
    ///         <item><see cref="DefaultScrollSpeed"/> — Rocksmith's own default, so a
    ///               chart that carries no value still agrees with the game.</item>
    ///     </list>
    /// </summary>
    public static float ResolveScrollSpeed(float? perRun, float? globalOverride, float? chartValue) =>
        ClampScrollSpeed(perRun ?? globalOverride ?? chartValue ?? DefaultScrollSpeed);
}
