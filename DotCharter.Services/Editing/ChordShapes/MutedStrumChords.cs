using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.State;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>A planned "Muted strums → muted chord": the ONE undoable action, or null when there is nothing to do.</summary>
public sealed record MutedStrumChordsPlan(
    IChartAction? Action,
    int Converted,
    int MintedTemplates,
    int HandShapesAdded,
    int HandShapesExtended)
{
    public static readonly MutedStrumChordsPlan Nothing = new(null, 0, 0, 0, 0);

    public bool IsEmpty => Action is null;
}

/// <summary>
/// "Muted strums → muted chord". A Guitar Pro tab writes the muted strums after a chord as
///     ONE fret-hand-muted note on one of its strings; Rocksmith wants them as fret-hand-muted copies
///     of the chord inside the chord's hand shape, so they ship as repeat boxes. Pure — it builds ONE
///     composite action and commits nothing.
///     <para>
///         A RUN starts at a chord and walks forward one sound position at a time. It takes:
///         a group of single notes (no chord there) that are all in scope, all fret-hand muted
///         (<see cref="MuteType.Full" />), carry no per-string technique, and sit only on strings
///         the chord plays — each group becomes ONE chord on the chord's template; and a later
///         chord on the SAME grip (<see cref="ResolvedGripSource.Holds" />), which the bar spans
///         too. Anything else ends the run, as do the hand-shape planner's own breaks
///         (<see cref="ChordHandShapePlanner" />): a gap past <see cref="ChordHandShapePlanner.MaxMergeGap" />
///         after the previous sound's authored span, an anchor, another chord's bar, and a source
///         chord with a technique drawn along its sustain.
///     </para>
///     <para>
///         The copy points at the chord's template through <see cref="ResolvedGripSource.GripFor" />
///         — the chord's own template when it describes its frets, else the resolver in the chord's
///         own flavour, minted in the same node only when a strum actually converts. Every string
///         is fret-hand muted and carries the strum's accent, ignore and sustain uniformly (what the
///         chord tag can say); the strum's own string keeps its grace fret.
///     </para>
///     <para>
///         The bar: the chord's own (the one covering it) is extended, never shortened; with none, a
///         new bar on the grip starts at the chord. It ends where the planner ends a bar with
///         <c>CoverEveryChord</c>: the last sound's authored span, clipped at the next sound, the next
///         bar and the next anchor; a zero-length last strum reaches to the earlier of one beat and
///         that limit. So it never runs over another chord's bar.
///     </para>
/// </summary>
public static class MutedStrumChords
{
    public const string LabelKey = "History_MutedStrumsToChord";

    private static readonly FractionalPosition OneBeat = new(1);

    /// <summary>
    ///     Plans the conversion on <paramref name="level" /> of <paramref name="arrangement" />.
    ///     <paramref name="candidates" /> are the notes that may convert (the selection, or every
    ///     note of the phrase under the playhead); <paramref name="inSpan" /> says which positions a
    ///     run may extend its bar over an identical chord.
    /// </summary>
    public static MutedStrumChordsPlan Plan(
        Arrangement arrangement,
        ChartLevel level,
        IReadOnlySet<GuitarNote> candidates,
        Func<FractionalPosition, bool> inSpan)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(inSpan);
        if (candidates.Count == 0) return MutedStrumChordsPlan.Nothing;

        var notesAt = level.Notes.GroupBy(n => n.Position).ToDictionary(g => g.Key, g => g.ToList());
        var chordsAt = new Dictionary<FractionalPosition, Chord>();
        foreach (var chord in level.Chords) chordsAt.TryAdd(chord.Position, chord);
        var positions = notesAt.Keys.Concat(chordsAt.Keys).Distinct().Order().ToList();
        var anchors = level.Anchors.Select(a => a.Position).Order().ToList();
        var bars = level.HandShapes.OrderBy(h => h.Position).ToList();

        var grips = new ResolvedGripSource(arrangement);
        var minted = new List<ChordTemplate>();
        var runs = new List<Run>();
        Run? run = null;

