using System.Collections.ObjectModel;
using DotCharter.Services.State;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
/// Turns accepted structure changes into ONE undoable action on an arrangement's
///     <see cref="Arrangement.EventPoints" /> — the live one for the editor, a scratch copy for
///     <see cref="AiStructureGate" />, so what the checks approved is exactly what gets written.
///     <para>
///         <b>Only section and phrase fields are written</b> — plus, ADDITIVELY, the hand position a
/// phrase needs. The composite holds nothing but <see cref="AddEventPointsAction" />,
///         <see cref="RemoveEventPointsAction" /> and <see cref="MutateEventPointsAction" /> on the
///         arrangement's event points and one <see cref="AddAnchorsAction" /> per level for the
///         anchors a change carries (<see cref="AiStructureChange.Anchors" />); no note, chord,
///         template or EXISTING anchor is reachable from here. An event point keeps its
///         <see cref="EventPoint.Events" /> and its other field; one left with nothing is removed.
///         An anchor is left out where its level already has one at that spot (the charter placed
///         it meanwhile), so an anchor is never doubled, moved or replaced.
///     </para>
///     <para>
///         <b>Guards:</b> a rename, move or remove names the live marker by position AND name; a
///         marker the charter moved or renamed since is stale and the change is left out (the
///         marks leave it out too — they are read off this same composition). An add or a move
///         onto a spot where a marker of its kind already is, is stale as well. Clears run before
///         sets, so a reply that moves a marker away and puts another in its place composes in
///         either order.
///     </para>
/// </summary>
public static class AiStructureApplier
{
    /// <summary>Builds the action for <paramref name="changes" />, or null when nothing would
    /// change. Nothing is executed.</summary>
    /// <param name="arrangement">Owns the event points.</param>
    /// <param name="changes">Accepted changes.</param>
    /// <param name="applied">How many changes the action carries (stale ones are left out).</param>
    /// <param name="stale">How many were stale.</param>
    public static IChartAction? Build(Arrangement arrangement, IReadOnlyList<AiStructureChange> changes, out int applied,
        out int stale)
    {
        var composition = Compose(arrangement, changes);
        applied = composition.Applied;
        stale = composition.Stale;
        var composite = new CompositeAction(composition.Actions) { LabelKey = AiChartApplier.HistoryLabelKey };
        return composite.HasEffect ? composite : null;
    }

    /// <summary>What <see cref="Build" /> would change, read off the same composition: the review's
    /// marks. Nothing is built or executed.</summary>
    public static AiStructureApplyTargets Preview(Arrangement arrangement, IReadOnlyList<AiStructureChange> changes,
        out int applied)
    {
        var composition = Compose(arrangement, changes);
        applied = composition.Applied;
        return composition.Targets;
    }

    private static Composition Compose(Arrangement arrangement, IReadOnlyList<AiStructureChange> changes)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(changes);

        var work = new Work(arrangement.EventPoints, changes);
        work.ClaimSources();
        work.AssignNames();
        var (replacedSections, replacedPhrases, proposed) = work.Marks();
        var (placed, anchorMarks) = work.PlaceAnchors(arrangement.Levels);
        var actions = work.Write();
        foreach (var (level, anchors) in placed)
        {
            if (anchors.Count > 0) actions.Add(new AddAnchorsAction(level.Anchors, anchors));
        }

