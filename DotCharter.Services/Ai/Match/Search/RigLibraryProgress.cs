using System.Diagnostics;
using System.Globalization;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>
///     The library's status lines, in one place so the panel, the log and the
///     tests agree on them.
///     <para>
///         <b>Every line that describes a build says how far it is AND how long is
///         left.</b> "Building tone library: 1/4000 rigs rendered" is what the
///         owner saw — a true sentence, and one that reads as "this
///         will never finish" because nothing on it said 2.8 hours or 4 minutes.
///         The estimate is an average over the renders so far, so it is honest
///         after a handful of rows and blank before the first one.
///     </para>
/// </summary>
public static class RigLibraryProgress
{
    /// <summary>"Building tone library: 12/96 · ~3 min left". <paramref name="remaining" />
    /// null means "no estimate yet" and the clause is omitted.</summary>
    /// <param name="done">Rows rendered so far, INCLUDING the one just finished.</param>
    /// <param name="total">The rows this build is going to.</param>
    /// <param name="remaining">The estimate for the rows still to render.</param>
    public static string Building(int done, int total, TimeSpan? remaining)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"Building tone library: {done}/{total}");
        // No clause on the last row: "3/3 · ~1 s left" is a lie by rounding.
        return remaining is { } eta && done < total ? $"{line} · ~{Duration(eta)} left" : line;
    }

    /// <summary>
    ///     The inverse of <see cref="Building" />: reads the counts back out of a
    ///     build line, so a status sink that only sees STRINGS (the matcher's
    ///     <c>IProgress&lt;string&gt;</c> contract is additive-only) can still drive
    ///     a real progress bar. False for any other line.
    /// </summary>
    /// <param name="line">A status line.</param>
    /// <param name="done">Rows rendered so far.</param>
    /// <param name="total">The rows this build is going to.</param>
    public static bool TryParseBuilding(string? line, out int done, out int total)
    {
        done = 0;
        total = 0;
        const string prefix = "Building tone library: ";
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var rest = line.AsSpan(prefix.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;
        var end = rest[(slash + 1)..].IndexOf(' ');
        var totalSpan = end < 0 ? rest[(slash + 1)..] : rest[(slash + 1)..(slash + 1 + end)];

        if (!int.TryParse(rest[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            || !int.TryParse(totalSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
            || t <= 0 || d < 0 || d > t)
        {
            return false; // and the outs stay 0: a refused line must not leak half a parse
        }

        done = d;
        total = t;
        return true;
    }

    /// <summary>"Tone library: checking an older library (96 rigs)…" — the
    /// three verification renders before a v2 file's rows are adopted.</summary>
    /// <param name="rows">Rows the older file holds.</param>
    public static string CheckingLegacy(int rows) =>
        string.Create(CultureInfo.InvariantCulture, $"Tone library: checking an older library ({rows} rigs)…");

    /// <summary>"Tone library: 4000 rigs from cache".</summary>
    /// <param name="rows">The rows loaded.</param>
    public static string FromCache(int rows) =>
        string.Create(CultureInfo.InvariantCulture, $"Tone library: {rows} rigs from cache");

    /// <summary>"Tone library: 96/4000 rigs (growing in the background)" — what a
    /// match on a partial library is searching, said once so a weak early result
    /// is not read as the feature's ceiling.</summary>
    /// <param name="rows">Rows available to this match.</param>
    /// <param name="target">K.</param>
    public static string Partial(int rows, int target) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Tone library: {rows}/{target} rigs (growing in the background)");

    /// <summary>A duration the way a person says it: "45 s", "3 min", "1 h 20 min".
    /// Never seconds past a minute — an ETA to the second is false precision.</summary>
    /// <param name="span">The estimate.</param>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span < TimeSpan.FromMinutes(1))
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds))} s");
        if (span < TimeSpan.FromHours(1))
            return string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Ceiling(span.TotalMinutes)} min");
        var hours = (int)Math.Floor(span.TotalHours);
        var minutes = (int)Math.Round(span.TotalMinutes - hours * 60);
        if (minutes == 60) { hours++; minutes = 0; }
        return minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours} h")
            : string.Create(CultureInfo.InvariantCulture, $"{hours} h {minutes} min");
    }

    /// <summary>
    ///     A running average of seconds per render, for the ETA. An AVERAGE and not
    ///     an EMA: a render's cost varies with the rig (a reverb tank is dearer than
    ///     a gain stage), so a short memory would swing the estimate by minutes
    ///     between two consecutive rows.
    /// </summary>
    public sealed class Estimator
    {
        private readonly Stopwatch _clock;
        private readonly Func<TimeSpan>? _elapsed;
        private int _ticks;

        /// <summary>Wall-clock backed.</summary>
        public Estimator() => _clock = Stopwatch.StartNew();

        /// <summary>Test seam: an injected clock.</summary>
        /// <param name="elapsed">Time since the build started.</param>
        public Estimator(Func<TimeSpan> elapsed)
        {
            _clock = new Stopwatch();
            _elapsed = elapsed;
        }

        private TimeSpan Elapsed => _elapsed?.Invoke() ?? _clock.Elapsed;

        /// <summary>One render finished.</summary>
        public void Tick() => _ticks++;

        /// <summary>Seconds per render so far, or null before the first.</summary>
        public TimeSpan? PerRender => _ticks == 0 ? null : Elapsed / _ticks;

        /// <summary>How long <paramref name="rowsLeft" /> more renders will take at
        /// the rate so far; null before the first render.</summary>
        /// <param name="rowsLeft">Renders still to do.</param>
        public TimeSpan? Remaining(int rowsLeft) =>
            PerRender is { } per ? per * Math.Max(0, rowsLeft) : null;
    }
}
