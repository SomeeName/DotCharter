using System.Runtime.CompilerServices;

namespace DotCharter.Services.Editing.Quality;

/// <summary>
/// The quality report's spare copy of a chart. A <see cref="QualityReportSnapshot" />
///     hands its copy back once its check has landed, and the next snapshot restores the live chart
///     INTO that copy with <see cref="ChartProjectRestorer" /> instead of cloning a new graph.
///     <para>It is the same copy, not a cheaper one: <see cref="ChartProjectCloner" /> is a restore
///     into an empty project, so both paths run the one field list there is
///     (<c>ChartProjectRestorerCoverageTests</c> walks it by reflection, onto a populated graph
///     too). What the spare saves is the allocation: the copy's notes, chords and collections
///     are reused.</para>
///     <para>Only a copy no check is reading is ever spare, so a check still running keeps its
///     own. While a report is <see cref="Open" /> the spare is held; once the last one closes it
///     is held weakly, so a closed report keeps no chart alive that the collector wants back, and a
///     collected spare only means the next copy is a clone.</para>
/// <para>The restore skips every arrangement the spare already holds: one filled from the
///     same live arrangement whose <see cref="Arrangement.GetRevision" /> has not moved since. Every
///     committed edit moves it, undo and redo included, so an arrangement nobody touched between
///     two checks is not copied again. A check only reads its copy, so the spare's arrangement is
///     still what was copied into it.</para>
/// </summary>
public sealed class QualityReportCopies
{
    private readonly Lock _gate = new();

    /// <summary>For each arrangement of a copy this hands out: the live arrangement it was filled from, and that one's revision then.</summary>
    private readonly ConditionalWeakTable<Arrangement, Filled> _filled = [];

    private ChartProject? _held;
    private WeakReference<ChartProject>? _loose;
    private int _open;

    /// <summary>A report is open until the result is disposed; meanwhile the spare is held strongly.</summary>
    public IDisposable Open()
    {
        lock (_gate)
        {
            _open++;
            if (_held == null && _loose != null && _loose.TryGetTarget(out var spare)) _held = spare;
            _loose = null;
        }

        return new Closing(this);
    }

    /// <summary>A copy of <paramref name="live" />: the spare restored when there is one, otherwise a clone.</summary>
    public ChartProject Copy(ChartProject live, out bool reused)
    {
        ArgumentNullException.ThrowIfNull(live);
        ChartProject? spare;
        lock (_gate)
        {
            spare = _held;
            if (spare == null) _loose?.TryGetTarget(out spare);
            _held = null;
            _loose = null;
        }

        reused = spare != null && !ReferenceEquals(spare, live);
        var revisions = new long[live.Arrangements.Count];
        for (var i = 0; i < revisions.Length; i++) revisions[i] = live.Arrangements[i].GetRevision();
        ChartProject copy;
        if (reused)
        {
            copy = spare!;
            ChartProjectRestorer.Restore(copy, live, IsCurrent);
        }
        else
        {
            copy = ChartProjectCloner.Clone(live);
        }

        for (var i = 0; i < revisions.Length; i++)
            _filled.AddOrUpdate(copy.Arrangements[i], new Filled(live.Arrangements[i], revisions[i]));
        return copy;
    }

    private bool IsCurrent(Arrangement copy, Arrangement live) =>
        _filled.TryGetValue(copy, out var filled) && filled.Live.TryGetTarget(out var from) && ReferenceEquals(from, live) &&
        filled.Revision == live.GetRevision();

    /// <summary>Makes <paramref name="copy" /> the spare. Call it only once nothing reads the copy any more.</summary>
    public void Return(ChartProject copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        lock (_gate)
        {
            if (_open > 0)
            {
                _held = copy;
                _loose = null;
            }
            else
            {
                _held = null;
                _loose = new WeakReference<ChartProject>(copy);
            }
        }
    }

    private void Close()
    {
        lock (_gate)
        {
            if (--_open > 0 || _held == null) return;
            _loose = new WeakReference<ChartProject>(_held);
            _held = null;
        }
    }

    /// <summary>Weak, so a spare that remembers a closed chart's arrangement does not keep that chart alive.</summary>
    private sealed class Filled(Arrangement live, long revision)
    {
        public WeakReference<Arrangement> Live { get; } = new(live);
        public long Revision { get; } = revision;
    }

    private sealed class Closing(QualityReportCopies owner) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) owner.Close();
        }
    }
}
