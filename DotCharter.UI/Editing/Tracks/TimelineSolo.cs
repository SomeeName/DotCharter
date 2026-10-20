namespace DotCharter.UI.Editing.Tracks;

/// <summary>
///     The Tracks popover's SOLO, as a pair of pure verbs over the two
///     per-arrangement visibility services.
/// </summary>
/// <remarks>
///     <para>
///         `Play Menus.dc.html` draws Solo / Mute / Settings… under the
///         arrangement picker. <b>Solo here is a TIMELINE solo</b>: on, nothing
///         but the arrangement being edited is drawn. It is deliberately not an
///         audio solo — an <c>Arrangement</c> carries no volume, mute, solo or
///         bus anywhere in this app, so a chip claiming to silence one would be
///         a capability the app cannot honour, looking live.
///     </para>
///     <para>
///         <b>The load-bearing half is that it is REVERSIBLE.</b> Turning solo
///         off has to put back exactly the ghosts and stacked rows it hid, not
///         "restore" to nothing — so engaging returns the snapshot and releasing
///         consumes it. Split out of <c>MultiTrackOverlayViewModel</c> so that
///         contract can be tested in the default gate: the view model takes an
///         18-member editing coordinator it does not need for this.
///     </para>
/// </remarks>
internal static class TimelineSolo
{
    /// <summary>
    ///     Hides every ghost and stacked overlay and returns what was showing,
    ///     for <see cref="Release" />.
    /// </summary>
    public static (List<Arrangement> Ghosts, List<Arrangement> Stacked) Engage(
        IGhostArrangementsService ghosts, IStackedArrangementsService stacked)
    {
        // Snapshot BEFORE clearing: both services expose live collections, so a
        // reference taken here would be emptied along with them.
        var hiddenGhosts = new List<Arrangement>(ghosts.VisibleGhosts);
        var hiddenStacks = new List<Arrangement>(stacked.StackedArrangements);

        ghosts.Clear();
        stacked.Clear();
        return (hiddenGhosts, hiddenStacks);
    }

    /// <summary>Puts back exactly what <see cref="Engage" /> hid.</summary>
    public static void Release(
        IGhostArrangementsService ghosts, IStackedArrangementsService stacked,
        IReadOnlyList<Arrangement> hiddenGhosts, IReadOnlyList<Arrangement> hiddenStacks)
    {
        foreach (var a in hiddenGhosts) ghosts.SetVisible(a, true);
        foreach (var a in hiddenStacks) stacked.SetStacked(a, true);
    }
}