        for (var i = 0; i < positions.Count; i++)
        {
            var position = positions[i];
            var chord = chordsAt.GetValueOrDefault(position);
            var notes = notesAt.GetValueOrDefault(position) ?? [];

            if (run != null && Continues(run, position, anchors, bars))
            {
                if (chord == null && IsStrum(notes, run.Strings, candidates))
                {
                    var strum = Copy(position, notes, run.Grip);
                    run.Strums.Add(strum);
                    run.Removed.AddRange(notes);
                    run.Advance(position, ChordHandShapePlanner.AuthoredSpan(strum), i);
                    continue;
                }

                if (chord != null && notes.Count == 0 && inSpan(position) && Opens(chord, notes) && grips.Holds(run.Grip, chord))
                {
                    run.Advance(position, ChordHandShapePlanner.AuthoredSpan(chord), i);
                    continue;
                }
            }

            Close(run, runs, minted);
            run = null;
            if (!Opens(chord, notes)) continue;
            var mark = minted.Count;
            run = new Run(chord!, grips.GripFor(chord!, minted), mark, bars.FirstOrDefault(b => b.Covers(chord!.Position)), i);
        }

        Close(run, runs, minted);
        if (runs.Count == 0) return MutedStrumChordsPlan.Nothing;

        var actions = new List<IChartAction>();
        if (minted.Count > 0) actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, minted));
        actions.Add(new RemoveNotesAction(level.Notes, runs.SelectMany(r => r.Removed)));
        actions.Add(new AddChordsAction(level.Chords, runs.SelectMany(r => r.Strums)));

        var added = new List<HandShape>();
        var extendTargets = new List<HandShape>();
        var extended = new List<HandShape>();
        foreach (var r in runs)
        {
            var end = BarEnd(r, positions, anchors, bars);
            if (r.Bar is { } bar)
            {
                if (end.CompareTo(bar.EndPosition) <= 0) continue;
                var longer = bar.Clone();
                longer.EndPosition = end;
                extendTargets.Add(bar);
                extended.Add(longer);
                continue;
            }

            added.Add(new HandShape
            {
                Position = r.First.Position,
                EndPosition = end,
                TemplateId = r.Grip.Id,
                Name = r.Grip.Name ?? string.Empty
            });
        }

        if (extendTargets.Count > 0)
        {
            actions.Add(new MutateHandShapesAction(level.HandShapes, extendTargets,
                extendTargets.Select(b => b.Clone()), extended));
        }

        if (added.Count > 0) actions.Add(new AddHandShapesAction(level.HandShapes, added));

        return new MutedStrumChordsPlan(new CompositeAction(actions) { LabelKey = LabelKey },
            runs.Sum(r => r.Strums.Count), minted.Count, added.Count, extendTargets.Count);
    }

    /// <summary>
    ///     Keeps a run that converted something. A run that converted nothing takes back the grip its
    ///     chord minted (runs are sequential, so those are the list's tail): a mint only ships with a strum.
    /// </summary>
    private static void Close(Run? run, List<Run> runs, List<ChordTemplate> minted)
    {
        if (run == null) return;
        if (run.Strums.Count > 0)
        {
            runs.Add(run);
            return;
        }

        minted.RemoveRange(run.MintedBefore, minted.Count - run.MintedBefore);
    }

    /// <summary>A chord a run may start at: it plays something and no technique moves the hand along its sustain.</summary>
    private static bool Opens(Chord? chord, List<GuitarNote> notes) =>
        chord != null && notes.Count == 0 && chord.ChordNotes.Count > 0
        && !ChordHandShapePlanner.HasSustainVisibleTechnique(chord);

    /// <summary>The planner's breaks between two sounds of one bar: the merge gap, an anchor, another bar.</summary>
    private static bool Continues(Run run, FractionalPosition position, List<FractionalPosition> anchors, List<HandShape> bars)
    {
        if (position.CompareTo(run.PreviousEnd + ChordHandShapePlanner.MaxMergeGap) > 0) return false;
        if (anchors.Any(a => a.CompareTo(run.Previous) > 0 && a.CompareTo(position) <= 0)) return false;
        return !bars.Any(b => !ReferenceEquals(b, run.Bar)
                              && (b.Covers(position) || (b.Position.CompareTo(run.First.Position) > 0 && b.Position.CompareTo(position) <= 0)));
    }

    /// <summary>
    ///     A muted strum the chord can stand for: every note in scope, fret-hand muted, on a string the
    ///     chord plays (one note per string), and carrying nothing a uniform chord tag cannot say.
    /// </summary>
    private static bool IsStrum(List<GuitarNote> notes, IReadOnlySet<int> strings, IReadOnlySet<GuitarNote> candidates) =>
        notes.Count > 0
        && notes.Select(n => n.StringIndex).Distinct().Count() == notes.Count
        && notes.All(n => candidates.Contains(n) && n.Mute == MuteType.Full && strings.Contains(n.StringIndex)
                          && !CarriesStringTechnique(n));

    private static bool CarriesStringTechnique(GuitarNote note) =>
        note.LinkNext || note.SlideTo.HasValue || note.UnpitchedSlide || note.Vibrato || note.Tremolo
        || note.Hopo != HopoType.None || note.Harmonic != HarmonicType.None
        || note.BassPicking != BassPickingType.None || note.BendValues.Count > 0;

    /// <summary>The muted copy: the grip's strings at the grip's frets, all fret-hand muted, with the strum's chord-wide flags.</summary>
    private static Chord Copy(FractionalPosition position, List<GuitarNote> notes, ChordTemplate grip)
    {
        var durations = notes.Select(n => n.NotationDuration).Distinct().ToList();
        var chord = new Chord
        {
            Position = position,
            TemplateId = grip.Id,
            Accent = notes.Any(n => n.Accent),
            Ignore = notes.All(n => n.Ignore),
            IgnoreDerived = notes.All(n => n.Ignore && n.IgnoreDerived),
            PassOtherNotes = notes.Any(n => n.PassOtherNotes),
            NotationDuration = durations.Count == 1 ? durations[0] : null
        };
        var sustain = notes.Max(n => n.Sustain);
        foreach (var (stringIndex, fret) in grip.Frets.OrderBy(f => f.Key))
        {
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = stringIndex,
                Fret = fret,
                Mute = MuteType.Full,
                Accent = chord.Accent,
                Ignore = chord.Ignore,
                Sustain = sustain,
                GraceFret = notes.FirstOrDefault(n => n.StringIndex == stringIndex)?.GraceFret
            });
        }

        return chord;
    }

    /// <summary>
    ///     Where the run's bar ends — <see cref="ChordHandShapePlanner" />'s rule with
    ///     <c>CoverEveryChord</c>: the last sound's authored span, clipped at the next sound, bar or
    ///     anchor after it; a last sound with no span reaches the earlier of one beat and that limit.
    /// </summary>
    private static FractionalPosition BarEnd(Run run, List<FractionalPosition> positions, List<FractionalPosition> anchors,
        List<HandShape> bars)
    {
        var last = run.Previous;
        FractionalPosition? limit = run.LastIndex + 1 < positions.Count ? positions[run.LastIndex + 1] : null;
        foreach (var anchor in anchors)
        {
            if (anchor.CompareTo(last) > 0 && (limit is not { } a || anchor.CompareTo(a) < 0)) limit = anchor;
        }

        foreach (var bar in bars)
        {
            if (ReferenceEquals(bar, run.Bar)) continue;
            if (bar.Position.CompareTo(last) > 0 && (limit is not { } b || bar.Position.CompareTo(b) < 0)) limit = bar.Position;
        }

        var end = run.PreviousEnd;
        if (limit is { } l && l.CompareTo(end) < 0) end = l;
        if (end.CompareTo(last) <= 0)
            end = limit is { } stop && stop.CompareTo(last + OneBeat) < 0 ? stop : last + OneBeat;
        return end;
    }

    /// <summary>One chord and the muted strums (and same-grip chords) its bar is about to cover.</summary>
    private sealed class Run
    {
        public Run(Chord first, ChordTemplate grip, int mintedBefore, HandShape? bar, int index)
        {
            First = first;
            Grip = grip;
            MintedBefore = mintedBefore;
            Bar = bar;
            Strings = first.ChordNotes.Select(n => n.StringIndex).ToHashSet();
            Advance(first.Position, ChordHandShapePlanner.AuthoredSpan(first), index);
        }

        public Chord First { get; }

        /// <summary>The bar covering <see cref="First" />, extended rather than doubled.</summary>
        public HandShape? Bar { get; }

        public IReadOnlySet<int> Strings { get; }

        /// <summary>The chord's grip (<see cref="ResolvedGripSource.GripFor" />): what every copy points at.</summary>
        public ChordTemplate Grip { get; }

        /// <summary>How many templates were minted before this run's grip — what <see cref="Close" /> takes back.</summary>
        public int MintedBefore { get; }

        public List<Chord> Strums { get; } = [];
        public List<GuitarNote> Removed { get; } = [];
        public FractionalPosition Previous { get; private set; }
        public FractionalPosition PreviousEnd { get; private set; }
        public int LastIndex { get; private set; }

        public void Advance(FractionalPosition position, FractionalPosition span, int index)
        {
            Previous = position;
            PreviousEnd = position + span;
            LastIndex = index;
        }
    }
}
