using System.Runtime;

namespace DotCharter.UI.Diagnostics;

/// <summary>
///     Holds garbage collection off the UI thread for the whole boot path —
///     DI build, view-model graph, window construction, first layout — and
///     releases it once the window is interactive.
///     <para>
///         <b>Why (sampled on the R2R release build).</b>
///         Of <c>MainWindow.InitializeComponent</c>'s 785 ms, <b>209 ms</b> was
///         <c>Thread.PollGC</c> — the UI thread suspended for gen0/gen1
///         collections while the compiled shell markup allocated its ~196
///         elements, styles and bindings — and the deferred-view construction
///         that follows first paint carried another <b>202 ms</b> of the same.
///         The tree work itself (<c>SetLogicalParent</c>, <c>ApplyStyling</c>)
///         is markup size and stays; the GC pauses are a budget problem, and
///         <see cref="GC.TryStartNoGCRegion(long)" /> is the runtime's own answer
///         to "do not collect during this window".
///     </para>
///     <para>
///         Contract: <see cref="Begin" /> never throws and never blocks; if the
///         runtime declines a budget it tries smaller ones, then gives up (the
///         boot runs as before). <see cref="End" /> is idempotent and tolerates
///         a region the runtime already ended because the budget was exceeded
///         (<c>EndNoGCRegion</c> throws for that; it is caught and REPORTED as
///         <c>blown</c>, so the log says whether the hold actually held).
///         <c>DOTCHARTER_NO_STARTUP_GC_HOLD=1</c> turns it off for an A/B.
///     </para>
/// </summary>
public static class StartupGcHold
{
    /// <summary>Budgets tried in order; the runtime rejects one it cannot
    /// commit up front (workstation GC caps the region by segment size).</summary>
    public static readonly long[] Budgets = [240L << 20, 128L << 20, 64L << 20];

    private static readonly object Gate = new();
    private static bool _active;
    private static int _gen0, _gen1, _gen2;
    private static string _outcome = "off";

    /// <summary>What the last hold did: <c>held</c>, <c>blown</c> (a GC ran
    /// anyway — budget exceeded), <c>unavailable</c> (the runtime declined
    /// every budget), or <c>off</c> (disabled / never begun).</summary>
    public static string Outcome => _outcome;

    /// <summary>Collections counted since <see cref="Begin" />, filled in by
    /// <see cref="End" />; all zero while a hold holds.</summary>
    public static (int Gen0, int Gen1, int Gen2) Collections { get; private set; }

    public static bool IsDisabledByEnvironment =>
        Environment.GetEnvironmentVariable("DOTCHARTER_NO_STARTUP_GC_HOLD") == "1";

    /// <summary>Opens the region. Returns true when the runtime accepted a
    /// budget.</summary>
    public static bool Begin()
    {
        if (IsDisabledByEnvironment) { _outcome = "off"; return false; }
        lock (Gate)
        {
            if (_active) return true;
            _gen0 = GC.CollectionCount(0);
            _gen1 = GC.CollectionCount(1);
            _gen2 = GC.CollectionCount(2);
            foreach (var budget in Budgets)
            {
                try
                {
                    if (GC.TryStartNoGCRegion(budget))
                    {
                        _active = true;
                        _outcome = "held";
                        return true;
                    }
                }
                catch (ArgumentOutOfRangeException) { /* too large for this GC — try the next */ }
                catch (InvalidOperationException) { break; /* a region is already in progress elsewhere */ }
            }
            _outcome = "unavailable";
            return false;
        }
    }

    /// <summary>Closes the region and records what happened. Safe to call
    /// more than once and without a matching <see cref="Begin" />.</summary>
    public static void End()
    {
        lock (Gate)
        {
            Collections = (GC.CollectionCount(0) - _gen0, GC.CollectionCount(1) - _gen1, GC.CollectionCount(2) - _gen2);
            if (!_active) return;
            _active = false;
            try
            {
                if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
                else _outcome = "blown";
            }
            catch (InvalidOperationException)
            {
                // The runtime ended it itself: the budget was exceeded and a
                // collection ran. Not a fault — a measurement.
                _outcome = "blown";
            }
        }
    }

    /// <summary>The key=value tail for the <c>StartupPhases:</c> line.</summary>
    public static string Describe() =>
        $"gc={_outcome} gc0={Collections.Gen0} gc1={Collections.Gen1} gc2={Collections.Gen2}";
}
