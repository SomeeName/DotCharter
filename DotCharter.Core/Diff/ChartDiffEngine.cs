using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Diff;

/// <summary>
/// Beat-aligned structural diff of two arrangements: added / removed /
/// changed sounds, for the diff viewer and the LAN "has update" flow.
///
/// Notes are keyed by (string, position); chords by position. Same-key sounds
/// whose positions differ by at most <see cref="ChartDiffOptions.PositionTolerance"/>
/// are matched in order (both sides position-sorted, greedy two-pointer) and
/// compared field-by-field — differing fret / sustain / technique flags yield
/// a Changed entry naming the fields. Unmatched sounds become Added (new side)
/// or Removed (old side). Levels are paired by <c>DifficultyIndex</c>; a level
/// present on only one side diffs against an empty level.
///
/// A second pass (<see cref="DiffStructure"/>) covers everything that is not a
/// sound — anchors, hand shapes, fret-hand mutes, phrase/section markers, tone
/// changes, phrase iterations and chord templates — and feeds the same
/// added/removed/changed counts, so <see cref="ChartDiffResult.IsEmpty"/> means
/// "identical chart" rather than "identical notes". Pure and deterministic — no
/// clocks, no I/O, input untouched.
/// </summary>
public static class ChartDiffEngine
{
    public static ChartDiffResult Diff(Arrangement oldArrangement, Arrangement newArrangement,
        ChartDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldArrangement);
        ArgumentNullException.ThrowIfNull(newArrangement);
        options ??= new ChartDiffOptions();

        var indexes = oldArrangement.Levels.Select(l => l.DifficultyIndex)
            .Concat(newArrangement.Levels.Select(l => l.DifficultyIndex))
            .Distinct()
            .OrderBy(i => i);

        var empty = new ChartLevel();
        var levelDiffs = new List<ChartLevelDiff>();
        foreach (var index in indexes)
        {
            var oldLevel = oldArrangement.Levels.FirstOrDefault(l => l.DifficultyIndex == index) ?? empty;
            var newLevel = newArrangement.Levels.FirstOrDefault(l => l.DifficultyIndex == index) ?? empty;
            var entries = DiffLevel(oldLevel, newLevel, options);
            if (entries.Count > 0) levelDiffs.Add(new ChartLevelDiff(index, entries));
        }