        var applied = work.Ok.Count(x => x);
        return new Composition(actions, applied, changes.Count - applied,
            new AiStructureApplyTargets(replacedSections, replacedPhrases, proposed, anchorMarks));
    }

    /// <summary>
    ///     One composition in progress: the section/phrase each touched event point would carry,
    ///     the points it creates, and per change its live source, that source's name before the
    ///     change and whether the change still applies. Nothing on the chart is written until
    ///     <see cref="Write" />, and then only the created points' own fields.
    /// </summary>
    private sealed class Work(ObservableCollection<EventPoint> live, IReadOnlyList<AiStructureChange> changes)
    {
        private readonly Dictionary<EventPoint, (string Section, string Phrase)> _state =
            new(ReferenceEqualityComparer.Instance);

        private readonly List<EventPoint> _created = [];
        private readonly EventPoint?[] _sources = new EventPoint?[changes.Count];
        private readonly string[] _originals = new string[changes.Count];

        public bool[] Ok { get; } = new bool[changes.Count];

        private (string Section, string Phrase) Fields(EventPoint e)
            => _state.TryGetValue(e, out var s) ? s : (e.Section, e.Phrase);

        private string Get(EventPoint e, AiStructureKind kind)
            => kind == AiStructureKind.Section ? Fields(e).Section : Fields(e).Phrase;

        private void Set(EventPoint e, AiStructureKind kind, string value)
        {
            var (section, phrase) = Fields(e);
            _state[e] = kind == AiStructureKind.Section ? (value, phrase) : (section, value);
        }

        /// <summary>1. The markers a rename, move or remove acts on: guarded, and cleared for a move or remove.</summary>
        public void ClaimSources()
        {
            var claimed = new HashSet<(EventPoint, AiStructureKind)>();
            for (var i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                if (c.Op == AiStructureOp.Add) continue;
                var source = live.FirstOrDefault(e => e.Position == c.From && !claimed.Contains((e, c.Kind)) &&
                                                      string.Equals(Get(e, c.Kind).Trim(), c.OldName, StringComparison.Ordinal));
                if (source is null) continue;
                claimed.Add((source, c.Kind));
                _sources[i] = source;
                _originals[i] = Get(source, c.Kind);
                Ok[i] = true;
                if (c.Op != AiStructureOp.Rename) Set(source, c.Kind, "");
            }
        }

        /// <summary>2. The names they get: a rename in place, an add or a move's destination.</summary>
        public void AssignNames()
        {
            for (var i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                if (c.Op == AiStructureOp.Remove) continue;
                if (c.Op == AiStructureOp.Rename)
                {
                    if (_sources[i] is { } renamed) Set(renamed, c.Kind, c.Name ?? "");
                    continue;
                }

                if (c.Op == AiStructureOp.Move && _sources[i] is null) continue;
                Ok[i] = Place(i, c);
            }
        }

        /// <summary>Puts an add or a move at its destination; false when the spot is taken.</summary>
        private bool Place(int i, AiStructureChange c)
        {
            var to = c.To!.Value;
            if (live.Concat(_created).Any(e => e.Position == to && Get(e, c.Kind).Length > 0))
            {
                // Taken: the move's clear is rolled back, so a stale move leaves its marker alone.
                if (_sources[i] is { } back) Set(back, c.Kind, _originals[i]);
                return false;
            }

            var target = live.FirstOrDefault(e => e.Position == to && Get(e, c.Kind).Length == 0) ??
                         _created.FirstOrDefault(e => e.Position == to);
            if (target is null)
            {
                target = new EventPoint { Position = to };
                _created.Add(target);
            }

            Set(target, c.Kind, c.NameAfter ?? "");
            return true;
        }

        /// <summary>3. The marks: the live marker each change replaces, and what it would show.</summary>
        public (List<EventPoint> Sections, List<EventPoint> Phrases, List<EventPoint> Proposed) Marks()
        {
            var replacedSections = new List<EventPoint>();
            var replacedPhrases = new List<EventPoint>();
            var proposed = new List<EventPoint>();
            for (var i = 0; i < changes.Count; i++)
            {
                if (!Ok[i]) continue;
                var c = changes[i];
                if (_sources[i] is { } source)
                    (c.Kind == AiStructureKind.Section ? replacedSections : replacedPhrases).Add(source);
                if (c.NameAfter is { } shown)
                {
                    proposed.Add(c.Kind == AiStructureKind.Section
                        ? new EventPoint { Position = c.At, Section = shown }
                        : new EventPoint { Position = c.At, Phrase = shown });
                }
            }

            return (replacedSections, replacedPhrases, proposed);
        }

        /// <summary>
        ///     4. The hand positions an added or moved phrase brings: ADDED only, never where the
        ///     level already has an anchor on that spot, and never onto a level that is gone.
        /// </summary>
        public (Dictionary<ChartLevel, List<FretAnchor>> Placed, List<AiStructureAnchorMark> Marks) PlaceAnchors(
            IList<ChartLevel> levels)
        {
            var placed = new Dictionary<ChartLevel, List<FretAnchor>>(ReferenceEqualityComparer.Instance);
            var anchorMarks = new List<AiStructureAnchorMark>();
            for (var i = 0; i < changes.Count; i++)
            {
                if (!Ok[i]) continue;
                foreach (var a in changes[i].Anchors)
                {
                    if (a.Level < 0 || a.Level >= levels.Count) continue;
                    var level = levels[a.Level];
                    if (!placed.TryGetValue(level, out var onLevel)) placed[level] = onLevel = [];
                    if (level.Anchors.Any(x => x.Position == a.Position) || onLevel.Any(x => x.Position == a.Position)) continue;
                    var anchor = new FretAnchor(a.Position, a.Fret, a.Width);
                    onLevel.Add(anchor);
                    anchorMarks.Add(new AiStructureAnchorMark(level, anchor));
                }
            }

            return (placed, anchorMarks);
        }

        /// <summary>5. The write: the live points whose fields changed, the ones left empty, the new ones.</summary>
        public List<IChartAction> Write()
        {
            var targets = new List<EventPoint>();
            var oldStates = new List<EventPoint>();
            var newStates = new List<EventPoint>();
            var removed = new List<EventPoint>();
            foreach (var e in live)
            {
                if (!_state.TryGetValue(e, out var s) || (s.Section == e.Section && s.Phrase == e.Phrase)) continue;
                if (s.Section.Length == 0 && s.Phrase.Length == 0 && string.IsNullOrEmpty(e.Events))
                {
                    removed.Add(e);
                    continue;
                }

                targets.Add(e);
                oldStates.Add(Copy(e, e.Section, e.Phrase));
                newStates.Add(Copy(e, s.Section, s.Phrase));
            }

            foreach (var e in _created)
            {
                var (section, phrase) = _state[e];
                e.Section = section;
                e.Phrase = phrase;
            }

            var actions = new List<IChartAction>();
            if (targets.Count > 0) actions.Add(new MutateEventPointsAction(live, targets, oldStates, newStates));
            if (removed.Count > 0) actions.Add(new RemoveEventPointsAction(live, removed));
            if (_created.Count > 0) actions.Add(new AddEventPointsAction(live, _created));
            return actions;
        }
    }

    private static EventPoint Copy(EventPoint e, string section, string phrase) => new()
    {
        Position = e.Position,
        Section = section,
        Phrase = phrase,
        Events = e.Events,
        Status = e.Status
    };

    private sealed record Composition(List<IChartAction> Actions, int Applied, int Stale, AiStructureApplyTargets Targets);
}
