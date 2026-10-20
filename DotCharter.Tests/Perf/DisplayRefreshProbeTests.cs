using DotCharter.UI.Platform;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Regression guards for the display-refresh probe behind the 3D loop's
///     "fps cap 0 = the display's own rate" translation. The v0.32.5 audit
///     found two silent failure modes that pinned every uncapped user to the
///     60 Hz fallback: EnumDisplaySettingsW(null) returns FALSE on
///     multi-adapter Windows 11 (the probe now resolves the primary monitor's
///     device name explicitly), and the driver's TTL bookkeeping overflowed
///     so the probe never ran at all. Windows-only APIs, but the calls are
///     safe headless — a query failure degrades to the fallback.
///     <para>
///         <b>The fallback must be a SENTINEL, and that is the whole trick.</b>
///         Until 2026-08-29 these cases called the probe with its DEFAULT
///         fallback of 60 and asserted <c>InRange(hz, 24, 480)</c> — and 60 is
///         inside that band, so replacing the whole body of
///         <c>DisplayRefresh.ForWindowOrDefault</c> with <c>return fallback;</c>
///         (i.e. reintroducing the exact v0.32.5 regression this file exists to
///         catch) left every one of them GREEN. Passing a fallback OUTSIDE the
///         plausible band is what turns "the Win32 path really resolved a
///         device" into an assertion instead of a wish.
///     </para>
/// </summary>
public class DisplayRefreshProbeTests
{
    /// <summary>A value the probe can only return by failing to resolve a device.</summary>
    private const int Sentinel = -1;

    [Fact]
    public void Probe_with_no_window_reports_a_plausible_primary_rate()
    {
        var hz = DisplayRefresh.ForWindowOrDefault(0, Sentinel);

        Assert.False(hz == Sentinel,
            "the probe returned its fallback for the PRIMARY monitor — MonitorFromWindow / " +
            "GetMonitorInfoW / EnumDisplaySettingsW resolved nothing between them, which is the " +
            "v0.32.5 regression that pinned every uncapped user to the 60 Hz floor.");
        Assert.InRange(hz, 24, 480);
    }

    [Fact]
    public void Probe_is_stable_across_calls()
    {
        var first = DisplayRefresh.ForWindowOrDefault(0, Sentinel);
        var second = DisplayRefresh.ForWindowOrDefault(0, Sentinel);

        // Without the sentinel this case is satisfied by ANY constant, and the
        // fallback is the most stable constant there is.
        Assert.False(first == Sentinel, "the probe fell back — see the case above.");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Probe_with_bogus_hwnd_falls_back_gracefully()
    {
        // A nonsense handle must degrade to a plausible value (nearest-monitor
        // resolution or the fallback), never throw. The fallback IS the expected
        // answer here — that is the behaviour under test — so this case
        // deliberately keeps an in-band-agnostic assertion; the two above are
        // what prove the happy path actually reads the device.
        var hz = DisplayRefresh.ForWindowOrDefault(unchecked((nint)0xDEAD_BEEF), 42);
        Assert.True(hz is 42 or >= 24 and <= 480);
    }
}
