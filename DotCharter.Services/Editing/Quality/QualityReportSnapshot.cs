using DotCharter.Services.Editing.Linting;

namespace DotCharter.Services.Editing.Quality;

/// <summary>
/// The quality report's copy of a chart, so its checks run off the UI thread, and the
///     way back to the live chart.
///     <list type="bullet">
///         <item><see cref="Take" />, on the UI thread: copies the chart with
///         <see cref="ChartProjectCloner" />, or restores it into the spare copy a landed check gave back
/// (<see cref="QualityReportCopies" />), and keys the audio outcomes by the copy's
///         arrangements.</item>
///         <item><see cref="Check" />, on any thread: builds the report over the copy and notes where
///         each arrangement, level and element a finding names sits in it. It reads the copy and
///         nothing else: the rules and the export gate keep no state between calls.</item>
///         <item><see cref="Rebind" />, on the UI thread: the same report naming the LIVE chart's
///         arrangements, levels and elements, matched by position, so Go to selects what is on
///         screen. An element whose live counterpart has gone or moved is dropped from its finding,
///         which keeps its time; a live chart whose arrangements or levels do not line up with
///         the copy gets no report at all.</item>
///         <item><see cref="Release" />, once the check has landed or been dropped: gives the copy back
///         to the <see cref="QualityReportCopies" /> it came from, for the next snapshot to reuse.</item>
///     </list>
/// </summary>
public sealed class QualityReportSnapshot
{
    private readonly ChartProject _live;
    private readonly QualityReportCopies? _copies;
    private bool _released;

    private QualityReportSnapshot(ChartProject live, ChartProject copy,
        IReadOnlyDictionary<Arrangement, AudioChartLintOutcome>? audio, QualityReportCopies? copies, bool reused)
    {
        _live = live;
        Copy = copy;
        Audio = audio;
        _copies = copies;
        Reused = reused;
    }

    /// <summary>The copy the checks read. Nothing else holds it until <see cref="Release" />.</summary>
    public ChartProject Copy { get; }

    /// <summary>True when the copy is the spare restored, false when it is a fresh clone.</summary>
    public bool Reused { get; }

    /// <summary>The audio check's outcomes, keyed by the copy's arrangements.</summary>
    public IReadOnlyDictionary<Arrangement, AudioChartLintOutcome>? Audio { get; }

    public static QualityReportSnapshot Take(ChartProject live, IReadOnlyDictionary<Arrangement, AudioChartLintOutcome>? audio,
        QualityReportCopies? copies = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        var reused = false;
        var copy = copies == null ? ChartProjectCloner.Clone(live) : copies.Copy(live, out reused);
        Dictionary<Arrangement, AudioChartLintOutcome>? copied = null;
        if (audio != null)
        {
            copied = new Dictionary<Arrangement, AudioChartLintOutcome>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < live.Arrangements.Count; i++)
            {
                if (audio.TryGetValue(live.Arrangements[i], out var outcome)) copied[copy.Arrangements[i]] = outcome;
            }
        }

