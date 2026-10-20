namespace DotCharter.UI.ViewModels.Panels;

/// <summary>The rungs of the chord panel's "Apply to" ladder, narrowest
/// first. Every edit the panel makes reaches exactly one of them.</summary>
public enum ChordApplyRung
{
    /// <summary>The chord the panel shows (plus its hand shape when
    /// <see cref="AppConfig.SyncHandShapeToChordTemplate" /> is on).</summary>
    ThisChord,

    /// <summary>The selection, narrowed to the sounds that play the shown shape.</summary>
    Selected,

    /// <summary>Every chord and hand shape on the shape, on every level.</summary>
    EveryUse,

    /// <summary>Every use, plus the other templates holding the same grip: the
    /// same frets, or the grip moved along the neck.</summary>
    SameGrip
}

/// <summary>What an edit changes. It decides what the "same grip" rung can
/// carry: a fingering is the same on every position of a grip, a name only on
/// the same frets, and a shape, arpeggio, Nop or Fingerless flag or template switch
/// never leaves the shape it was made on.</summary>
public enum ChordTemplateEditKind
{
    Shape,
    Fingering,
    Name,
    Arpeggio,

    /// <summary>The Rocksmith double-stop panel flag, <see cref="ChordTemplate.NopInRs" />.</summary>
    Nop,

    /// <summary>EOF's Fingerless flag, <see cref="ChordTemplate.Fingerless" />: a statement
    /// about this shape, so like Nop it never crosses to another grip.</summary>
    Fingerless,
    Switch,

    /// <summary>A technique or playback flag on the chords themselves: never the
    /// template. It reaches every grip, as a fingering does — the same strings are struck.</summary>
    Technique
}

/// <summary>
/// Which edits the ladder is describing. A <see cref="Shape" /> edit writes the
///     template and reaches hand shapes; a <see cref="Technique" /> edit writes the chords
///     only, and its "selected" rung is the whole selection, as the Technique segment always
///     edited — so the counts, the lit rung and the preview follow the edits on screen.
/// </summary>
public enum ChordApplyFamily
{
    Shape,
    Technique
}

/// <summary>The chords one technique or playback edit writes, and the rung it took (null
/// when no rung reaches anything).</summary>
public sealed record ChordTechniqueTargets(ChordApplyRung? Rung, IReadOnlyList<Chord> Chords)
{
    /// <summary>The edit reaches exactly the selection: the inspector's own path.</summary>
    public bool IsSelection => Rung is null or ChordApplyRung.Selected;
}

/// <summary>How the planner writes a resolved edit.</summary>
public enum ChordApplyPath
{
    /// <summary>Nothing to write: no target, or a reshape with nothing selected.</summary>
    Refused,

    /// <summary>Bind the listed sounds to a find-or-created template; the loaded
    /// template is left as it is.</summary>
    Rebind,

    /// <summary>Rewrite the loaded template in place (every user follows) and
    /// bind the listed unbound sounds to it.</summary>
    InPlace
}

/// <summary>A set of chords and hand shapes.</summary>
public sealed record ChordApplySounds(IReadOnlyList<Chord> Chords, IReadOnlyList<HandShape> HandShapes)
{
    public static readonly ChordApplySounds None = new([], []);

    public int Count => Chords.Count + HandShapes.Count;
}

/// <summary>What one rung would reach, for its row on the ladder.</summary>
public readonly record struct ChordApplyRungReach(
    bool IsAvailable,
    int Chords,
    int HandShapes,
    int Levels,
    int GripTemplates,
    int GripChords)
{
    public int Sounds => Chords + HandShapes;
}

