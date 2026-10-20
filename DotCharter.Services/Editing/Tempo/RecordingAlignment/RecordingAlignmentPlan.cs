using DotCharter.Analysis.BeatMap;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Editing.Tempo.RecordingAlignment;

public enum RecordingAlignmentRangeKind
{
    WholeSong,
    FromBar,
    TimeSpan
}

/// <summary>Which beats an align-to-recording run may move: all of them, those from a bar on, or those
///     inside a time span (the editor's loop region). Beats outside stay where they are.</summary>
public sealed record RecordingAlignmentRange(RecordingAlignmentRangeKind Kind, int FromBar = 1, double StartMs = 0, double EndMs = 0)
{
    public static RecordingAlignmentRange WholeSong { get; } = new(RecordingAlignmentRangeKind.WholeSong);

    public static RecordingAlignmentRange FromBarNumber(int bar) => new(RecordingAlignmentRangeKind.FromBar, Math.Max(1, bar));

    public static RecordingAlignmentRange Span(double startMs, double endMs) =>
        new(RecordingAlignmentRangeKind.TimeSpan, StartMs: Math.Min(startMs, endMs), EndMs: Math.Max(startMs, endMs));
}

/// <summary>One section the confidence is reported for: its beats [<paramref name="StartBeat" />,
///     <paramref name="EndBeat" />), its 1-based bars and its name in the chart ("" when unnamed).</summary>
public sealed record RecordingAlignmentSectionSpan(string Name, int StartBeat, int EndBeat, int FirstBar, int LastBar);

/// <summary>
/// The chart side of an align-to-recording run, read from the project ON THE CALLING
///     THREAD so the analysis can run on a worker without touching the live graph: the beat times,
///     which beats are fixed (outside the range, or anchored by hand when those are kept), every
///     sound the chart expects across all instrumental arrangements, and the sections the
///     confidence is reported per. Pure.
/// </summary>
public sealed class RecordingAlignmentPlan
{
    /// <summary>Bars per reported section when the chart has no section markers.</summary>
    public const int FallbackSectionBars = 8;

    private RecordingAlignmentPlan(double[] oldTimes, AlignmentBeat[] beats, IReadOnlyList<AlignmentEvent> events,
        IReadOnlyList<RecordingAlignmentSectionSpan> sections, int anchoredInRange)
    {
        OldTimes = oldTimes;
        Beats = beats;
        Events = events;
        Sections = sections;
        AnchoredInRange = anchoredInRange;
    }

    public double[] OldTimes { get; }

    public AlignmentBeat[] Beats { get; }

    public IReadOnlyList<AlignmentEvent> Events { get; }

    /// <summary>Every section of the chart, in order, covering every beat.</summary>
    public IReadOnlyList<RecordingAlignmentSectionSpan> Sections { get; }

    /// <summary>Anchored beats inside the range that were kept fixed.</summary>
    public int AnchoredInRange { get; }

    public int FreeBeats => Beats.Count(b => !b.IsFixed);

    public static RecordingAlignmentPlan Build(ChartProject project, RecordingAlignmentRange range, bool keepAnchors)
    {
        var source = project.Beats;
        var n = source.Count;
        var oldTimes = new double[n];
        var beats = new AlignmentBeat[n];
        var bars = Bars(source);
        var anchored = 0;
        for (var i = 0; i < n; i++)
        {
            oldTimes[i] = source[i].Time;
            var inRange = range.Kind switch
            {
                RecordingAlignmentRangeKind.FromBar => bars[i] >= range.FromBar,
                RecordingAlignmentRangeKind.TimeSpan => source[i].Time >= range.StartMs && source[i].Time <= range.EndMs,
                _ => true
            };
            var locked = keepAnchors && source[i].IsAnchor;
            if (inRange && locked) anchored++;
            beats[i] = new AlignmentBeat(source[i].Time, !inRange || locked);
        }

        return new RecordingAlignmentPlan(oldTimes, beats, n < 2 ? [] : ExpectedEvents(project), SectionSpans(project, bars), anchored);
    }

    /// <summary>The 1-based bar of every beat (<see cref="BarCount" />'s rule: pickups read as bar 1).</summary>
    public static int[] Bars(IList<BeatMarker> beats)
    {
        var bars = new int[beats.Count];
        var count = BarCount.Zero;
        for (var i = 0; i < beats.Count; i++)
        {
            count = count.Advance(beats[i].IsMeasure);
            bars[i] = count.Bar;
        }

        return bars;
    }