        return new ChartDiffResult(levelDiffs, DiffStructure(oldArrangement, newArrangement, options));
    }

    /// <summary>
    ///     Differences in everything that is NOT a sound: per-level anchors, hand
    ///     shapes and fret-hand mutes, plus the arrangement's phrase/section markers,
    ///     tone changes, phrase iterations and chord templates. Same greedy
    ///     position-tolerant matching as the sound pass; templates have no position so
    ///     they are compared as a multiset by value.
    /// </summary>
    public static IReadOnlyList<ChartStructureDiff> DiffStructure(Arrangement oldArrangement,
        Arrangement newArrangement, ChartDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldArrangement);
        ArgumentNullException.ThrowIfNull(newArrangement);
        options ??= new ChartDiffOptions();
        var tolerance = new FractionalPosition(options.PositionTolerance);

        var entries = new List<ChartStructureDiff>();

        DiffHeader(entries, oldArrangement, newArrangement);

        // Sections, phrases and events are diffed as three independent MARKER
        // streams rather than by pairing EventPoint objects. An EventPoint is a
        // container whose grouping is reader-specific — EOF stores each marker as
        // its own text event, so a lossless round trip legitimately returns one
        // authored point as two. Pairing the containers reported that regrouping
        // as a real edit; pairing the markers does not.
        DiffTimed(entries, Markers(oldArrangement, e => e.Section), Markers(newArrangement, e => e.Section),
            ChartDiffElementKind.EventPoint, ArrangementScope, tolerance,
            m => m.Position, m => m.Text,
            (a, b) =>
            {
                var changed = new List<string>();
                if (a.Text != b.Text) changed.Add("Section");
                if (a.Status != b.Status) changed.Add("Status");
                return changed;
            });

        DiffTimed(entries, Markers(oldArrangement, e => e.Phrase), Markers(newArrangement, e => e.Phrase),
            ChartDiffElementKind.EventPoint, ArrangementScope, tolerance,
            m => m.Position, m => m.Text,
            (a, b) => a.Text != b.Text ? ["Phrase"] : []);

        DiffTimed(entries, Markers(oldArrangement, e => e.Events), Markers(newArrangement, e => e.Events),
            ChartDiffElementKind.EventPoint, ArrangementScope, tolerance,
            m => m.Position, m => m.Text,
            (a, b) => a.Text != b.Text ? ["Events"] : []);

        DiffTimed(entries, oldArrangement.ToneChanges, newArrangement.ToneChanges,
            ChartDiffElementKind.ToneChange, ArrangementScope, tolerance,
            t => t.Position, t => t.ToneName,
            (a, b) => a.ToneName != b.ToneName ? ["ToneName"] : []);

        DiffTimed(entries, oldArrangement.PhraseIterations, newArrangement.PhraseIterations,
            ChartDiffElementKind.PhraseIteration, ArrangementScope, tolerance,
            p => p.Position, p => PhraseName(oldArrangement, p.PhraseId),
            (a, b) => PhraseName(oldArrangement, a.PhraseId) != PhraseName(newArrangement, b.PhraseId)
                ? ["Phrase"]
                : []);

        DiffTemplates(entries, oldArrangement.ChordTemplates, newArrangement.ChordTemplates);

        var indexes = oldArrangement.Levels.Select(l => l.DifficultyIndex)
            .Concat(newArrangement.Levels.Select(l => l.DifficultyIndex))
            .Distinct()
            .OrderBy(i => i);

        var empty = new ChartLevel();
        foreach (var index in indexes)
        {
            var oldLevel = oldArrangement.Levels.FirstOrDefault(l => l.DifficultyIndex == index) ?? empty;
            var newLevel = newArrangement.Levels.FirstOrDefault(l => l.DifficultyIndex == index) ?? empty;

            DiffTimed(entries, oldLevel.Anchors, newLevel.Anchors,
                ChartDiffElementKind.Anchor, index, tolerance,
                a => a.Position, a => $"Fret {a.Fret}",
                (a, b) =>
                {
                    var changed = new List<string>();
                    if (a.Fret != b.Fret) changed.Add("Fret");
                    if (a.Width != b.Width) changed.Add("Width");
                    return changed;
                });

            DiffTimed(entries, oldLevel.HandShapes, newLevel.HandShapes,
                ChartDiffElementKind.HandShape, index, tolerance,
                h => h.Position, h => h.Name,
                (a, b) =>
                {
                    var changed = new List<string>();
                    if (a.EndPosition != b.EndPosition) changed.Add("EndPosition");
                    if (a.TemplateId != b.TemplateId) changed.Add("Template");
                    if (a.Name != b.Name) changed.Add("Name");
                    return changed;
                });

            DiffTimed(entries, oldLevel.FretHandMutes, newLevel.FretHandMutes,
                ChartDiffElementKind.FretHandMute, index, tolerance,
                m => m.Position, _ => "", (_, _) => []);
        }

        return entries
            .OrderBy(e => e.Position)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.DifficultyIndex)
            .ThenBy(e => e.Change)
            .ThenBy(e => e.Label, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary><see cref="ChartStructureDiff.DifficultyIndex" /> for an element that
    /// belongs to the arrangement rather than to one level.</summary>
    private const int ArrangementScope = -1;

    /// <summary>
    ///     The arrangement HEADER — everything that decides how the same notes
    ///     are PLAYED without being a note, an anchor or a marker.
    ///     <para>
    ///         It was compared by nothing at all, which put a whole class of edit
    ///         in the same hole the note-technique fields were in: retune a
    ///         chart from E standard to Drop D, put a capo on it, change it from
    ///         Lead to Rhythm or halve its scroll speed and the diff reported
    ///         <see cref="ChartDiffResult.IsEmpty" /> — "identical" in the dialog,
    ///         and "no update" to a LAN peer, which is the documented meaning of
    ///         that flag. Reporting a retune as a HEADER change is also the only
    ///         honest shape: the sound pass sees the same frets on the same
    ///         strings, so it has nothing to report and never will.
    ///     </para>
    ///     <para>
    ///         <c>Name</c> is deliberately excluded — the two sides are PAIRED by
    ///         the user (or by name), so a differing name is the premise of the
    ///         comparison rather than a finding. <c>Properties</c> is excluded
    ///         too: all but one of its flags are DERIVED from the notes by
    ///         <c>ArrangementPropertiesDeriver</c>, so it would double-report
    ///         every technique edit the sound pass already names.
    ///     </para>
    /// </summary>
    private static void DiffHeader(List<ChartStructureDiff> entries, Arrangement oldArrangement,
        Arrangement newArrangement)
    {
        var changed = new List<string>();

        if (!TuningEqual(oldArrangement, newArrangement)) changed.Add("Tuning");
        if (oldArrangement.Capo != newArrangement.Capo) changed.Add("Capo");
        // ReSharper disable once CompareOfFloatsByEqualityOperator — an authored
        // field that round-trips as text; a bit-exact compare is what "unchanged"
        // means for it.
        if (oldArrangement.CentOffset != newArrangement.CentOffset) changed.Add("CentOffset");
        if (oldArrangement.StringCount != newArrangement.StringCount) changed.Add("StringCount");
        if (oldArrangement.ArrangementType != newArrangement.ArrangementType) changed.Add("ArrangementType");
        if (oldArrangement.Subtype != newArrangement.Subtype) changed.Add("Subtype");
        if (oldArrangement.PickedBass != newArrangement.PickedBass) changed.Add("PickedBass");
        if (!Nullable.Equals(oldArrangement.ScrollSpeed, newArrangement.ScrollSpeed)) changed.Add("ScrollSpeed");
        if (!TonesEqual(oldArrangement, newArrangement)) changed.Add("Tones");

        if (changed.Count == 0) return;
        entries.Add(new ChartStructureDiff(ChartDiffChange.Changed, ChartDiffElementKind.ArrangementHeader,
            FractionalPosition.Zero, ArrangementScope, TuningLabel(newArrangement), changed));
    }

    /// <summary>Pitch, not spelling: the per-string semitone offsets carry the real
    /// tuning and the string names are the label. Either one differing is a retune.</summary>
    private static bool TuningEqual(Arrangement a, Arrangement b)
    {
        if (!a.Tuning.SequenceEqual(b.Tuning, StringComparer.Ordinal)) return false;
        if (a.TuningPitches.Count != b.TuningPitches.Count) return false;
        for (var i = 0; i < a.TuningPitches.Count; i++)
        {
            if (a.TuningPitches[i].Step != b.TuningPitches[i].Step)
                return false;
        }

        return string.Equals(a.TuningType, b.TuningType, StringComparison.Ordinal);
    }

    private static bool TonesEqual(Arrangement a, Arrangement b)
    {
        return string.Equals(a.StartingTone, b.StartingTone, StringComparison.Ordinal)
               && string.Equals(a.ToneBase, b.ToneBase, StringComparison.Ordinal)
               && string.Equals(a.ToneA, b.ToneA, StringComparison.Ordinal)
               && string.Equals(a.ToneB, b.ToneB, StringComparison.Ordinal)
               && string.Equals(a.ToneC, b.ToneC, StringComparison.Ordinal)
               && string.Equals(a.ToneD, b.ToneD, StringComparison.Ordinal);
    }

    private static string TuningLabel(Arrangement arrangement)
    {
        var strings = string.Join(" ", arrangement.Tuning);
        return arrangement.Capo > 0 ? $"{strings} (capo {arrangement.Capo})" : strings;
    }

    private static string PhraseName(Arrangement arrangement, int phraseId)
    {
        return phraseId >= 0 && phraseId < arrangement.Phrases.Count
            ? arrangement.Phrases[phraseId]
            : $"#{phraseId}";
    }

    /// <summary>One timeline marker carried by an <see cref="EventPoint" />.
    /// <see cref="Status" /> is only meaningful on the section stream.</summary>
    private sealed record Marker(FractionalPosition Position, string Text, SectionStatus Status);

    private static List<Marker> Markers(Arrangement arrangement, Func<EventPoint, string> select)
    {
        return arrangement.EventPoints
            .Where(e => !string.IsNullOrEmpty(select(e)))
            .Select(e => new Marker(e.Position, select(e), e.Status))
            .OrderBy(m => m.Position)
            .ToList();
    }

    /// <summary>Chord templates have no timeline position, so they are compared as a
    /// multiset by value — <see cref="ChordTemplate.Equals" /> keys on frets, fingers,
    /// name and both arpeggio flags. O(n*m), which is fine: a template list is tens of
    /// entries, not thousands.</summary>
    private static void DiffTemplates(List<ChartStructureDiff> entries,
        IReadOnlyList<ChordTemplate> oldTemplates, IReadOnlyList<ChordTemplate> newTemplates)
    {
        var unmatched = new List<ChordTemplate>(newTemplates);
        foreach (var template in oldTemplates)
        {
            var index = unmatched.FindIndex(t => t.Equals(template));
            if (index >= 0)
            {
                unmatched.RemoveAt(index);
            }
            else
            {
                entries.Add(new ChartStructureDiff(ChartDiffChange.Removed, ChartDiffElementKind.ChordTemplate,
                    FractionalPosition.Zero, ArrangementScope, template.GetNameWithFrets()));
            }
        }

        foreach (var template in unmatched)
        {
            entries.Add(new ChartStructureDiff(ChartDiffChange.Added, ChartDiffElementKind.ChordTemplate,
                FractionalPosition.Zero, ArrangementScope, template.GetNameWithFrets()));
        }
    }

    private static void DiffTimed<T>(List<ChartStructureDiff> entries,
        IEnumerable<T> oldItems, IEnumerable<T> newItems,
        ChartDiffElementKind kind, int difficultyIndex, FractionalPosition tolerance,
        Func<T, FractionalPosition> position, Func<T, string> label, Func<T, T, List<string>> compare)
    {
        var oldSorted = oldItems.OrderBy(position).ToList();
        var newSorted = newItems.OrderBy(position).ToList();

        MatchSequences(oldSorted, newSorted, position, tolerance,
            onMatch: (oldItem, newItem) =>
            {
                var changed = compare(oldItem, newItem);
                // Tolerance is for PAIRING; comparison stays exact, matching how
                // the sound pass reports a sub-tolerance nudge.
                if (position(oldItem) != position(newItem)) changed.Insert(0, "Position");
                if (changed.Count > 0)
                {
                    entries.Add(new ChartStructureDiff(ChartDiffChange.Changed, kind, position(newItem),
                        difficultyIndex, label(newItem), changed));
                }
            },
            onOldOnly: oldItem => entries.Add(new ChartStructureDiff(ChartDiffChange.Removed, kind,
                position(oldItem), difficultyIndex, label(oldItem))),
            onNewOnly: newItem => entries.Add(new ChartStructureDiff(ChartDiffChange.Added, kind,
                position(newItem), difficultyIndex, label(newItem))));
    }

    public static IReadOnlyList<ChartSoundDiff> DiffLevel(ChartLevel oldLevel, ChartLevel newLevel,
        ChartDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldLevel);
        ArgumentNullException.ThrowIfNull(newLevel);
        options ??= new ChartDiffOptions();
        var tolerance = new FractionalPosition(options.PositionTolerance);

        var entries = new List<ChartSoundDiff>();

        // Notes: per string, position-ordered two-pointer match.
        var strings = oldLevel.Notes.Select(n => n.StringIndex)
            .Concat(newLevel.Notes.Select(n => n.StringIndex))
            .Distinct()
            .OrderBy(s => s);
        foreach (var stringIndex in strings)
        {
            var oldNotes = oldLevel.Notes.Where(n => n.StringIndex == stringIndex)
                .OrderBy(n => n.Position).ToList();
            var newNotes = newLevel.Notes.Where(n => n.StringIndex == stringIndex)
                .OrderBy(n => n.Position).ToList();
            MatchSequences(oldNotes, newNotes, n => n.Position, tolerance,
                onMatch: (oldNote, newNote) =>
                {
                    var changed = CompareNotes(oldNote, newNote, tolerance);
                    if (changed.Count > 0)
                    {
                        entries.Add(new ChartSoundDiff(ChartDiffChange.Changed, ChartDiffSoundKind.Note,
                            newNote.Position, stringIndex, oldNote, newNote, changedFields: changed));
                    }
                },
                onOldOnly: oldNote => entries.Add(new ChartSoundDiff(ChartDiffChange.Removed,
                    ChartDiffSoundKind.Note, oldNote.Position, stringIndex, oldNote)),
                onNewOnly: newNote => entries.Add(new ChartSoundDiff(ChartDiffChange.Added,
                    ChartDiffSoundKind.Note, newNote.Position, stringIndex, newNote: newNote)));
        }

        // Chords: keyed by position only.
        var oldChords = oldLevel.Chords.OrderBy(c => c.Position).ToList();
        var newChords = newLevel.Chords.OrderBy(c => c.Position).ToList();
        MatchSequences(oldChords, newChords, c => c.Position, tolerance,
            onMatch: (oldChord, newChord) =>
            {
                var changed = CompareChords(oldChord, newChord, tolerance);
                if (changed.Count > 0)
                {
                    entries.Add(new ChartSoundDiff(ChartDiffChange.Changed, ChartDiffSoundKind.Chord,
                        newChord.Position, -1, oldChord: oldChord, newChord: newChord, changedFields: changed));
                }
            },
            onOldOnly: oldChord => entries.Add(new ChartSoundDiff(ChartDiffChange.Removed,
                ChartDiffSoundKind.Chord, oldChord.Position, -1, oldChord: oldChord)),
            onNewOnly: newChord => entries.Add(new ChartSoundDiff(ChartDiffChange.Added,
                ChartDiffSoundKind.Chord, newChord.Position, -1, newChord: newChord)));

        return entries
            .OrderBy(e => e.Position)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.StringIndex)
            .ThenBy(e => e.Change)
            .ToList();
    }

    /// <summary>Greedy in-order matcher over two position-sorted sequences:
    /// heads within tolerance pair up; otherwise the earlier head is emitted
    /// as one-sided and skipped. Deterministic.</summary>
    private static void MatchSequences<T>(IReadOnlyList<T> oldItems, IReadOnlyList<T> newItems,
        Func<T, FractionalPosition> position, FractionalPosition tolerance,
        Action<T, T> onMatch, Action<T> onOldOnly, Action<T> onNewOnly)
    {
        var i = 0;
        var j = 0;
        while (i < oldItems.Count && j < newItems.Count)
        {
            var oldPosition = position(oldItems[i]);
            var newPosition = position(newItems[j]);
            if ((oldPosition - newPosition).Absolute() <= tolerance)
            {
                onMatch(oldItems[i], newItems[j]);
                i++;
                j++;
            }
            else if (oldPosition < newPosition)
            {
                onOldOnly(oldItems[i]);
                i++;
            }
            else
            {
                onNewOnly(newItems[j]);
                j++;
            }
        }

        for (; i < oldItems.Count; i++) onOldOnly(oldItems[i]);
        for (; j < newItems.Count; j++) onNewOnly(newItems[j]);
    }

    private static List<string> CompareNotes(GuitarNote a, GuitarNote b, FractionalPosition tolerance)
    {
        var changed = new List<string>();
        if (a.Position != b.Position) changed.Add("Position");
        if (a.Fret != b.Fret) changed.Add("Fret");
        if (a.Sustain != b.Sustain) changed.Add("Sustain");
        AppendSoundTechniqueDiffs(changed, a, b, a.Position, b.Position, tolerance);
        if (a.Accent != b.Accent) changed.Add("Accent");
        if (a.Ignore != b.Ignore) changed.Add("Ignore");
        // These three are real, exportable authoring decisions that were simply
        // never compared, so a chart edited only in these ways diffed as
        // IDENTICAL — and IsEmpty is the documented LAN "has update" signal, so
        // peers were told a changed chart was unchanged.
        if (a.PassOtherNotes != b.PassOtherNotes) changed.Add("PassOtherNotes");
        if (a.LinkNext != b.LinkNext) changed.Add("LinkNext");
        return changed;
    }

    private static List<string> CompareChords(Chord a, Chord b, FractionalPosition tolerance)
    {
        var changed = new List<string>();
        if (a.Position != b.Position) changed.Add("Position");
        if (a.TemplateId != b.TemplateId) changed.Add("Template");
        if (a.Accent != b.Accent) changed.Add("Accent");
        if (a.Ignore != b.Ignore) changed.Add("Ignore");
        if (a.SplitIntoNotes != b.SplitIntoNotes) changed.Add("SplitIntoNotes");
        if (a.ForceNoNotes != b.ForceNoNotes) changed.Add("ForceNoNotes");
        if (a.PassOtherNotes != b.PassOtherNotes) changed.Add("PassOtherNotes");

        // Ordered so ChangedFields is a stable SEQUENCE, not just a stable set:
        // without it the order in which technique fields were appended followed
        // ChordNotes order, so a semantically identical chord with reordered notes
        // produced a differently ordered field list.
        var strings = a.ChordNotes.Select(n => n.StringIndex)
            .Concat(b.ChordNotes.Select(n => n.StringIndex))
            .Distinct()
            .OrderBy(s => s);
        var notesDiffer = false;
        var techniqueDiffs = new List<string>();
        foreach (var stringIndex in strings)
        {
            var oldNote = a.ChordNotes.FirstOrDefault(n => n.StringIndex == stringIndex);
            var newNote = b.ChordNotes.FirstOrDefault(n => n.StringIndex == stringIndex);
            if (oldNote == null || newNote == null)
            {
                notesDiffer = true;
                continue;
            }

            if (oldNote.Fret != newNote.Fret) notesDiffer = true;
            // Per-STRING accent and ignore were invisible to the diff: only the
            // chord-level flags were compared, so accenting one string of a
            // strum reported no change at all.
            if (oldNote.Accent != newNote.Accent && !techniqueDiffs.Contains("Accent"))
                techniqueDiffs.Add("Accent");
            if (oldNote.Ignore != newNote.Ignore && !techniqueDiffs.Contains("Ignore"))
                techniqueDiffs.Add("Ignore");
            if (oldNote.Sustain != newNote.Sustain && !techniqueDiffs.Contains("Sustain"))
                techniqueDiffs.Add("Sustain");
            AppendSoundTechniqueDiffs(techniqueDiffs, oldNote, newNote, a.Position, b.Position, tolerance);
        }

        if (notesDiffer) changed.Add("Notes");
        foreach (var diff in techniqueDiffs.Distinct())
        {
            if (!changed.Contains(diff))
                changed.Add(diff);
        }

        return changed;
    }

    private static void AppendSoundTechniqueDiffs(List<string> changed, IGuitarSound a, IGuitarSound b,
        FractionalPosition oldAnchor, FractionalPosition newAnchor, FractionalPosition tolerance)
    {
        Add(a.Mute != b.Mute, "Mute");
        Add(a.Hopo != b.Hopo, "Hopo");
        Add(a.Harmonic != b.Harmonic, "Harmonic");
        Add(a.Vibrato != b.Vibrato || EffectiveVibrato(a) != EffectiveVibrato(b), "Vibrato");
        Add(a.Tremolo != b.Tremolo, "Tremolo");
        Add(a.LinkNext != b.LinkNext, "LinkNext");
        Add(a.SlideTo != b.SlideTo || a.UnpitchedSlide != b.UnpitchedSlide, "Slide");
        Add(a.BassPicking != b.BassPicking, "BassPicking");
        Add(!BendsEqual(a.BendValues, b.BendValues, oldAnchor, newAnchor, tolerance), "Bends");
        return;

        void Add(bool condition, string field)
        {
            if (condition && !changed.Contains(field)) changed.Add(field);
        }
    }

    /// <summary>Bend curves are equal when steps match and each point sits at
    /// the same offset from its note (within tolerance — bend positions are
    /// absolute, but the two notes may be tolerance-matched, not identical).</summary>
    private static bool BendsEqual(IReadOnlyList<BendValue> a, IReadOnlyList<BendValue> b,
        FractionalPosition oldAnchor, FractionalPosition newAnchor, FractionalPosition tolerance)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Step.Equals(b[i].Step)) return false;
            var oldOffset = a[i].Position - oldAnchor;
            var newOffset = b[i].Position - newAnchor;
            if ((oldOffset - newOffset).Absolute() > tolerance) return false;
        }

        return true;
    }

    private static int EffectiveVibrato(IGuitarSound sound) => sound.Vibrato ? Math.Max(0, sound.VibratoStrength) : 0;
}