/// <summary>
///     The resolved target set of one edit: the rung it took (after an
///     unavailable rung fell through), the write path, and every chord, hand
///     shape and template it touches. The planner executes exactly this.
/// </summary>
public sealed record ChordApplyTargets(
    ChordApplyRung Rung,
    ChordTemplateEditKind Kind,
    ChordApplyPath Path,
    ChordApplySounds Sounds,
    IReadOnlyList<Chord> BoundChords,
    int BoundHandShapes,
    IReadOnlyList<ChordTemplate> GripTemplates,
    int GripChords,
    int GripHandShapes = 0)
{
    /// <summary>True when the "same grip" rung was lit for a fingering that
    /// leaves a fretted string without a finger: the other grips keep theirs,
    /// and the status rail says why (<see cref="ChordApplyScope.IsFullyFingered" />).</summary>
    public bool GripsWithheld { get; init; }

    /// <summary>Every chord the edit changes, across all templates it touches.</summary>
    public int ChordCount => Sounds.Chords.Count + BoundChords.Count + GripChords;

    /// <summary>Every hand shape the edit changes, across all templates it touches.</summary>
    public int HandShapeCount => Sounds.HandShapes.Count + BoundHandShapes + GripHandShapes;

    /// <summary>How many templates the edit writes or binds to.</summary>
    public int ShapeCount => 1 + GripTemplates.Count;
}

/// <summary>An edit the planner wrote: the template the panel shows next and
/// the targets it reached, which the status rail reports.</summary>
public sealed record ChordApplyResult(ChordTemplate Shown, ChordApplyTargets Targets);

/// <summary>
/// The ONE decision of how far a chord-panel edit reaches: given the
///     shape the panel shows, the chord it shows ("this"), the selection and the
///     arrangement, <see cref="Resolve" /> answers the target set of an edit kind
///     on a rung, and <see cref="Reach" /> what each rung of the ladder would
///     touch. Pure: it reads the arrangement and writes nothing.
///     <para>
///         It replaced five mechanisms that each decided reach on their own — the
///         this/all switch, Save fingering's propagation to same-fret templates
///         that only ran under "all", the "Finger N alike shapes" button, the
///         hand-shape sync and the hand-shape rebind of unbound chords — so the
///         same click never changes 1 chord or 40 depending on state nobody
///         could see. The sync and the rebind live on here, inside the rungs, and
///         the ladder prints what they add.
///     </para>
///     <para>
///         Both inputs are NARROWED to the sounds that play the shown shape
///         (<see cref="ChordTemplateEditPlanner.ChordsCarryingDisplayedShape" />):
///         every write projects its targets onto that shape, and Ctrl+Shift+A
///         selects sounds of every shape.
///     </para>
/// </summary>
public sealed class ChordApplyScope
{
    private readonly ChordTemplate _loaded;
    private readonly ChordApplySounds _this;
    private readonly ChordApplySounds _selected;
    private readonly IReadOnlyList<Chord> _thisChordsRaw;
    private readonly IReadOnlyList<Chord> _selectedChordsRaw;
    private readonly IReadOnlyList<ChartLevel> _levelsOfArrangement;
    private readonly IReadOnlyList<Chord> _activeLevelChords;
    private readonly bool _templateIsReal;
    private readonly List<Chord> _boundChords = [];
    private readonly List<HandShape> _boundHandShapes = [];
    private readonly int _levels;
    private readonly int _capo;
    private readonly List<ChordTemplate> _sameFrets = [];
    private readonly List<ChordTemplate> _grip = [];
    private readonly Dictionary<ChordTemplate, List<Chord>> _chordsOnTemplate = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ChordTemplate, List<HandShape>> _handShapesOnTemplate = new(ReferenceEqualityComparer.Instance);
    private List<Chord>? _enclosedBySelection;

