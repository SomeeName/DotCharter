using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Services.State;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>A planned ghost-gem edit: the ONE undoable action, or null when there is nothing to do.</summary>
public sealed record GhostGemPlan(IChartAction? Action, int Gems, int TemplatesMinted, int HandShapesAdded)
{
    public static readonly GhostGemPlan Nothing = new(null, 0, 0, 0);

    public bool IsEmpty => Action is null;
}

/// <summary>
/// GHOST GEMS: a fret the hand HOLDS but does not play, charted the way Editor on
///     Fire charts it. EOF keeps a ghost bitmask on each note (<c>song.h:200</c>) and, on
///     Rocksmith export, writes a partially ghosted chord's chord tag from a ghost-less clone
///     and its handshape tag from the full shape (<c>rs.c:2369-2420</c>,
///     <c>eof_is_partially_ghosted</c> at <c>rs.c:110</c>), so in the game the ghost exists
///     ONLY in the hand shape's chord template. DotCharter stores it exactly there: a
///     <see cref="ChordTemplate" /> whose <see cref="ChordTemplate.GhostMask" /> marks the held
///     strings, named by the <see cref="HandShape" /> over the sound. A ghost is therefore
///     never a note — nothing plays, scores, detects or exports it as one, by construction.
///     <para>
///         Every verb here is PURE: it builds one composite action and commits nothing. A grip
///         is never rewritten in place (templates are shared): the bar is repointed at a
///         PINNED template holding the new grip — an identical pinned one when the chart has
///         it, else a mint in the same node.
///     </para>
/// </summary>
public static class GhostGems
{
    public const string GhostLabelKey = "History_GhostGems";
    public const string UnghostLabelKey = "History_UnghostGems";
    public const string SetGhostLabelKey = "History_SetGhostGem";
    public const string TappedHarmonicLabelKey = "History_TappedHarmonic";

    /// <summary>The default distance from a tapped harmonic down to the fret the hand holds.</summary>
    public const int DefaultTappedHarmonicOffset = 12;

    private static readonly FractionalPosition OneBeat = new(1);

    /// <summary>
    ///     Turns the selected gems into ghost gems of the hand shape over them: every note in
    ///     <paramref name="notes" />, and the strings of each chord in <paramref name="chords" />
    ///     that <paramref name="inScope" /> takes (the string-edit mask). The gem leaves the
    ///     level — a chord keeps the strings it still plays, re-templated on exactly those, and
    ///     becomes a single note when one string is left — and its fret joins the grip of the
    ///     hand shape covering it (one is added over the sound when none does).
    /// </summary>
    public static GhostGemPlan Ghost(
        Arrangement arrangement,
        ChartLevel level,
        IReadOnlyCollection<GuitarNote> notes,
        IReadOnlyCollection<Chord> chords,
        Func<int, bool> inScope)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(chords);
        ArgumentNullException.ThrowIfNull(inScope);

        var work = new Work(arrangement, level);
        var removedNotes = new List<GuitarNote>();
        var removedChords = new List<Chord>();
        var addedNotes = new List<GuitarNote>();
        var chordTargets = new List<Chord>();
        var chordNew = new List<Chord>();
        var gems = 0;

        foreach (var note in notes)
        {
            if (!level.Notes.Contains(note)) continue;
            var bar = work.BarFor(note.Position, note.Position + note.Sustain, null);
            bar.Hold(note.StringIndex, note.Fret);
            removedNotes.Add(note);
            gems++;
        }

        foreach (var chord in chords)
        {
            if (!level.Chords.Contains(chord)) continue;
            var ghosted = chord.ChordNotes.Where(cn => inScope(cn.StringIndex)).ToList();
            if (ghosted.Count == 0) continue;

            var bar = work.BarFor(chord.Position, chord.Position + ChordHandShapePlanner.AuthoredSpan(chord), chord);
            foreach (var gem in ghosted) bar.Hold(gem.StringIndex, gem.Fret);
            gems += ghosted.Count;

            var kept = chord.ChordNotes.Where(cn => !ghosted.Contains(cn)).ToList();
            if (kept.Count <= 1)
            {
                removedChords.Add(chord);
                if (kept.Count == 1) addedNotes.Add(LinkLandingBreakup.ToLooseNote(chord, kept[0]));
                continue;
            }

            var next = chord.Clone();
            next.ChordNotes = [.. kept.Select(cn => cn.Clone())];
            var current = ChordTemplateResolver.FindById(arrangement, chord.TemplateId);
            next.TemplateId = ChordTemplateResolver.Resolve(arrangement, ChordTemplateResolver.FretsOf(next),
                ArpeggioFlavour.Of(current), null, work.Minted).Id;
            chordTargets.Add(chord);
            chordNew.Add(next);
        }

