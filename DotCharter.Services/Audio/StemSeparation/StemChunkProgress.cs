using static System.FormattableString;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
///     Optional second face of the <see cref="IProgress{T}" /> a caller hands
///     <see cref="IStemSeparationService.SeparateAsync" />: a backend that separates in
///     chunks tells it "chunk i of N" before the matching fraction, so the task toast can
/// say how far a slow run has got instead of looking stuck.
/// </summary>
public interface IStemChunkProgress
{
    /// <summary>Chunk <paramref name="chunk" /> (1-based) of <paramref name="total" /> is done.</summary>
    void ReportChunk(int chunk, int total);
}

/// <summary>
///     Turns the separator's per-chunk 0..1 fraction (one report per finished chunk,
///     <c>(k + 1) / N</c>) into chunk numbers, timings and log lines, and forwards both
///     to the caller. Reports arrive synchronously on the inference thread — unlike a
///     <see cref="Progress{T}" /> made off the UI thread, which posts each report to
///     the thread pool, where they can land out of order and late.
///     <para>
///         <b>A chunk's time is the gap between two reports.</b> The split-complex
///         BS-RoFormer pipeline overlaps chunk k's host stage with chunk k+1's model
///         run, so the gap is the model stage's time per chunk whenever the model is
///         the slower stage — it was 17.4 of 17.6 s on a fast run and
///         351.3 of 351.5 s on a slow one. That makes "every chunk slow" and "stuck on
///         one chunk" two different log patterns.
///     </para>
/// </summary>
public sealed class StemChunkProgressReporter : IProgress<double>
{
    private readonly Func<TimeSpan> _clock;
    private readonly Action<double> _overall;
    private readonly IStemChunkProgress? _chunks;
    private readonly Action<string> _log;
    private readonly int _summaryEvery;
    private readonly TimeSpan _start;
    private TimeSpan _lastTick;
    private TimeSpan _windowSlowest;
    private TimeSpan _windowTotal;
    private int _windowSlowestChunk;
    private int _windowFirstChunk = 1;
    private int _total;
    private int _last;

    /// <param name="clock">Monotonic time; read once at construction and once per finished chunk.</param>
    /// <param name="overall">Receives every fraction, unchanged (the caller maps it into its own range).</param>
    /// <param name="chunks">Receives "chunk i of N" before the fraction that carries it; may be null.</param>
    /// <param name="log">One English line per chunk, plus a summary every <paramref name="summaryEvery" /> chunks and at the last.</param>
    /// <param name="summaryEvery">Chunks per summary line.</param>
    public StemChunkProgressReporter(
        Func<TimeSpan> clock, Action<double> overall, IStemChunkProgress? chunks, Action<string> log, int summaryEvery = 10)
    {
        _clock = clock;
        _overall = overall;
        _chunks = chunks;
        _log = log;
        _summaryEvery = Math.Max(1, summaryEvery);
        _start = clock();
        _lastTick = _start;
    }

    /// <summary>The chunk count, read off the first report; 0 before it.</summary>
    public int Total => _total;

    /// <summary>The last chunk reported done; 0 before the first.</summary>
    public int Completed => _last;

    public void Report(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            _overall(Math.Max(0, double.IsNaN(value) ? 0 : value));
            return;
        }

        var fraction = Math.Min(1.0, value);
        if (_total == 0) _total = Math.Max(1, (int)Math.Round(1.0 / fraction));
        var chunk = Math.Clamp((int)Math.Round(fraction * _total), 1, _total);
        if (chunk > _last) OnChunksDone(chunk);
        _overall(fraction);
    }

    private void OnChunksDone(int chunk)
    {
        var now = _clock();
        var span = now - _lastTick;
        var done = chunk - _last;
        _lastTick = now;
        _last = chunk;

        var perChunk = span / done;
        _windowTotal += span;
        if (perChunk > _windowSlowest)
        {
            _windowSlowest = perChunk;
            _windowSlowestChunk = chunk;
        }

        var elapsed = now - _start;
        var together = done > 1 ? Invariant($" (average over {done} chunks reported together)") : "";
        _log(Invariant($"Stem separation: chunk {chunk}/{_total} took {perChunk.TotalMilliseconds:0} ms{together}, ") +
             Invariant($"elapsed {elapsed.TotalSeconds:0.0} s."));

        _chunks?.ReportChunk(chunk, _total);

        if (chunk % _summaryEvery != 0 && chunk != _total) return;
        var windowChunks = chunk - _windowFirstChunk + 1;
        var remaining = _total - chunk;
        var eta = remaining == 0 ? TimeSpan.Zero : elapsed / chunk * remaining;
        var tail = remaining == 0 ? "." : Invariant($", ~{eta.TotalSeconds:0} s to go at this rate.");
        _log(Invariant($"Stem separation: chunks {_windowFirstChunk}-{chunk} of {_total}: ") +
             Invariant($"{(_windowTotal / windowChunks).TotalMilliseconds:0} ms each on average, slowest ") +
             Invariant($"{_windowSlowest.TotalMilliseconds:0} ms (chunk {_windowSlowestChunk}); elapsed {elapsed.TotalSeconds:0.0} s") +
             tail);
        _windowFirstChunk = chunk + 1;
        _windowTotal = TimeSpan.Zero;
        _windowSlowest = TimeSpan.Zero;
        _windowSlowestChunk = 0;
    }
}

/// <summary>
///     The progress the stem-separation task toast is fed: the fraction moves the bar,
///     and once a backend reports chunks the label says "chunk i of N".
/// </summary>
public sealed class StemSeparationToastProgress(Action<double, string?> report, Func<int, int, string> chunkLabel)
    : IProgress<double>, IStemChunkProgress
{
    private string? _label;

    public void ReportChunk(int chunk, int total) => _label = chunkLabel(chunk, total);

    public void Report(double value) => report(value, _label);
}