    private ChordApplyScope(Arrangement arrangement, ChordTemplate loaded, ChordApplySounds thisSounds,
        ChordApplySounds selected, IReadOnlyList<Chord> activeLevelChords)
    {
        _loaded = loaded;
        _capo = arrangement.Capo;
        _activeLevelChords = activeLevelChords;
        _this = Narrow(thisSounds, loaded, arrangement);
        _thisChordsRaw = thisSounds.Chords;
        _selectedChordsRaw = selected.Chords;
        _levelsOfArrangement = arrangement.Levels;
        _selected = Narrow(selected, loaded, arrangement);
        _templateIsReal = arrangement.ChordTemplates.Any(t => ReferenceEquals(t, loaded));

        // A grip of the OTHER arpeggio flavour is a different kind of shape
        // (`ChordMask.Arpeggio` is load-bearing on export), so neither a
        // fingering nor a name crosses it — BOTH flags, as every find-or-create
        // key reads them (editing-invariants.md).
        var flavour = ArpeggioFlavour.Of(loaded);
        foreach (var template in arrangement.ChordTemplates)
        {
            if (ReferenceEquals(template, loaded) || !flavour.Matches(template)) continue;
            if (FingersDictHelper.Equal(template.Frets, loaded.Frets) && template.Frets.Count > 0)
                _sameFrets.Add(template);
        }

        _grip.AddRange(_sameFrets);
        var inGrip = new HashSet<ChordTemplate>(_grip, ReferenceEqualityComparer.Instance);
        foreach (var moved in ChordShapeTransposition.FindTransposed(arrangement, loaded, arrangement.Capo))
        {
            if (flavour.Matches(moved) && inGrip.Add(moved)) _grip.Add(moved);
        }

        var gripById = new Dictionary<int, ChordTemplate>();
        foreach (var template in _grip) gripById.TryAdd(template.Id, template);

        foreach (var level in arrangement.Levels)
        {
            var onLevel = false;
            foreach (var chord in level.Chords)
            {
                if (_templateIsReal && chord.TemplateId == loaded.Id)
                {
                    _boundChords.Add(chord);
                    onLevel = true;
                }
                else if (gripById.TryGetValue(chord.TemplateId, out var grip))
                {
                    UsesOf(_chordsOnTemplate, grip).Add(chord);
                }
            }

            foreach (var handShape in level.HandShapes)
            {
                if (_templateIsReal && handShape.TemplateId == loaded.Id)
                {
                    _boundHandShapes.Add(handShape);
                    onLevel = true;
                }
                else if (gripById.TryGetValue(handShape.TemplateId, out var grip))
                {
                    UsesOf(_handShapesOnTemplate, grip).Add(handShape);
                }
            }

            if (onLevel) _levels++;
        }
    }

    /// <summary>Builds the scope. <paramref name="activeLevelChords" /> is where
    /// a hand shape's enclosed, still-unbound chords are looked for.</summary>
    public static ChordApplyScope Prepare(Arrangement arrangement, ChordTemplate loaded,
        ChordApplySounds thisSounds, ChordApplySounds selected, IReadOnlyList<Chord> activeLevelChords) =>
        new(arrangement, loaded, thisSounds, selected, activeLevelChords);

    /// <summary>True when the selection narrowed to the shown shape is empty —
    /// browse mode, where the panel edits the shape itself.</summary>
    public bool HasSelection => _selected.Count > 0 || _this.Count > 0;

    /// <summary>The "this" sounds after narrowing: does it include a hand shape
    /// beside the chord (the sync), or is it a hand shape alone?</summary>
    public ChordApplySounds ThisSounds => _this;

    public ChordApplySounds SelectedSounds => _selected;

    /// <summary>
    ///     True when a template of the same grip is fingered differently from the
    ///     shown shape — so a Save on "+ the same grip elsewhere" has something to
    ///     do even with no finger typed. That is what the "Finger N alike shapes"
    ///     button did; the rung replaced it. A shape that is not
    ///     <see cref="IsFullyFingered" /> copies nothing: half a fingering would
    ///     erase the fingers a complete grip elsewhere already has.
    /// </summary>
    public bool GripFingeringDiffers => IsFullyFingered(_loaded) && _grip.Any(g =>
        !FingersDictHelper.Equal(g.Fingers, _loaded.Fingers.Where(f => g.Frets.ContainsKey(f.Key))
            .ToDictionary(f => f.Key, f => f.Value)));

