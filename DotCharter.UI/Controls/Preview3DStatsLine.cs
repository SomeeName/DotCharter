using System.Globalization;
using DotCharter.Rendering.Diagnostics;

namespace DotCharter.UI.Controls;

/// <summary>
///     The 3D preview's one-line corner HUD.
///
///     <para>
///         <b>The line printed two measurements of two different things as if
///         they were one, in BOTH states, and the owner reported each of them
///         as a render regression.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Parked:</b> <c>10 fps · p50 7,0 / p99 7,1 ms</c>. The loop is
///             idle-gated — it parks and renders a ≤10 Hz safety-net trickle
///             (<see cref="Preview3DFrameGate.IdleTrickleMs" />) — and every
///             trickle frame comes out of a park, so the driver substitutes a
///             nominal delta and records NOTHING. The fps figure was live, the
///             percentiles were the last second the loop actually ran.
///         </item>
///         <item>
///             <b>Active:</b> <c>34 fps · p50 7,0 / p99 48,4 ms</c>, and 7 ms
///             at 34 fps is not arithmetic anyone can make work. The sample was
///             taken BEFORE <c>RenderFrame</c>, so a DECLINED attempt — the
///             composed present pump back-pressuring to the compositor's rate —
///             contributed a short interval to the percentiles and nothing to
///             the fps tally. p50 was the decline loop's spin rate.
///             <c>Preview3DRenderDriver._sincePresent</c> samples on the
///             success path now, so <c>fps ≈ 1000 / p50</c> holds by
///             construction and this type only has to SAY which second each
///             number came from.
///         </item>
///     </list>
///
///     <para>
///         It also prints why the loop is presenting at the rate it is, because
///         the cap alone does not explain it: 34 fps under a 63 fps cap is the
///         DECLINES, and a reader with only the cap in front of them has to
///         guess. Order is rate → interval → pacing → cost.
///     </para>
///
///     <para>
///         Pure and culture-explicit so <c>Preview3DStatsLineTests</c> can pin
///         the text without a GPU or a locale. The driver passes
///         <c>CurrentCulture</c>, which is why the owner's screenshots show a
///         decimal comma.
///     </para>
/// </summary>
internal static class Preview3DStatsLine
{
    /// <summary>Shown for a percentile that has no sample behind it at all —
    /// the first second after attach, before the loop has run a frame outside
    /// the trickle.</summary>
    internal const string NoSample = "—";

    /// <summary><c>DOTCHARTER_3D_HIDE_STATS=1</c> hides the line everywhere: the README
    /// captures (tools/readme-media) photograph the preview without it. Read once.</summary>
    internal static readonly bool HiddenByEnvironment =
        Environment.GetEnvironmentVariable("DOTCHARTER_3D_HIDE_STATS") == "1";

    /// <summary>Whether the corner line is drawn. It is a diagnostic, not game HUD: the editor's
    /// preview always shows it, an immersive run only while the detection monitor is open, and
    /// <paramref name="hidden" /> (<see cref="HiddenByEnvironment" />) turns it off in both.</summary>
    internal static bool Shown(bool gameplayPresentation, bool detectionMonitorOpen, bool hidden) =>
        !hidden && (!gameplayPresentation || detectionMonitorOpen);

    /// <summary>
    ///     <paramref name="framesLastSecond" /> is every frame the loop
    ///     PRESENTED in the 1 s window; <paramref name="idleFramesLastSecond" />
    ///     is how many of those were trickle frames that came out of an idle
    ///     park; <paramref name="declinedLastSecond" /> is how many attempts
    ///     produced no frame at all. Equal frame counts (with at least one
    ///     frame) mean the loop did nothing but trickle, which is the case that
    ///     has to say so.
    /// </summary>
    internal static string Format(
        int framesLastSecond,
        int idleFramesLastSecond,
        int idleHz,
        FrameTimeSummary stats,
        int effectiveMaxFps,
        int declinedLastSecond,
        double gpuSubmitMs,
        int drawCalls,
        int vertexCount,
        IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;

        var idle = framesLastSecond > 0 && idleFramesLastSecond >= framesLastSecond;
        var rate = idle
            ? string.Create(provider, $"idle {Math.Max(1, idleHz)} Hz")
            : string.Create(provider, $"{framesLastSecond} fps");

        // "frame", not a bare p50: the number is the interval BETWEEN PRESENTED
        // FRAMES, which is the only reading under which it agrees with the rate
        // beside it. Per-phase costs (snapshot, render, present) stay in the
        // [3DStats] log, where there is room to name each one.
        var timing = stats.Count > 0
            ? string.Create(provider, $"frame p50 {stats.P50Ms:F1} / p99 {stats.P99Ms:F1} ms")
            : $"frame p50 {NoSample} / p99 {NoSample} ms";
        if (idle && stats.Count > 0) timing = "last active " + timing;

        return string.Create(provider,
            $"{rate} · {timing} · {Pacing(idle, idleHz, effectiveMaxFps, declinedLastSecond, provider)}" +
            $" · gpu {gpuSubmitMs:F2} ms · {drawCalls} draws · {vertexCount} verts");
    }

    /// <summary>
    ///     Why the loop is presenting at that rate. A cap of 0 is the
    ///     uncapped/engine-driven case; declines are named whenever there were
    ///     any, because they — not the cap — are what holds a healthy composed
    ///     loop below its ceiling.
    /// </summary>
    private static string Pacing(
        bool idle, int idleHz, int effectiveMaxFps, int declinedLastSecond, IFormatProvider provider)
    {
        var reason = idle
            ? string.Create(provider, $"trickle {Math.Max(1, idleHz)} Hz")
            : effectiveMaxFps > 0
                ? string.Create(provider, $"cap {effectiveMaxFps}")
                : "uncapped";

        return declinedLastSecond > 0
            ? string.Create(provider, $"{reason} · {declinedLastSecond} declined")
            : reason;
    }
}