    /// <summary>
    ///     Every sound of every instrumental arrangement's highest level, merged by position: two
    ///     arrangements playing the same onset are one expected onset with both pitch sets. A full
    ///     mute keeps its onset and contributes no pitch.
    /// </summary>
    internal static List<AlignmentEvent> ExpectedEvents(ChartProject project)
    {
        var byPosition = new SortedDictionary<FractionalPosition, (List<int> Midi, double Sustain)>();
        foreach (var arrangement in project.Arrangements)
        {
            if (arrangement.ArrangementType == ArrangementType.Vocal || arrangement.Levels.Count == 0) continue;
            var level = arrangement.Levels[^1];
            foreach (var note in level.Notes)
                Add(byPosition, note.Position, note.Sustain, PitchOf(arrangement, note));
            foreach (var chord in level.Chords)
            {
                var midi = new List<int>();
                foreach (var cn in chord.ChordNotes)
                {
                    if (PitchOf(arrangement, cn) is { } m) midi.AddRange(m);
                }

                Add(byPosition, chord.Position, chord.GetMaxSustain(), midi.Count == 0 ? null : midi);
            }
        }

        var events = new List<AlignmentEvent>(byPosition.Count);
        foreach (var (position, value) in byPosition)
        {
            var beat = position.BeatId + position.Fraction.DoubleValue;
            if (beat < 0) continue;
            events.Add(new AlignmentEvent(beat, value.Sustain, value.Midi.Distinct().Order().ToArray()));
        }

        return events;
    }

    private static void Add(SortedDictionary<FractionalPosition, (List<int> Midi, double Sustain)> map,
        FractionalPosition position, FractionalPosition sustain, IEnumerable<int>? midi)
    {
        var length = sustain.BeatId + sustain.Fraction.DoubleValue;
        if (!map.TryGetValue(position, out var entry)) entry = ([], 0);
        if (midi != null) entry.Midi.AddRange(midi);
        map[position] = (entry.Midi, Math.Max(entry.Sustain, length));
    }

    private static int[]? PitchOf(Arrangement arrangement, IGuitarSound sound) =>
        sound.Mute == MuteType.Full || LivePitchAcceptance.SoundingMidi(arrangement, sound.StringIndex, sound.Fret) is not { } midi
            ? null
            : [midi];

    /// <summary>
    ///     The chart's own sections (the first instrumental arrangement that has any), or blocks of
    ///     <see cref="FallbackSectionBars" /> bars. A section starts on the beat its marker sits in.
    /// </summary>
    internal static List<RecordingAlignmentSectionSpan> SectionSpans(ChartProject project, int[] bars)
    {
        var n = bars.Length;
        var result = new List<RecordingAlignmentSectionSpan>();
        if (n == 0) return result;

        var starts = new List<(int Beat, string Name)>();
        var marked = project.Arrangements.FirstOrDefault(a =>
            a.ArrangementType != ArrangementType.Vocal && a.EventPoints.Any(e => !string.IsNullOrWhiteSpace(e.Section)));
        if (marked != null)
        {
            foreach (var point in marked.EventPoints.Where(e => !string.IsNullOrWhiteSpace(e.Section)).OrderBy(e => e.Position))
            {
                var beat = Math.Clamp(point.Position.BeatId, 0, n - 1);
                if (starts.Count > 0 && starts[^1].Beat == beat) starts[^1] = (beat, point.Section);
                else if (starts.Count == 0 || beat > starts[^1].Beat) starts.Add((beat, point.Section));
            }
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                if (i == 0 || (bars[i] != bars[i - 1] && (bars[i] - 1) % FallbackSectionBars == 0)) starts.Add((i, ""));
            }
        }

        if (starts.Count == 0 || starts[0].Beat != 0) starts.Insert(0, (0, ""));
        for (var s = 0; s < starts.Count; s++)
        {
            var end = s + 1 < starts.Count ? starts[s + 1].Beat : n;
            result.Add(new RecordingAlignmentSectionSpan(starts[s].Name, starts[s].Beat, end, bars[starts[s].Beat], bars[end - 1]));
        }

        return result;
    }
}
