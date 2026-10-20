namespace DotCharter.UI.Diagnostics;

/// <summary>
///     The startup phase timings <c>App</c> already logs as the machine-readable
///     <c>StartupPhases:</c> line, kept unformatted so the Session Health
///     dashboard can print them per phase and draw them as a stacked bar. Every
///     value is EXCLUSIVE (the phase's own cost) except the two marked
///     cumulative.
/// </summary>
/// <param name="DiMs">Container built (cumulative from process start).</param>
/// <param name="ServicesMs">Services resolved (cumulative).</param>
/// <param name="ViewModelMs">The view-model graph.</param>
/// <param name="KeybindsMs">Keybind registration.</param>
/// <param name="WindowCtorMs">The window constructor.</param>
/// <param name="BindMs">DataContext bind.</param>
/// <param name="OpenedMs">Window opened (cumulative).</param>
/// <param name="InteractiveMs">First interactive frame (cumulative).</param>
/// <param name="Precompiled">
///     <c>yes</c> / <c>no</c> / <c>?</c> from <see cref="ReadyToRunProbe" />.
///     <b>Every number above is unreadable without it</b>: a plain
///     <c>dotnet build -c Release</c> is not ReadyToRun and JIT-compiles its
///     whole startup path, which measures roughly twice the shipped build. A
///     phase read from an <c>r2r=no</c> run is a JIT measurement, not a product
///     one — so this travels WITH the phases and is never surfaced separately.
/// </param>
public readonly record struct StartupPhases(
    long DiMs,
    long ServicesMs,
    long ViewModelMs,
    long KeybindsMs,
    long WindowCtorMs,
    long BindMs,
    long OpenedMs,
    long InteractiveMs,
    string Precompiled)
{
    /// <summary>True only when the probe positively CONFIRMED a precompiled
    /// image. <c>?</c> counts as not confirmed — an unanswerable question about
    /// the numbers carries the same warning as a negative answer.</summary>
    public bool IsPrecompiled => string.Equals(Precompiled, "yes", StringComparison.Ordinal);

    /// <summary>
    ///     The phases as measured SEGMENTS in the order they run — what a stacked
    ///     bar draws. The last element is whatever is left of
    ///     <see cref="InteractiveMs" /> after the six named phases: real time the
    ///     user waited, and a bar that dropped it would claim a faster start than
    ///     happened.
    /// </summary>
    public IReadOnlyList<long> Segments
    {
        get
        {
            long[] named =
            [
                DiMs,
                Math.Max(0, ServicesMs - DiMs),
                ViewModelMs,
                KeybindsMs,
                WindowCtorMs,
                BindMs
            ];
            var accounted = 0L;
            foreach (var seg in named) accounted += seg;
            return [.. named, Math.Max(0, InteractiveMs - accounted)];
        }
    }
}

/// <inheritdoc cref="StartupPhases" />
public static class StartupDiagnostics
{
    /// <summary>Process start time, for an uptime readout.</summary>
    public static readonly DateTimeOffset ProcessStartUtc = DateTimeOffset.UtcNow;

    /// <summary>The formatted phase summary, or null until the window has opened.
    /// Kept as-is because the exported diagnostics bundle embeds it verbatim.</summary>
    public static string? PhaseSummary { get; private set; }

    /// <summary>The same numbers unformatted, for the dashboard's per-phase
    /// metrics and its stacked bar. Null until the window has opened.</summary>
    public static StartupPhases? Phases { get; private set; }

    public static void SetPhases(
        long diMs, long servicesMs, long viewModelMs, long keybindsMs, long windowCtorMs,
        long windowConstructedMs, long openedMs, long interactiveMs)
    {
        var precompiled = ReadyToRunProbe.Describe();

        Phases = new StartupPhases(
            diMs,
            servicesMs,
            viewModelMs - servicesMs,
            keybindsMs - viewModelMs,
            windowCtorMs - keybindsMs,
            windowConstructedMs - windowCtorMs,
            openedMs,
            interactiveMs,
            precompiled);

        PhaseSummary =
            $"DI {diMs} ms · services {servicesMs} ms · VM graph {viewModelMs - servicesMs} ms · " +
            $"keybinds {keybindsMs - viewModelMs} ms · window ctor {windowCtorMs - keybindsMs} ms · " +
            $"bind {windowConstructedMs - windowCtorMs} ms · " +
            $"opened {openedMs} ms · interactive {interactiveMs} ms · " +
            // Without this the numbers above are unreadable: a plain
            // `dotnet build -c Release` is not ReadyToRun and JITs its whole
            // startup path, which measures roughly twice the shipped build.
            $"precompiled {precompiled}";
    }
}