        if (gems == 0) return GhostGemPlan.Nothing;

        var actions = work.Finish();
        if (removedNotes.Count > 0) actions.Add(new RemoveNotesAction(level.Notes, removedNotes));
        if (removedChords.Count > 0) actions.Add(new RemoveChordsAction(level.Chords, removedChords));
        if (chordTargets.Count > 0)
            actions.Add(new MutateChordsAction(level.Chords, chordTargets, chordTargets.Select(c => c.Clone()), chordNew));
        if (addedNotes.Count > 0) actions.Add(new AddNotesAction(level.Notes, addedNotes));
        work.AddBarActions(actions);
        return work.Plan(actions, GhostLabelKey, gems);
    }

    /// <summary>
    ///     Marks <paramref name="stringIndex" /> of <paramref name="shape" />'s grip as a ghost
    ///     gem (or a played fret again): the inspector's per-string checkbox. Null-plan when the
    ///     grip has no fret on that string or already says so.
    /// </summary>
    public static GhostGemPlan SetGhost(Arrangement arrangement, ChartLevel level, HandShape shape, int stringIndex,
        bool ghost)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(shape);
        var current = ChordTemplateResolver.FindById(arrangement, shape.TemplateId);
        if (current == null || !current.Frets.ContainsKey(stringIndex) || current.IsGhost(stringIndex) == ghost)
            return GhostGemPlan.Nothing;

        var work = new Work(arrangement, level);
        var bar = work.BarFor(shape);
        bar.SetGhost(stringIndex, ghost);
        var actions = work.Finish();
        work.AddBarActions(actions);
        return work.Plan(actions, SetGhostLabelKey, 1);
    }

    /// <summary>The keyboard toggle's other half: every ghost gem of each selected hand shape's
    /// grip becomes a plain fret of it again (the grip stays pinned — it is still authored).</summary>
    public static GhostGemPlan Unghost(Arrangement arrangement, ChartLevel level, IReadOnlyCollection<HandShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(shapes);
        var work = new Work(arrangement, level);
        var gems = 0;
        foreach (var shape in shapes)
        {
            if (!level.HandShapes.Contains(shape)) continue;
            var current = ChordTemplateResolver.FindById(arrangement, shape.TemplateId);
            if (current is not { GhostMask: not 0 }) continue;
            var bar = work.BarFor(shape);
            foreach (var stringIndex in current.Frets.Keys.Where(current.IsGhost).ToList())
            {
                bar.SetGhost(stringIndex, false);
                gems++;
            }
        }

        if (gems == 0) return GhostGemPlan.Nothing;
        var actions = work.Finish();
        work.AddBarActions(actions);
        return work.Plan(actions, UnghostLabelKey, gems);
    }

    /// <summary>
    ///     The TAPPED-HARMONIC shortcut: each selected single note becomes a tapped
    ///     (<see cref="HopoType.Tap" />) natural harmonic, and the hand shape over it holds a
    ///     ghost gem <paramref name="fretOffset" /> frets lower on the same string — fret 17
    ///     tapped over fret 5 held. The held fret is clamped to the neck (never below 0) and
    ///     lifted onto the capo rather than written behind it.
    /// </summary>
    public static GhostGemPlan TappedHarmonic(Arrangement arrangement, ChartLevel level,
        IReadOnlyCollection<GuitarNote> notes, int fretOffset)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(notes);
        var work = new Work(arrangement, level);
        var targets = new List<GuitarNote>();
        var next = new List<GuitarNote>();
        foreach (var note in notes)
        {
            if (!level.Notes.Contains(note)) continue;
            var tapped = note.Clone();
            tapped.Harmonic = HarmonicType.Normal;
            tapped.Hopo = HopoType.Tap;
            targets.Add(note);
            next.Add(tapped);

            var bar = work.BarFor(note.Position, note.Position + note.Sustain, null);
            bar.Hold(note.StringIndex, HeldFret(note.Fret, fretOffset, arrangement.Capo));
        }

        if (targets.Count == 0) return GhostGemPlan.Nothing;
        var actions = work.Finish();
        actions.Add(new MutateNotesAction(level.Notes, targets, targets.Select(n => n.Clone()), next));
        work.AddBarActions(actions);
        return work.Plan(actions, TappedHarmonicLabelKey, targets.Count);
    }

    /// <summary>The fret a tapped harmonic's hand holds: <paramref name="fretOffset" /> below the
    /// tap, never below the nut, and never a stopped fret behind the capo.</summary>
    public static int HeldFret(int tappedFret, int fretOffset, int capo)
    {
        var held = Math.Max(0, tappedFret - Math.Max(0, fretOffset));
        return held > 0 && held < capo ? capo : held;
    }

    /// <summary>
    /// Import: Rocksmith content states a ghost gem only as a hand-shape template
    ///     fret nothing under the shape plays. Marks those strings ghost on every template
    ///     that (a) no CHORD names — a chord's template is what it plays, so a shared one is
    ///     never reinterpreted — and (b) leaves the string unplayed at that fret under EVERY
    ///     hand shape naming it that covers any sound at all. Returns how many templates
    ///     changed. Runs at import, before any undo history exists, so it writes in place.
    /// </summary>
    public static int MarkUnplayedHandShapeFrets(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var namedByChord = new HashSet<int>();
        foreach (var level in arrangement.Levels)
        {
            foreach (var chord in level.Chords)
            {
                if (chord.TemplateId >= 0) namedByChord.Add(chord.TemplateId);
            }
        }

        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        var unplayed = new Dictionary<ChordTemplate, int>(ReferenceEqualityComparer.Instance);
        foreach (var level in arrangement.Levels)
        {
            foreach (var shape in level.HandShapes)
            {
                if (!byId.TryGetValue(shape.TemplateId, out var template)) continue;
                if (namedByChord.Contains(template.Id) || template.GhostMask != 0 || template.Frets.Count == 0) continue;

                var covered = false;
                var played = 0;
                foreach (var note in level.Notes)
                {
                    if (!shape.Covers(note.Position)) continue;
                    covered = true;
                    if (template.GetFret(note.StringIndex) == note.Fret) played |= ChordTemplate.GhostBit(note.StringIndex);
                }

                foreach (var chord in level.Chords)
                {
                    if (!shape.Covers(chord.Position)) continue;
                    covered = true;
                    foreach (var chordNote in chord.ChordNotes)
                    {
                        if (template.GetFret(chordNote.StringIndex) == chordNote.Fret)
                            played |= ChordTemplate.GhostBit(chordNote.StringIndex);
                    }
                }

                if (!covered) continue;
                var mask = unplayed.TryGetValue(template, out var was) ? was : AllStrings(template);
                unplayed[template] = mask & ~played;
            }
        }

        var changed = 0;
        foreach (var (template, mask) in unplayed)
        {
            if (mask == 0) continue;
            template.GhostMask = mask;
            changed++;
        }

        return changed;
    }

    private static int AllStrings(ChordTemplate template)
    {
        var mask = 0;
        foreach (var stringIndex in template.Frets.Keys) mask |= ChordTemplate.GhostBit(stringIndex);
        return mask;
    }

    /// <summary>One edit's hand shapes and the grips they end up holding.</summary>
    private sealed class Work(Arrangement arrangement, ChartLevel level)
    {
        private readonly List<Bar> _bars = [];
        private readonly List<HandShape> _added = [];
        private readonly List<HandShape> _rebound = [];
        private readonly List<HandShape> _reboundNew = [];

        public List<ChordTemplate> Minted { get; } = [];

        /// <summary>The bar over <paramref name="start" />: one already in this edit, the level's
        /// own, or a new one spanning the sound (at least a beat).</summary>
        public Bar BarFor(FractionalPosition start, FractionalPosition end, Chord? seedChord)
        {
            foreach (var bar in _bars)
            {
                if (bar.Shape.Covers(start)) return bar;
            }

            var existing = level.HandShapes.FirstOrDefault(h => h.Covers(start));
            if (existing != null) return BarFor(existing);

            var shape = new HandShape { Position = start, EndPosition = end > start ? end : start + OneBeat, Name = string.Empty };
            var grip = new ChordTemplate();
            if (seedChord != null)
            {
                // A fresh bar over a chord holds the chord's whole grip, fingered as the chord's
                // own template fingers it when that template describes the chord.
                var own = ChordTemplateResolver.FindById(arrangement, seedChord.TemplateId);
                foreach (var cn in seedChord.ChordNotes)
                {
                    grip.Frets[cn.StringIndex] = cn.Fret;
                    if (own != null && own.GetFret(cn.StringIndex) == cn.Fret && own.Fingers.TryGetValue(cn.StringIndex, out var f))
                        grip.Fingers[cn.StringIndex] = f;
                }

                if (own != null) grip.Name = own.Name;
            }

            var added = new Bar(shape, null, grip, isNew: true);
            _bars.Add(added);
            return added;
        }

        public Bar BarFor(HandShape shape)
        {
            foreach (var bar in _bars)
            {
                if (ReferenceEquals(bar.Shape, shape)) return bar;
            }

            var current = ChordTemplateResolver.FindById(arrangement, shape.TemplateId);
            var grip = current?.Clone() ?? new ChordTemplate();
            var made = new Bar(shape, current, grip, isNew: false);
            _bars.Add(made);
            return made;
        }

        /// <summary>Resolves every bar's grip to a pinned template (minting what the chart lacks) and
        /// returns the action list, starting with the mint so it runs before anything names it.</summary>
        public List<IChartAction> Finish()
        {
            var stringCount = Math.Max(1, arrangement.Tuning.Length > 0 ? arrangement.Tuning.Length : arrangement.StringCount);
            foreach (var bar in _bars)
            {
                var grip = bar.Grip;
                grip.Pinned = true;
                grip.FollowedFingering = false;
                FillFingers(grip, stringCount);

                var target = arrangement.ChordTemplates.Concat(Minted)
                    .FirstOrDefault(t => t.Pinned && t.Equals(grip));
                if (target == null)
                {
                    grip.Id = NextId();
                    Minted.Add(grip);
                    target = grip;
                }

                if (bar.IsNew)
                {
                    bar.Shape.TemplateId = target.Id;
                    bar.Shape.Name = target.Name;
                    _added.Add(bar.Shape);
                }
                else if (bar.Shape.TemplateId != target.Id)
                {
                    var next = bar.Shape.Clone();
                    next.TemplateId = target.Id;
                    _rebound.Add(bar.Shape);
                    _reboundNew.Add(next);
                }
            }

            var actions = new List<IChartAction>();
            if (Minted.Count > 0) actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, Minted));
            return actions;
        }

        public void AddBarActions(List<IChartAction> actions)
        {
            if (_rebound.Count > 0)
                actions.Add(new MutateHandShapesAction(level.HandShapes, _rebound, _rebound.Select(h => h.Clone()), _reboundNew));
            if (_added.Count > 0) actions.Add(new AddHandShapesAction(level.HandShapes, _added));
        }

        public GhostGemPlan Plan(List<IChartAction> actions, string labelKey, int gems) =>
            actions.Count == 0
                ? GhostGemPlan.Nothing
                : new GhostGemPlan(new CompositeAction(actions) { LabelKey = labelKey }, gems,
                    Minted.Count(t => t.GhostMask != 0 || t.Pinned), _added.Count);

        private int NextId()
        {
            var max = -1;
            foreach (var template in arrangement.ChordTemplates) max = Math.Max(max, template.Id);
            foreach (var template in Minted) max = Math.Max(max, template.Id);
            return max + 1;
        }

        /// <summary>Fingers every fretted string the grip does not finger yet (the suggester's answer
        /// for the whole grip, ghosts included — the chord box shows the fretting finger), and never
        /// an open string.</summary>
        private static void FillFingers(ChordTemplate grip, int stringCount)
        {
            if (grip.Fingerless) return;
            if (grip.Frets.Any(f => f.Value > 0 && !grip.Fingers.ContainsKey(f.Key)))
            {
                var suggestion = grip.Clone();
                suggestion.Fingers.Clear();
                ChordTemplateFingerSetter.SetSuggestedFingers(suggestion, stringCount);
                foreach (var (stringIndex, finger) in suggestion.Fingers) grip.Fingers.TryAdd(stringIndex, finger);
            }

            foreach (var stringIndex in grip.Fingers.Keys.ToList())
            {
                if (!grip.Frets.TryGetValue(stringIndex, out var fret) || fret == 0)
                    grip.Fingers.Remove(stringIndex);
            }
        }
    }

    /// <summary>One hand shape of the edit and the grip it will hold.</summary>
    private sealed class Bar(HandShape shape, ChordTemplate? current, ChordTemplate grip, bool isNew)
    {
        public HandShape Shape { get; } = shape;

        public ChordTemplate? Current { get; } = current;

        public ChordTemplate Grip { get; } = grip;

        public bool IsNew { get; } = isNew;

        /// <summary>The hand holds <paramref name="fret" /> on <paramref name="stringIndex" />
        /// without playing it. A finger set for another fret on that string goes.</summary>
        public void Hold(int stringIndex, int fret)
        {
            if (Grip.Frets.TryGetValue(stringIndex, out var was) && was != fret) Grip.Fingers.Remove(stringIndex);
            Grip.Frets[stringIndex] = fret;
            Grip.GhostMask |= ChordTemplate.GhostBit(stringIndex);
        }

        public void SetGhost(int stringIndex, bool ghost)
        {
            if (ghost) Grip.GhostMask |= ChordTemplate.GhostBit(stringIndex);
            else Grip.GhostMask &= ~ChordTemplate.GhostBit(stringIndex);
        }
    }
}
