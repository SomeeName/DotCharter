namespace DotCharter.UI.Input;

/// <summary>
///     Which STRING a keyboard digit addresses, and whether the arrangement
///     being edited actually HAS that string.
///     <para>
///         Three keyboard families address a string by number — <c>Ctrl+1..9</c>
///         (the string-edit mask), bare <c>1..9</c> (the pen's note entry) and
///         the <c>StringUp</c> ceiling — and all three must be bounded by the lanes
///         on screen, not by anything else. Bounds that come from elsewhere fail like this:
///     </para>
///     <list type="bullet">
///         <item>
///             the mask and the pen bounded by
///             <c>StringEditMask.MaxStrings</c> (9, a property of the KEYBOARD:
///             there are nine digit keys) let, on a 4-string bass, a bare <c>6</c>
///             create a <c>GuitarNote</c> on string index 5 — a lane the
///             renderer does not draw, the exporters cannot express and the
///             pointer cannot reach (<c>ChartHitTester.GetLaneIndexAtY</c> is
///             bounded by the track's string count). A <c>Ctrl+6</c> would set a mask
///             bit with no lane header to draw it on, which then makes every
///             per-string verb a silent no-op — exactly the "invisible modal
///             state" the mask contract forbids;
///         </item>
///         <item>
///             <c>StringUp</c>'s ceiling is the active arrangement's lane count, not a
///             project-level one (<c>ChartEditor.CurrentChart</c> is the PROJECT-level
///             <c>ChartData</c>). In a project whose lead is a 6-string guitar and whose
///             second arrangement is a 4-string bass, a project-level ceiling would clamp
///             the bass to string 5.
///         </item>
///     </list>
///     <para>
///         <c>ChartData</c> has no tuning of its own (see the note on the type), so this is the one answer to
///         "how many lanes?" for every caller that needs a null-safe one:
///         <c>ChartRenderer</c>, <c>MinimapOverviewControl</c>,
///         <c>CommandCatalog</c>, <c>InspectorViewModel</c> and
///         <c>AppKeybindRegistrar</c>.
///     </para>
///     <para>
///         <b>The bound is the ACTIVE ARRANGEMENT's tuning length</b>, because
///         that is what the editor draws lanes for
///         (<c>TrackEditorContext.StringCount =&gt; Arrangement.Tuning.Length</c>)
///         and what the pointer is already limited to. Pure by construction — an
///         <c>Arrangement</c> in, an <c>int</c> out, no services — the same split
///         as <c>ChartPen</c> and <c>GlobalChordGate</c>.
///     </para>
/// </summary>
public static class StringDigitTargeting
{
    /// <summary>What to assume with no arrangement open. Six, matching
    /// <c>Arrangement.Tuning</c>'s own default, so an unloaded editor behaves
    /// exactly as it did.</summary>
    public const int FallbackStringCount = 6;

    /// <summary>
    ///     How many lanes the arrangement has. <c>Tuning.Length</c> rather than
    ///     <c>Arrangement.StringCount</c>: the two are independent fields and can
    ///     disagree, and it is the TUNING vector the renderer, the hit tester and
    ///     the lane headers all lay out from.
    /// </summary>
    public static int StringCountOf(Arrangement? arrangement)
    {
        var tuning = arrangement?.Tuning;
        return tuning is { Length: > 0 } ? tuning.Length : FallbackStringCount;
    }

    /// <summary>
    ///     THE question every digit-addressed string verb asks: does this
    ///     arrangement have that string? Refusing is a strict no-op — no history
    ///     node, no mask bit, no note — which is the only answer that cannot
    ///     produce state nothing on screen can show.
    /// </summary>
    public static bool IsAddressable(int stringIndex, int stringCount) =>
        stringIndex >= 0 && stringIndex < stringCount;

    /// <summary>Convenience over the two above, for the registrar's call sites.</summary>
    public static bool IsAddressable(int stringIndex, Arrangement? arrangement) =>
        IsAddressable(stringIndex, StringCountOf(arrangement));
}