    /// <summary>
    ///     True when every string <paramref name="shape" /> frets (above the capo)
    ///     carries a finger. Only such a fingering is copied onto the same grip
    ///     elsewhere: the copy OVERWRITES, so a shape fingered on one string of
    ///     three would wipe the other two fingers of a complete grip — through the
    ///     finger keybind, or a Save with nothing typed.
    /// </summary>
    public bool IsFullyFingered(ChordTemplate shape)
    {
        var fretted = 0;
        foreach (var (stringIndex, fret) in shape.Frets)
        {
            if (fret <= _capo) continue;
            if (!shape.Fingers.TryGetValue(stringIndex, out var finger) || finger < 0) return false;
            fretted++;
        }

        return fretted > 0;
    }

    public bool IsAvailable(ChordApplyRung rung, ChordApplyFamily family = ChordApplyFamily.Shape) => rung switch
    {
        ChordApplyRung.ThisChord when family == ChordApplyFamily.Technique => _thisChordsRaw.Count > 0,
        ChordApplyRung.Selected when family == ChordApplyFamily.Technique => _selectedChordsRaw.Count > 0,
        ChordApplyRung.ThisChord => _this.Count > 0,
        ChordApplyRung.Selected => _selected.Count > 0,
        ChordApplyRung.EveryUse => _templateIsReal || _selected.Count > 0,
        _ => (_templateIsReal || _selected.Count > 0) && _grip.Count > 0
    };

    /// <summary>
    ///     The rung an edit actually takes: the chosen one, or — when it has
    ///     nothing to reach (no selection for the first two, no other grip for the
    ///     last) — the nearest available rung ABOVE it, then below. With nothing
    ///     selected that is "every use", which is what browse mode has always
    ///     edited. Null when no rung reaches anything.
    /// </summary>
    public ChordApplyRung? Effective(ChordApplyRung chosen, ChordApplyFamily family = ChordApplyFamily.Shape)
    {
        for (var rung = (int)chosen; rung <= (int)ChordApplyRung.SameGrip; rung++)
        {
            if (IsAvailable((ChordApplyRung)rung, family)) return (ChordApplyRung)rung;
        }

        for (var rung = (int)chosen - 1; rung >= 0; rung--)
        {
            if (IsAvailable((ChordApplyRung)rung, family)) return (ChordApplyRung)rung;
        }

        return null;
    }

    /// <summary>
    ///     What <paramref name="rung" /> reaches, for its row on the ladder — READ
    ///     OFF <see cref="Resolve" /> (a fingering, the edit that reaches furthest),
    ///     so the count includes everything the write would touch: the unbound
    ///     strums under a selected hand shape included. The grips are counted as
    ///     a complete fingering would reach them.
    /// </summary>
    public ChordApplyRungReach Reach(ChordApplyRung rung, ChordApplyFamily family = ChordApplyFamily.Shape)
    {
        if (!IsAvailable(rung, family)) return default;
        if (family == ChordApplyFamily.Technique)
        {
            var chords = TechniqueChordsOn(rung);
            return new ChordApplyRungReach(true, chords.Count, 0, rung >= ChordApplyRung.EveryUse ? LevelsOf(chords) : 0,
                rung == ChordApplyRung.SameGrip ? _grip.Count : 0, rung == ChordApplyRung.SameGrip ? GripChordsOf(_grip) : 0);
        }

        var targets = ResolveWithGrips(rung, ChordTemplateEditKind.Fingering, fullyFingered: true);
        return new ChordApplyRungReach(true,
            targets.Sounds.Chords.Count + targets.BoundChords.Count,
            targets.Sounds.HandShapes.Count + targets.BoundHandShapes,
            rung >= ChordApplyRung.EveryUse ? _levels : 0,
            targets.GripTemplates.Count, targets.GripChords);
    }

    /// <summary>The target set of a <paramref name="kind" /> edit on
    /// <paramref name="chosen" /> (or the rung it falls through to).
    /// <paramref name="newState" /> is the state being written: a fingering
    /// reaches the other grips only when it is <see cref="IsFullyFingered" />
    /// (the loaded shape's fingering is judged when it is null).</summary>
    public ChordApplyTargets Resolve(ChordApplyRung chosen, ChordTemplateEditKind kind, ChordTemplate? newState = null) =>
        ResolveWithGrips(chosen, kind, kind != ChordTemplateEditKind.Fingering || IsFullyFingered(newState ?? _loaded));

