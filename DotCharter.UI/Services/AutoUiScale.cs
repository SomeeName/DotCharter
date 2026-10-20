namespace DotCharter.UI.Services;

/// <summary>
///     The <b>Auto</b> arm of the interface-scale setting: the factor derived
///     from the shell window's own client height.
///
///     <para>
///         The whole app is laid out in fixed logical pixels against a ~1080p
///         design, so on a 1440p or 4K monitor every surface is correct and
///         SMALL — the owner's Play Mode start screen occupied roughly
///         350 x 300 px in the middle of a 2560 x 1440 window.
///         Auto answers that without asking the user to discover a slider.
///     </para>
///
///     <para>
///         <b>Every number here is a policy decision, and each one exists for a
///         reason:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="BaselineHeightDip" /> is 1080 because that is the
///             height the fixed pixel sizes were authored against. Ratio, not
///             DPI: the input is the window's CLIENT height in DIPs, so a user
///             who already runs 150 % OS scaling sees ~960 DIPs and is
///             correctly left near 1.0 — the OS has already done the work, and
///             compounding the two is how a 4K laptop ends up at 300 %.
///         </item>
///         <item>
///             <see cref="MinScale" /> is 1.0, not
///             <c>UiScaleController.MinScale</c>. Auto may only ever GROW the
///             UI. A short window (a restored window on a 1080p monitor is
///             ~900 DIPs of client area) must not shrink chrome that was
///             designed at 1.0 — the manual arm still reaches 0.75 for anyone
///             who wants it.
///         </item>
///         <item>
///             <see cref="Step" /> is 0.05 so that a drag-resize re-lays out at
///             most a handful of times instead of once per delivered size. One
///             step is ~54 DIPs of window height.
///         </item>
///         <item>
///             <see cref="HysteresisMargin" /> is what stops a
///             maximise/restore/snap cycle from FLAPPING across a step
///             boundary. Quantisation alone does not: a window whose height
///             lands within a pixel of a boundary alternates on every size
///             delivery, and each alternation is a full shell re-layout. The
///             band is half a step plus this margin, so a full step (0.05)
///             still moves and a 0.044-or-less wobble does not.
///         </item>
///     </list>
///
///     <para>
///         Pure and static on purpose: the table below is the contract, and it
///         is asserted without a window by <c>AutoUiScaleTests</c>.
///     </para>
/// </summary>
public static class AutoUiScale
{
    /// <summary>The client height the app's fixed pixel sizes were authored
    /// against. A window this tall derives exactly 1.0.</summary>
    public const double BaselineHeightDip = 1080.0;

    /// <summary>Auto never shrinks the UI — see the type remarks.</summary>
    public const double MinScale = 1.0;

    /// <summary>Matches <see cref="UiScaleController.MaxScale" />; past this
    /// the shell does not fit a typical display.</summary>
    public const double MaxScale = 2.0;

    /// <summary>The quantisation grid. One step is ~54 DIPs of window height.</summary>
    public const double Step = 0.05;

    /// <summary>Added to half a step to form the stickiness band. A full step
    /// must still move, so this has to stay strictly below
    /// <c>Step / 2</c>.</summary>
    public const double HysteresisMargin = 0.02;

    /// <summary>The unquantised factor for a client height, clamped to
    /// <see cref="MinScale" />..<see cref="MaxScale" />. A non-finite or
    /// non-positive height answers 1.0 rather than throwing — this runs from a
    /// size-changed handler, and a window mid-teardown reports zeroes.</summary>
    public static double Raw(double clientHeightDip)
    {
        if (!double.IsFinite(clientHeightDip) || clientHeightDip <= 0) return 1.0;
        return Math.Clamp(clientHeightDip / BaselineHeightDip, MinScale, MaxScale);
    }

    /// <summary>Rounds to the nearest <see cref="Step" />, then re-clamps
    /// (rounding 1.98 up would leave 2.00, which is fine, but rounding must
    /// never be the thing that leaves the range).</summary>
    public static double Quantise(double scale)
    {
        if (!double.IsFinite(scale)) return 1.0;
        var snapped = Math.Round(scale / Step, MidpointRounding.AwayFromZero) * Step;
        // Round(…) * 0.05 lands on values like 1.3500000000000001; the second
        // round keeps the value printable and makes equality comparisons in
        // tests and in the hysteresis band exact.
        return Math.Clamp(Math.Round(snapped, 4), MinScale, MaxScale);
    }

    /// <summary>The quantised factor for a client height, with no hysteresis —
    /// the answer for a FIRST layout, where there is no previous value to be
    /// sticky about.</summary>
    public static double ForHeight(double clientHeightDip) => Quantise(Raw(clientHeightDip));

    /// <summary>
    ///     The factor to adopt for <paramref name="clientHeightDip" /> given
    ///     the one currently applied. Returns <paramref name="current" />
    ///     unchanged while the raw factor is still inside the stickiness band
    ///     around it, so the caller's "did it change?" test is also the
    ///     "should I re-lay-out?" test.
    /// </summary>
    public static double Next(double clientHeightDip, double current)
    {
        var raw = Raw(clientHeightDip);
        var target = Quantise(raw);
        if (!double.IsFinite(current)) return target;
        if (Math.Abs(target - current) < 1e-9) return current;

        // Hold the current value until the RAW factor has left its band. Testing
        // the raw value rather than the target is the point: two adjacent
        // targets are always a full step apart, so comparing targets could
        // never express "not far enough yet".
        return Math.Abs(raw - current) < Step / 2 + HysteresisMargin ? current : target;
    }
}