        return new QualityReportSnapshot(live, copy, copied, copies, reused);
    }

    public QualityReportCheck Check(ChartQualityReporter reporter, double? audioLengthMs)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ObjectDisposedException.ThrowIf(_released, this);
        var report = reporter.Build(Copy, audioLengthMs, Audio);
        return new QualityReportCheck(report, Locate(report));
    }

    /// <summary>The report over the live chart, or null when the live chart's arrangements or levels do not match the copy's.</summary>
    public ChartQualityReport? Rebind(QualityReportCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        ObjectDisposedException.ThrowIf(_released, this);
        if (!StillLinesUp()) return null;
        var places = check.Places;
        var findings = check.Report.Findings.Select(f => f with
        {
            Arrangement = Live(f.Arrangement, places) as Arrangement,
            Level = Live(f.Level, places) as ChartLevel,
            Element = Live(f.Element, places),
            Rule = f.Rule == null ? null : f.Rule with
            {
                Note = Live(f.Rule.Note, places) as GuitarNote,
                Chord = Live(f.Rule.Chord, places) as Chord,
                HandShape = Live(f.Rule.HandShape, places) as HandShape,
                Anchor = Live(f.Rule.Anchor, places) as FretAnchor,
                HandShapes = f.Rule.HandShapes?.Select(s => Live(s, places)).OfType<HandShape>().ToList()
            }
        }).ToList();
        var arrangements = check.Report.Arrangements
            .Select(a => a with { Arrangement = _live.Arrangements[places[a.Arrangement].Arrangement] }).ToList();
        return new ChartQualityReport(findings, arrangements);
    }

    /// <summary>
    ///     Hands the copy back to be reused. Call it on the thread that rebinds, once the check over
    ///     the copy has finished and its report has been rebound or dropped: a rebound report names
    ///     only the live chart, so nothing holds the copy after. <see cref="Check" /> and
    ///     <see cref="Rebind" /> refuse after it; a second call does nothing.
    /// </summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        _copies?.Return(Copy);
    }

    private bool StillLinesUp()
    {
        if (_live.Arrangements.Count != Copy.Arrangements.Count) return false;
        for (var i = 0; i < Copy.Arrangements.Count; i++)
        {
            if (_live.Arrangements[i].Levels.Count != Copy.Arrangements[i].Levels.Count) return false;
        }

        return true;
    }

    private object? Live(object? copied, IReadOnlyDictionary<object, QualityPlace> places)
    {
        if (copied == null || !places.TryGetValue(copied, out var place)) return null;
        var arrangement = _live.Arrangements[place.Arrangement];
        if (place.Level < 0) return arrangement;
        var level = arrangement.Levels[place.Level];
        return copied switch
        {
            ChartLevel => level,
            GuitarNote note => At(level.Notes, place.Index) is { } live && live.Position == note.Position ? live : null,
            Chord chord => At(level.Chords, place.Index) is { } live && live.Position == chord.Position ? live : null,
            HandShape shape => At(level.HandShapes, place.Index) is { } live && live.Position == shape.Position ? live : null,
            FretAnchor anchor => At(level.Anchors, place.Index) is { } live && live.Position == anchor.Position ? live : null,
            _ => null
        };
    }

    private static T? At<T>(IList<T> list, int index) where T : class => index < list.Count ? list[index] : null;

    /// <summary>Where each arrangement, level and element the report names sits in the copy.</summary>
    private Dictionary<object, QualityPlace> Locate(ChartQualityReport report)
    {
        var wanted = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var finding in report.Findings)
        {
            Want(finding.Arrangement);
            Want(finding.Level);
            Want(finding.Element);
            if (finding.Rule is not { } rule) continue;
            Want(rule.Note);
            Want(rule.Chord);
            Want(rule.HandShape);
            Want(rule.Anchor);
            foreach (var shape in rule.HandShapes ?? []) Want(shape);
        }

        foreach (var summary in report.Arrangements) Want(summary.Arrangement);

        var places = new Dictionary<object, QualityPlace>(ReferenceEqualityComparer.Instance);
        for (var a = 0; a < Copy.Arrangements.Count; a++)
        {
            var arrangement = Copy.Arrangements[a];
            if (wanted.Contains(arrangement)) places[arrangement] = new QualityPlace(a, -1, -1);
            for (var l = 0; l < arrangement.Levels.Count; l++)
            {
                var level = arrangement.Levels[l];
                if (wanted.Contains(level)) places[level] = new QualityPlace(a, l, -1);
                Find(level.Notes, a, l);
                Find(level.Chords, a, l);
                Find(level.HandShapes, a, l);
                Find(level.Anchors, a, l);
            }
        }

        return places;

        void Want(object? item)
        {
            if (item != null) wanted.Add(item);
        }

        void Find<T>(IList<T> items, int a, int l) where T : class
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (wanted.Contains(items[i])) places[items[i]] = new QualityPlace(a, l, i);
            }
        }
    }
}

/// <summary>A report built over a <see cref="QualityReportSnapshot" />'s copy, waiting to be rebound to the live chart.</summary>
public sealed class QualityReportCheck
{
    internal QualityReportCheck(ChartQualityReport report, IReadOnlyDictionary<object, QualityPlace> places)
    {
        Report = report;
        Places = places;
    }

    /// <summary>The report as built: it names the copy's arrangements, levels and elements.</summary>
    public ChartQualityReport Report { get; }

    internal IReadOnlyDictionary<object, QualityPlace> Places { get; }
}

/// <summary>Arrangement, level and element index in the copy; -1 for "the level itself" or "the arrangement itself".</summary>
internal readonly record struct QualityPlace(int Arrangement, int Level, int Index);