    /// <summary>The same set with only <paramref name="changed" /> of its grip
    /// templates — the ones the write actually changed, which the status rail
    /// counts with their chords and hand shapes.</summary>
    public ChordApplyTargets WithGrips(ChordApplyTargets targets, IReadOnlyList<ChordTemplate> changed) =>
        targets with
        {
            GripTemplates = changed,
            GripChords = GripChordsOf(changed),
            GripHandShapes = changed.Sum(g => _handShapesOnTemplate.GetValueOrDefault(g)?.Count ?? 0)
        };

    /// <summary>
    ///     Every chord and hand shape <paramref name="targets" /> changes, on every
    ///     level, each once: the sounds it binds, the uses of the loaded shape an
    ///     in-place edit rewrites, and the uses of every grip template it writes.
    ///     The counts on <see cref="ChordApplyTargets" /> describe exactly this set.
    /// </summary>
    public ChordApplySounds Members(ChordApplyTargets targets)
    {
        if (targets.Path == ChordApplyPath.Refused) return ChordApplySounds.None;
        var chords = new List<Chord>();
        var handShapes = new List<HandShape>();
        var seenChords = new HashSet<Chord>(ReferenceEqualityComparer.Instance);
        var seenHandShapes = new HashSet<HandShape>(ReferenceEqualityComparer.Instance);
        AddAll(chords, seenChords, targets.Sounds.Chords);
        AddAll(handShapes, seenHandShapes, targets.Sounds.HandShapes);
        if (targets.Path == ChordApplyPath.InPlace)
        {
            AddAll(chords, seenChords, targets.BoundChords);
            AddAll(handShapes, seenHandShapes, _boundHandShapes);
        }

        foreach (var grip in targets.GripTemplates)
        {
            AddAll(chords, seenChords, _chordsOnTemplate.GetValueOrDefault(grip) ?? []);
            AddAll(handShapes, seenHandShapes, _handShapesOnTemplate.GetValueOrDefault(grip) ?? []);
        }

        return new ChordApplySounds(chords, handShapes);
    }

    /// <summary>
    ///     What an edit on <paramref name="chosen" /> would change, for the bulk-edit
    /// preview: <see cref="Members" /> of the very target set
    ///     <see cref="Reach" /> counts — a complete fingering, the edit that reaches
    ///     furthest — so the marks and the rung's number are one answer. Empty when
    ///     no rung reaches anything.
    /// </summary>
    public ChordApplySounds Preview(ChordApplyRung chosen, ChordApplyFamily family = ChordApplyFamily.Shape) =>
        family == ChordApplyFamily.Technique
            ? new ChordApplySounds(TechniqueTargets(chosen).Chords, [])
            : Members(ResolveWithGrips(chosen, ChordTemplateEditKind.Fingering, fullyFingered: true));

    /// <summary>
    /// The chords a technique or playback edit on <paramref name="chosen" /> writes,
    ///     on every level the rung covers. "this chord" and "selected" are the chord the panel
    ///     shows and the WHOLE selection — what the Technique segment always edited, whatever
    ///     shape each chord plays; "every use" and "+ the same grip" are <see cref="Members" />
    ///     of the very target set a shape edit resolves, chords only, since a hand shape
    ///     carries no technique. The template is never written.
    /// </summary>
    public ChordTechniqueTargets TechniqueTargets(ChordApplyRung chosen) =>
        Effective(chosen, ChordApplyFamily.Technique) is { } rung
            ? new ChordTechniqueTargets(rung, TechniqueChordsOn(rung))
            : new ChordTechniqueTargets(null, []);

    private IReadOnlyList<Chord> TechniqueChordsOn(ChordApplyRung rung) => rung switch
    {
        ChordApplyRung.ThisChord => _thisChordsRaw,
        ChordApplyRung.Selected => _selectedChordsRaw,
        _ => Members(ResolveWithGrips(rung, ChordTemplateEditKind.Technique, fullyFingered: true)).Chords
    };

    private int LevelsOf(IReadOnlyList<Chord> chords)
    {
        var set = new HashSet<Chord>(chords, ReferenceEqualityComparer.Instance);
        return _levelsOfArrangement.Count(level => level.Chords.Any(set.Contains));
    }

    private static void AddAll<T>(List<T> into, HashSet<T> seen, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            if (seen.Add(item)) into.Add(item);
        }
    }

    private static List<T> UsesOf<T>(Dictionary<ChordTemplate, List<T>> uses, ChordTemplate grip)
    {
        if (!uses.TryGetValue(grip, out var list)) uses[grip] = list = [];
        return list;
    }

    private ChordApplyTargets ResolveWithGrips(ChordApplyRung chosen, ChordTemplateEditKind kind, bool fullyFingered)
    {
        var targets = ResolveSounds(chosen, kind);
        if (targets.Path == ChordApplyPath.Refused || targets.Rung != ChordApplyRung.SameGrip) return targets;
        var grips = GripsFor(kind);
        if (fullyFingered) return WithGrips(targets, grips);
        return targets with { GripsWithheld = grips.Count > 0 };
    }

    /// <summary>The chords and hand shapes of the loaded shape an edit reaches;
    /// the grip templates are added by the caller.</summary>
    private ChordApplyTargets ResolveSounds(ChordApplyRung chosen, ChordTemplateEditKind kind)
    {
        var effective = Effective(chosen);
        // BROWSE-MODE GUARD: with nothing selected a reshape has nothing
        // to scope it, and the fret box would rewrite every use of the browsed
        // shape once per digit. Name, fingering and arpeggio edits still land
        // on the shape itself — browse mode and "Save & next missing" exist for
        // them.
        if (effective is not { } rung || (kind == ChordTemplateEditKind.Shape && !HasSelection))
            return Refused(effective ?? chosen, kind);

        if (rung <= ChordApplyRung.Selected)
        {
            var sounds = SoundsOf(rung);
            // Every user of the shape is in the set: an in-place edit is the same
            // edit, and it leaves no orphaned template behind. The unbound strums
            // under its hand shapes join it, as on "every use".
            if (kind != ChordTemplateEditKind.Switch && CoversEveryUse(sounds))
                return InPlace(rung, kind, EnclosedUnbound(sounds.HandShapes), []);
            return new ChordApplyTargets(rung, kind, ChordApplyPath.Rebind, sounds, [], 0, [], 0);
        }

        if (!_templateIsReal)
        {
            // A display-only shape (an untemplated chord) has no uses of its own:
            // every use of it IS the selection.
            return _selected.Count == 0
                ? Refused(rung, kind)
                : new ChordApplyTargets(rung, kind, ChordApplyPath.Rebind, _selected, [], 0, [], 0);
        }

        var joinChords = Unbound(_selected.Chords).ToList();
        var joined = new HashSet<Chord>(joinChords, ReferenceEqualityComparer.Instance);
        _enclosedBySelection ??= EnclosedUnbound(_selected.HandShapes);
        foreach (var chord in _enclosedBySelection)
        {
            if (joined.Add(chord)) joinChords.Add(chord);
        }

        var joinHandShapes = Unbound(_selected.HandShapes).ToList();
        if (kind == ChordTemplateEditKind.Switch)
        {
            // Moving every use onto another template is a rebind of all of them.
            return new ChordApplyTargets(rung, kind, ChordApplyPath.Rebind,
                new ChordApplySounds([.. _boundChords, .. joinChords], [.. _boundHandShapes, .. joinHandShapes]),
                [], 0, [], 0);
        }

        return InPlace(rung, kind, joinChords, joinHandShapes);
    }

    private ChordApplyTargets InPlace(ChordApplyRung rung, ChordTemplateEditKind kind, IReadOnlyList<Chord> joinChords,
        IReadOnlyList<HandShape> joinHandShapes) =>
        new(rung, kind, ChordApplyPath.InPlace, new ChordApplySounds(joinChords, joinHandShapes),
            _boundChords, _boundHandShapes.Count, [], 0);

    private static ChordApplyTargets Refused(ChordApplyRung rung, ChordTemplateEditKind kind) =>
        new(rung, kind, ChordApplyPath.Refused, ChordApplySounds.None, [], 0, [], 0);

    private ChordApplySounds SoundsOf(ChordApplyRung rung) => rung switch
    {
        ChordApplyRung.ThisChord => _this,
        _ => _selected
    };

    /// <summary>The grip templates an edit of this kind may write. Each one is
    /// edited IN PLACE and moves no fret, which is the sanctioned exception to
    /// "never mutate a shared template" (editing-invariants.md): every chord on
    /// it plays this very grip.</summary>
    private IReadOnlyList<ChordTemplate> GripsFor(ChordTemplateEditKind kind) => kind switch
    {
        ChordTemplateEditKind.Fingering or ChordTemplateEditKind.Technique => _grip,
        // A name belongs to the PITCHES, so a grip moved along the neck is a
        // different chord; only a same-fret template (of the same flavour, as
        // every grip is) can share it.
        ChordTemplateEditKind.Name => _sameFrets,
        _ => []
    };

    private int GripChordsOf(IReadOnlyList<ChordTemplate> grips)
    {
        var count = 0;
        foreach (var grip in grips) count += _chordsOnTemplate.GetValueOrDefault(grip)?.Count ?? 0;
        return count;
    }

    private bool CoversEveryUse(ChordApplySounds sounds)
    {
        if (!_templateIsReal || sounds.Count != _boundChords.Count + _boundHandShapes.Count) return false;
        return sounds.Chords.All(IsBound) && sounds.HandShapes.All(h => h.TemplateId == _loaded.Id);
    }

    private bool IsBound(Chord chord) =>
        _templateIsReal && chord.TemplateId == _loaded.Id && ChordTemplateEditPlanner.TemplateMatchesChord(_loaded, chord);

    private IEnumerable<Chord> Unbound(IReadOnlyList<Chord> chords) => chords.Where(c => !IsBound(c));

    private IEnumerable<HandShape> Unbound(IReadOnlyList<HandShape> handShapes) =>
        handShapes.Where(h => !_templateIsReal || h.TemplateId != _loaded.Id);

    /// <summary>
    ///     Chords inside one of <paramref name="handShapes" /> that carry the
    ///     shown shape but are NOT bound to it — the freshly imported strums
    ///     (TemplateId -1 until the first save) under a hand-shape bar. An
    ///     in-place edit binds and reshapes them too, or the 3D bar and its
    ///     fingering move to the new frets while the note heads under it stay put
    ///     (owner: "edited a handshape, the fingering detached from the notes").
    ///     Chords of another shape, and anything outside every bar, are left alone.
    /// </summary>
    private List<Chord> EnclosedUnbound(IReadOnlyList<HandShape> handShapes)
    {
        var result = new List<Chord>();
        if (handShapes.Count == 0 || !_templateIsReal) return result;

        foreach (var chord in _activeLevelChords)
        {
            if (chord.TemplateId == _loaded.Id) continue;
            if (!ChordTemplateEditPlanner.TemplateMatchesChord(_loaded, chord)) continue;
            if (handShapes.Any(h => chord.Position >= h.Position && chord.Position < h.EndPosition))
                result.Add(chord);
        }

        return result;
    }

    private static ChordApplySounds Narrow(ChordApplySounds sounds, ChordTemplate loaded, Arrangement arrangement) =>
        new(ChordTemplateEditPlanner.ChordsCarryingDisplayedShape([.. sounds.Chords], loaded, arrangement),
            ChordTemplateEditPlanner.HandShapesCarryingDisplayedShape([.. sounds.HandShapes], loaded, arrangement));
}
