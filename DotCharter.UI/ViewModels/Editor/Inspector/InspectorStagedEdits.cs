using System.ComponentModel;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The staged-edit engine behind the Note pages' pinned Apply (design D; decisions 1 and 2 of the
///     wiring plan): Placement, Selection, Technique and Bends edits are STAGED here and written by one Apply,
///     not the moment a control changes.
///     <list type="bullet">
///         <item>A draft per page, kept per source sound (the note or chord the inspector shows), so moving to
///         another sound and back finds it again. The Selection draft belongs to the selection: a selection
///         change drops it (the leave guard cannot hold a click on the timeline).</item>
///         <item><see cref="Plan" /> works out, writing nothing, which targets the page's scope reaches (a target
///         that already holds every staged value is not counted), which it passes by and why, which it writes
///         only in part, and which authored bends it replaces; <see cref="Review" /> says it in words.</item>
///         <item><see cref="Apply" /> writes exactly the plan as ONE history node, on every level the scope
///         covers, and drops the page's draft; <see cref="Discard" /> drops it and writes nothing;
///         <see cref="CanLeave" /> is the guard a page asks before it is left.</item>
///         <item>A history change under a draft (an undo, a timeline or keyboard edit, which stay immediate)
///         re-reads the baselines: a staged placement field the note now holds is dropped, a staged technique value
///         the sound has since been given is dropped (so Apply never reverts the keyboard), a bend's baseline follows
///         the sound: a move or a sustain change carries the staged curve along (it is kept relative to the sound's span),
///         a keyboard edit of the bend itself drops the draft and says so on the status rail, and a draft whose sound left
///         the project goes.</item>
///         <item>A chord keeps one bend draft per string, so switching the plot's string keeps the other's staged curve;
///         Apply writes every staged string.</item>
///         <item>Another level or arrangement keeps the drafts (each is on its own sound); the footer names them and
///         goes back to them (<see cref="DraftsElsewhere" />, <see cref="Locate" />). Another project drops every draft.
///         A held draft is unsaved work: the editor's <c>HasPendingDrafts</c> makes the close and replace prompts ask.</item>
///     </list>
///     The rules are the pure <see cref="StagedPlacement" />, <see cref="StagedBulk" />,
///     <see cref="StagedTechnique" /> and <see cref="StagedBends" />. Plain methods until the pages bind them (P4, P5).
/// </summary>
public sealed class InspectorStagedEdits : IDisposable
{
    private readonly ChartEditorViewModel _editor;
    private readonly IHistoryService _history;
    private readonly IHighwayEditingService _highway;
    private readonly ILocalizationService _localization;
    private readonly ProjectState _projectState;
    private readonly Func<int> _gridDivisor;
    private readonly Func<IReadOnlyList<Chord>> _chordScope;
    private readonly Dictionary<GuitarNote, PlacementDraft> _placement = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ITimedElement, TechniqueDraft> _technique = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ITimedElement, Dictionary<int, BendDraft>> _bends = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<StagedKey, int?> NoTechnique = [];
    private HashSet<ITimedElement> _bulkSelection = new(ReferenceEqualityComparer.Instance);
    private ProjectSoundIndex? _index;
    private ITimedElement? _sourceOverride;

    // The last "draft dropped" line Rebase put on the status rail, so an Apply that caused it keeps it.
    private string? _droppedMessage;
    private int _lastNode;

    /// <summary>Builds the engine over the editor's chart and selection. <paramref name="projectState" /> gives
    /// the string mask and the audio length, <paramref name="gridDivisor" /> the timeline's grid (the sustain
    /// step), <paramref name="chordScope" /> the chords a chord technique or bend reaches (the "Apply to" rung).</summary>
    public InspectorStagedEdits(ChartEditorViewModel editor, IHistoryService history, IHighwayEditingService highway,
        ILocalizationService localization, ProjectState projectState, Func<int> gridDivisor,
        Func<IReadOnlyList<Chord>> chordScope)
    {
        _editor = editor;
        _history = history;
        _highway = highway;
        _localization = localization;
        _projectState = projectState;
        _gridDivisor = gridDivisor;
        _chordScope = chordScope;
        _lastNode = history.CurrentNodeId;
        _history.HistoryChanged += Rebase;
        _editor.PropertyChanged += OnEditorChanged;
        _editor.Selection.SelectionChanged += OnSelectionChanged;
        _projectState.PropertyChanged += OnProjectStateChanged;
        _editor.AddPendingDraft(() => HasAnyDraft, () => ApplyAll());
    }

    /// <summary>Settings ▸ Editor's "Inspector edits wait for Apply": on, a page's edits stay a draft until the
    /// footer's Apply; off (the default), the pages write each edit at once through <see cref="ApplyNow" />.</summary>
    public bool ApplyStep => _projectState.InspectorApplyStep;

    /// <summary>Any page holds a draft on any sound.</summary>
    public bool HasAnyDraft => _placement.Count > 0 || _technique.Values.Any(d => d.IsLive) || _bends.Count > 0 || !Bulk.IsEmpty;

    /// <summary>The sound the pages show and stage on: the selected note, else the selected chord (or, while
    /// <see cref="ApplyAll" /> writes a draft left on another sound, that sound).</summary>
    public ITimedElement? Source => _sourceOverride ?? Selected;

    private ITimedElement? Selected => (ITimedElement?)_editor.Selection.SelectedNote ?? _editor.Selection.SelectedChord;

    /// <summary>How many times the project's sound index was built (a test reads it: an in-place edit reuses it).</summary>
    internal int SoundIndexBuilds { get; private set; }

    /// <summary>Which selected sounds the Selection page writes.</summary>
    public BulkSource BulkSource { get; set; } = BulkSource.NotesAndChords;

    /// <summary>A note's Technique scope: the note alone, or every selected note.</summary>
    public bool TechniqueOnSelectedNotes { get; set; }

    /// <summary>The Selection page's staged values.</summary>
    public BulkDraft Bulk { get; private set; } = BulkDraft.None;

    public PlacementDraft? Placement => Source is GuitarNote note ? _placement.GetValueOrDefault(note) : null;

    public IReadOnlyDictionary<StagedKey, int?> Technique =>
        Source is { } source && _technique.TryGetValue(source, out var draft) ? draft.Values : NoTechnique;

    public bool TechniqueReset => Source is { } source && _technique.TryGetValue(source, out var draft) && draft.Reset;

    /// <summary>A bend draft on the source (the note's, or a chord's first staged string).</summary>
    public BendDraft? Bend => Bends.FirstOrDefault();

    /// <summary>Every bend draft on the source: a note's one, a chord's one per staged string.</summary>
    public IReadOnlyList<BendDraft> Bends =>
        Source is { } source && _bends.TryGetValue(source, out var drafts) ? [.. drafts.Values.OrderBy(d => d.StringIndex)] : [];

    /// <summary>The bend draft on the source's <paramref name="stringIndex" /> (null for a note).</summary>
    public BendDraft? BendOn(int? stringIndex) =>
        Source is { } source && _bends.TryGetValue(source, out var drafts) ? drafts.GetValueOrDefault(BendKey(stringIndex)) : null;

    private static int BendKey(int? stringIndex) => stringIndex ?? -1;

    private int Capo => Math.Max(0, _editor.ActiveArrangement?.Capo ?? 0);

    private int StringCount => _editor.ActiveArrangement?.Tuning.Length ?? 6;

    private StringEditMask Mask => _projectState.StringEditMask;

    private int GridDivisor => Math.Max(1, _gridDivisor());

    // ---- staging ------------------------------------------------------------

    /// <summary>Stages the note's placement; a field the note already holds is not staged.</summary>
    public void StagePlacement(PlacementDraft draft)
    {
        if (Source is not GuitarNote note) return;
        var staged = draft.Without(note);
        if (staged.IsEmpty) _placement.Remove(note);
        else _placement[note] = staged;
    }

    /// <summary>Stages the Selection page's values for the selection as it is now.</summary>
    public void StageBulk(BulkDraft draft)
    {
        Bulk = draft;
        _bulkSelection = SelectedSounds();
    }

    /// <summary>Stages one technique value on the source; a key the source cannot carry is ignored. A value the
    /// source already holds still stages: the scope may reach sounds that do not.</summary>
    public void StageTechnique(StagedKey key, int? value)
    {
        if (Source is not { } source || !StagedTechnique.Accepts(key, source is Chord)) return;
        var draft = DraftOf(source);
        draft.Parked.Remove(key);
        draft.Values[key] = value;
        draft.Baselines.TryAdd(key, StagedTechnique.Read(source, key, out _));
    }

    /// <summary>Stages "Reset": every technique on each target is cleared before the staged values land.</summary>
    public void ResetTechnique()
    {
        if (Source is not { } source) return;
        var draft = DraftOf(source);
        draft.Values.Clear();
        draft.Baselines.Clear();
        draft.Parked.Clear();
        draft.Reset = true;
    }

    /// <summary>Stages a bend curve on the source's string (null for a note); each string of a chord keeps its own
    /// draft. The first stage on a string remembers the curve it held and the sound's span, so an untouched curve writes
    /// nothing and a later move or sustain change carries the draft along.</summary>
    public void StageBend(int? stringIndex, IReadOnlyList<BendValue> values)
    {
        if (Source is not { } source || StagedBends.Curve(source, stringIndex) is not { } held ||
            StagedBends.Span(source, stringIndex) is not { } span)
        {
            return;
        }

        var open = BendOn(stringIndex);
        var draft = new BendDraft(stringIndex, Copy(values), open?.Baseline ?? Copy(held), span.Onset, span.Sustain);
        SetBend(source, draft.IsEdited ? draft : null, stringIndex);
    }

    private void SetBend(ITimedElement source, BendDraft? draft, int? stringIndex)
    {
        if (!_bends.TryGetValue(source, out var drafts))
        {
            if (draft == null) return;
            _bends[source] = drafts = [];
        }

        if (draft == null) drafts.Remove(BendKey(stringIndex));
        else drafts[BendKey(stringIndex)] = draft;
        if (drafts.Count == 0) _bends.Remove(source);
    }

    // ---- reading ------------------------------------------------------------

    public bool HasDraft(StagedPage page) => page switch
    {
        StagedPage.Placement => Placement is not null,
        StagedPage.Selection => !Bulk.IsEmpty,
        StagedPage.Technique => Source is { } source && _technique.TryGetValue(source, out var draft) && draft.IsLive,
        _ => Bends.Any(d => d.IsEdited)
    };

    /// <summary>The other sounds that hold a Placement, Technique or Bends draft, in time order (the Selection draft
    /// belongs to its selection and is never elsewhere).</summary>
    public IReadOnlyList<ITimedElement> DraftsElsewhere()
    {
        if (_placement.Count == 0 && _technique.Count == 0 && _bends.Count == 0) return [];
        var source = Source;
        var sounds = new HashSet<ITimedElement>(ReferenceEqualityComparer.Instance);
        foreach (var sound in DraftSounds())
        {
            if (!ReferenceEquals(sound, source)) sounds.Add(sound);
        }

        return [.. sounds.OrderBy(SoundQueueViewModel.PositionOf)];
    }

    /// <summary>Every sound that holds a Placement, a live Technique or a Bends draft.</summary>
    private IEnumerable<ITimedElement> DraftSounds() => _placement.Keys
        .Concat<ITimedElement>(_technique.Where(d => d.Value.IsLive).Select(d => d.Key)).Concat(_bends.Keys);

    /// <summary>The arrangement and level <paramref name="sound" /> sits on, or null when it is not in the project.</summary>
    internal static (Arrangement Arrangement, ChartLevel Level)? Locate(ChartProject? project, ITimedElement sound)
    {
        foreach (var arrangement in project?.Arrangements ?? [])
        {
            foreach (var level in arrangement.Levels)
            {
                var here = sound switch
                {
                    GuitarNote note => level.Notes.Contains(note),
                    Chord chord => level.Chords.Contains(chord),
                    _ => false
                };
                if (here) return (arrangement, level);
            }
        }

        return null;
    }

    /// <summary>The leave guard: a page holding a draft asks Stay / Discard / Apply first. The Selection page's
    /// draft never outlives its selection: a selection change drops it without asking.</summary>
    public bool CanLeave(StagedPage page) => !HasDraft(page);

    /// <summary>The sounds the page's scope reaches, before any is passed by.</summary>
    public IReadOnlyList<ITimedElement> Targets(StagedPage page) => (page, Source) switch
    {
        (not StagedPage.Selection, { } other) when _sourceOverride is not null && !ReferenceEquals(other, Selected) => [other],
        (StagedPage.Placement, GuitarNote note) => [note],
        (StagedPage.Selection, _) => StagedBulk.Targets(_editor.Selection.SelectedNotes, _editor.Selection.SelectedChords, BulkSource),
        (StagedPage.Technique, GuitarNote note) => TechniqueOnSelectedNotes ? [.. _editor.Selection.SelectedNotes] : [note],
        (StagedPage.Bends, GuitarNote note) => [note],
        (StagedPage.Technique or StagedPage.Bends, Chord) => [.. _chordScope()],
        _ => []
    };

    /// <summary>What Apply would write, pass by and replace on the page. Writes nothing.</summary>
    public StagedEditPlan Plan(StagedPage page)
    {
        var source = Source;
        var plan = page switch
        {
            StagedPage.Placement => Placement is { } placement ? StagedPlacement.Plan(source, placement, Capo, StringCount) : StagedEditPlan.Empty,
            StagedPage.Selection => StagedBulk.Plan(Bulk, Targets(page), Capo, Mask),
            StagedPage.Technique when source is not null && _technique.TryGetValue(source, out var draft) =>
                StagedTechnique.Plan(source, draft.Values, draft.Reset, Targets(page), Capo),
            StagedPage.Bends when source is not null && Bends is { Count: > 0 } bends => StagedBends.Plan(source, bends, Targets(page)),
            _ => StagedEditPlan.Empty
        };
        return plan.Where(target => Changes(page, target));
    }

    /// <summary>True when the page's write changes <paramref name="target" /> (the history node's own test).</summary>
    private bool Changes(StagedPage page, ITimedElement target)
    {
        switch (target)
        {
            case GuitarNote note when NoteWrite(page, note) is { } write:
                var after = note.Clone();
                write(after);
                return !ChartActionStateComparer.GuitarNotesEqual(note, after);
            case Chord chord when ChordWrite(page, chord) is { } write:
                var changed = chord.Clone();
                write(changed);
                return !ChartActionStateComparer.ChordsEqual(chord, changed);
            default:
                return false;
        }
    }

    /// <summary>The footer's sentence for the page's plan and whether Apply asks first.</summary>
    public StagedEditReview Review(StagedPage page) => Describe(Plan(page));

    /// <summary>What a Selection "Move" step would reach and leave where it is. A sustain step is
    /// <paramref name="delta" /> steps of the timeline's grid, the keyboard's sustain keys.</summary>
    public StagedEditPlan PlanMove(BulkMove move, int delta) => move == BulkMove.Sustain
        ? delta == 0 ? StagedEditPlan.Empty : StagedBulk.PlanSustain(_highway.PreviewSelectedNotesSustain(delta,
            _projectState.TotalAudioLengthMs, GridDivisor, _editor.Beats))
        : StagedBulk.PlanMove(move, delta, StagedBulk.Targets(_editor.Selection.SelectedNotes, _editor.Selection.SelectedChords,
            BulkSource.NotesAndChords), Capo, StringCount);

    /// <summary>The sentence for a Move step.</summary>
    public StagedEditReview ReviewMove(BulkMove move, int delta) => Describe(PlanMove(move, delta));

    // ---- writing ------------------------------------------------------------

    /// <summary>Writes the page's plan as ONE history node and drops its draft. False when nothing landed.</summary>
    public bool Apply(StagedPage page)
    {
        var plan = Plan(page);
        var action = plan.Count == 0 ? null : Build(page, plan);
        if (action == null) return false;
        var message = Applied(page, plan);
        Discard(page);
        _droppedMessage = null;
        _history.Do(action);
        // The write may drop another sound's bend draft (Rebase says so first): keep that line beside the Apply's.
        _projectState.SetMessage(_droppedMessage is { } dropped ? $"{message} · {dropped}" : message);
        return true;
    }

    /// <summary>What the status rail says after an Apply: "Bend applied to 14 chords on 3 shapes", "Technique applied to
    /// 2 notes", "Placement applied to 1 sound" (notes and chords together).</summary>
    private string Applied(StagedPage page, StagedEditPlan plan)
    {
        var what = _localization[page switch
        {
            StagedPage.Placement => "Inspector_Staged_Applied_Placement",
            StagedPage.Selection => "Inspector_Staged_Applied_Selection",
            StagedPage.Technique => "ChordTemplate_Applied_Technique",
            _ => "Inspector_Staged_Applied_Bend"
        }];
        var chords = plan.Writes.OfType<Chord>().ToList();
        if (chords.Count == plan.Count)
        {
            return _localization.Format("ChordTemplate_Applied", what,
                _localization.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", chords.Count),
                _localization.Plural("ChordTemplate_Applied_ShapesOne", "ChordTemplate_Applied_Shapes",
                    chords.Select(c => c.TemplateId).Distinct().Count()));
        }

        return _localization.Format("Inspector_Staged_AppliedTo", what, chords.Count == 0
            ? _localization.Plural("Inspector_Staged_AppliedNotesOne", "Inspector_Staged_AppliedNotes", plan.Count)
            : _localization.Plural("Inspector_Staged_AppliedSoundsOne", "Inspector_Staged_AppliedSounds", plan.Count));
    }

    /// <summary>The immediate path (<see cref="ApplyStep" /> off): writes the page's draft at once through
    /// <see cref="Apply" /> (the same plan, targets and skips; one undo step) and drops it even when nothing landed, so
    /// no draft is ever left behind. A draft that lands nowhere puts its reason on the status rail. False when nothing landed.</summary>
    public bool ApplyNow(StagedPage page)
    {
        if (Apply(page)) return true;
        var review = Review(page);
        Discard(page);
        if (review.Text.Length > 0) _projectState.SetMessage(review.Text);
        return false;
    }

    /// <summary>Drops the page's draft and writes nothing.</summary>
    public void Discard(StagedPage page)
    {
        switch (page)
        {
            case StagedPage.Placement when Source is GuitarNote note:
                _placement.Remove(note);
                break;
            case StagedPage.Selection:
                Bulk = BulkDraft.None;
                break;
            case StagedPage.Technique when Source is { } source:
                _technique.Remove(source);
                break;
            case StagedPage.Bends when Source is { } source:
                _bends.Remove(source);
                break;
        }
    }

    /// <summary>Writes every draft on every page and sound as ONE history node and drops them all: the close or replace
    /// prompt's Save, so a held draft reaches the file instead of being dropped. A draft left on another sound writes
    /// that sound only (the scope it was staged under belongs to its own selection). False when nothing landed.</summary>
    public bool ApplyAll()
    {
        var notes = new Dictionary<GuitarNote, Action<GuitarNote>>(ReferenceEqualityComparer.Instance);
        var chords = new Dictionary<Chord, Action<Chord>>(ReferenceEqualityComparer.Instance);
        if (!Bulk.IsEmpty) Gather(StagedPage.Selection, Plan(StagedPage.Selection), notes, chords);
        foreach (var sound in DraftSounds().Distinct<ITimedElement>(ReferenceEqualityComparer.Instance).ToArray())
        {
            _sourceOverride = sound;
            try
            {
                foreach (var page in (StagedPage[])[StagedPage.Placement, StagedPage.Technique, StagedPage.Bends])
                {
                    if (HasDraft(page)) Gather(page, Plan(page), notes, chords);
                }
            }
            finally
            {
                _sourceOverride = null;
            }
        }

        DiscardAll();
        return Compose(notes, chords, everyLevel: true) is { } action && _history.Do(action);
    }

    /// <summary>Drops every draft on every page and sound.</summary>
    public void DiscardAll()
    {
        _placement.Clear();
        _technique.Clear();
        _bends.Clear();
        Bulk = BulkDraft.None;
    }

    /// <summary>One Move step on the selection, ONE history node; a sound it would push off the neck stays.</summary>
    public bool ApplyMove(BulkMove move, int delta)
    {
        if (PlanMove(move, delta).Count == 0) return false;
        switch (move)
        {
            case BulkMove.Fret:
                _highway.MoveSelectedFretsWhole(delta);
                return true;
            case BulkMove.String when delta == 1:
                _highway.MoveSelectedStringUp(StringCount);
                return true;
            case BulkMove.String when delta == -1:
                _highway.MoveSelectedStringDown();
                return true;
            case BulkMove.Sustain:
                return _highway.AdjustSelectedNotesSustain(delta, _projectState.TotalAudioLengthMs, GridDivisor, _editor.Beats);
            default:
                return false;
        }
    }

    private IChartAction? Build(StagedPage page, StagedEditPlan plan)
    {
        var notes = new Dictionary<GuitarNote, Action<GuitarNote>>(ReferenceEqualityComparer.Instance);
        var chords = new Dictionary<Chord, Action<Chord>>(ReferenceEqualityComparer.Instance);
        Gather(page, plan, notes, chords);
        return Compose(notes, chords);
    }

    /// <summary>The page's write per target; a target another page already writes runs both, in turn.</summary>
    private void Gather(StagedPage page, StagedEditPlan plan, Dictionary<GuitarNote, Action<GuitarNote>> notes,
        Dictionary<Chord, Action<Chord>> chords)
    {
        foreach (var target in plan.Writes)
        {
            if (target is GuitarNote note && NoteWrite(page, note) is { } noteWrite)
                notes[note] = notes.TryGetValue(note, out var earlier) ? earlier + noteWrite : noteWrite;
            else if (target is Chord chord && ChordWrite(page, chord) is { } chordWrite)
                chords[chord] = chords.TryGetValue(chord, out var before) ? before + chordWrite : chordWrite;
        }
    }

    private Action<GuitarNote>? NoteWrite(StagedPage page, GuitarNote target) => (page, Source) switch
    {
        (StagedPage.Placement, GuitarNote source) when Placement is { } draft => StagedPlacement.Write(source, draft),
        (StagedPage.Selection, _) => StagedBulk.NoteWrite(Bulk, Capo),
        (StagedPage.Technique, { } source) when _technique.TryGetValue(source, out var draft) =>
            StagedTechnique.NoteWrite(StagedTechnique.Landing(source, draft.Values, draft.Reset, target, Capo), draft.Reset),
        (StagedPage.Bends, GuitarNote) when Bend is { } bend => StagedBends.NoteWrite(bend),
        _ => null
    };

    private Action<Chord>? ChordWrite(StagedPage page, Chord target) => (page, Source) switch
    {
        (StagedPage.Selection, _) => StagedBulk.ChordWrite(Bulk, Capo, Mask),
        (StagedPage.Technique, { } source) when _technique.TryGetValue(source, out var draft) =>
            StagedTechnique.ChordWrite(StagedTechnique.Landing(source, draft.Values, draft.Reset, target, Capo), draft.Reset),
        (StagedPage.Bends, Chord) when Bends is { Count: > 0 } bends => StagedBends.ChordWrite(bends),
        _ => null
    };

    /// <summary>The notes' mutation and one mutation per level the chords sit on, folded into one node. With
    /// <paramref name="everyLevel" /> (<see cref="ApplyAll" />) the notes are grouped by the level they sit on and the
    /// chords looked up on every arrangement's levels, since a draft left elsewhere may be on any of them.</summary>
    private IChartAction? Compose(Dictionary<GuitarNote, Action<GuitarNote>> notes, Dictionary<Chord, Action<Chord>> chords,
        bool everyLevel = false)
    {
        var postPass = _highway.GetLinkNextUpdater();
        var actions = new List<IChartAction>();
        foreach (var onLevel in notes.Keys.GroupBy(n => everyLevel ? Index().LevelOf(n) : null))
            actions.Add(NoteAction(onLevel.Key, [.. onLevel], notes, postPass));

        if (chords.Count > 0)
        {
            var levels = everyLevel ? Index().Levels : _editor.ActiveArrangement?.Levels ?? [];
            actions.AddRange(ChordTechniqueEdit.BuildPerChord(levels, _editor.PlacedChords, chords, postPass,
                _highway.GetLevelSpacingUpdater));
        }

        var effective = actions.Where(a => a.HasEffect).ToList();
        return effective.Count switch
        {
            0 => null,
            1 => effective[0],
            _ => new CompositeAction(effective)
        };
    }

    /// <summary>One mutation over the notes of one level: the level on screen (or none known) through the placed
    /// notes and the link pass, another level through its own notes and spacing pass.</summary>
    private MutateNotesAction NoteAction(ChartLevel? level, List<GuitarNote> targets,
        Dictionary<GuitarNote, Action<GuitarNote>> notes, Action? postPass)
    {
        var newStates = targets.Select(n =>
        {
            var clone = n.Clone();
            notes[n](clone);
            return clone;
        }).ToList();
        var oldStates = targets.Select(n => n.Clone()).ToList();
        return level is null || ReferenceEquals(level.Notes, _editor.PlacedNotes)
            ? new MutateNotesAction(_editor.PlacedNotes, targets, oldStates, newStates, postPass)
            : new MutateNotesAction(level.Notes, targets, oldStates, newStates, _highway.GetLevelSpacingUpdater(level));
    }

    // ---- review -------------------------------------------------------------

    private StagedEditReview Describe(StagedEditPlan plan)
    {
        if (plan.Count == 0 && plan.Skips.Count == 0) return StagedEditReview.None;
        var parts = new List<string> { _localization.Plural("Inspector_Staged_ApplyOne", "Inspector_Staged_Apply", plan.Count) };
        if (plan.Partly.Count > 0)
            parts.Add(_localization.Format("Inspector_Staged_Partly", string.Join(", ", plan.Partly.Select(SkipText))));
        if (plan.Skips.Count > 0)
            parts.Add(_localization.Format("Inspector_Staged_Skips", string.Join(", ", plan.Skips.Select(SkipText))));
        if (plan.Replacements.Count > 0)
            parts.Add(_localization.Format("Inspector_Staged_Replaces", string.Join(", ", plan.Replacements.Select(Stamp))));
        var confirm = plan.Count > 1 || plan.Skips.Count > 0 || plan.Partly.Count > 0 || plan.Replacements.Count > 0;
        return new StagedEditReview(string.Join(" ", parts), confirm, plan.Count);
    }

    private string SkipText(StagedSkip skip) => skip.Reason == StagedSkipReason.StringSilent && skip.StringIndex is { } index
        ? _localization.Format("Inspector_Staged_SkipStringSilent", Stamp(skip.Target), StringName(index))
        : _localization.Format(skip.Reason switch
        {
            StagedSkipReason.SlideOffNeck => "Inspector_Staged_SkipSlideOffNeck",
            StagedSkipReason.LeavesNeck => "Inspector_Staged_SkipLeavesNeck",
            StagedSkipReason.SustainFloor => "Inspector_Staged_SkipSustainFloor",
            StagedSkipReason.SustainNextSound => "Inspector_Staged_SkipSustainNextSound",
            StagedSkipReason.SustainAudioEnd => "Inspector_Staged_SkipSustainAudioEnd",
            StagedSkipReason.SustainLinked => "Inspector_Staged_SkipSustainLinked",
            _ => "Inspector_Staged_SkipNothingLands"
        }, Stamp(skip.Target));

    /// <summary>A target named by its time, to the millisecond ("0:12.500").</summary>
    private string Stamp(ITimedElement target) => ElapsedTimeLabel.Format(target.GetTimeMs(_editor.Beats), 3);

    private string StringName(int index) => GuitarStringLabel.Format(index, StringCount,
        TuningDisplayNames.For(_editor.ActiveArrangement), _localization["Inspector_StringLabelFmt"]);

    // ---- baselines ----------------------------------------------------------

    /// <summary>Re-reads every draft against the chart after a history change (see the type's summary).</summary>
    public void Rebase()
    {
        var before = _lastNode;
        _lastNode = _history.CurrentNodeId;
        if (_placement.Count == 0 && _technique.Count == 0 && _bends.Count == 0) return;
        var inProject = Index();

        foreach (var (note, draft) in _placement.ToArray())
        {
            var staged = draft.Without(note);
            if (!inProject.Contains(note) || staged.IsEmpty) _placement.Remove(note);
            else _placement[note] = staged;
        }

        foreach (var (source, draft) in _technique.ToArray())
        {
            if (!inProject.Contains(source) || !RebaseTechnique(source, draft, before, _lastNode)) _technique.Remove(source);
        }

        var dropped = new List<ITimedElement>();
        foreach (var (source, drafts) in _bends.ToArray())
        {
            var here = inProject.Contains(source);
            foreach (var bend in drafts.Values.ToArray())
            {
                var edited = false;
                var rebased = here ? StagedBends.Rebase(source, bend, out edited) : null;
                if (edited) dropped.Add(source);
                SetBend(source, rebased is { IsEdited: true } ? rebased : null, bend.StringIndex);
            }
        }

        if (dropped.Count > 0)
        {
            _droppedMessage = _localization.Format("Inspector_Bends_DraftDropped",
                string.Join(", ", dropped.Distinct(ReferenceEqualityComparer.Instance).Cast<ITimedElement>().Select(Stamp)));
            _projectState.SetMessage(_droppedMessage);
        }
    }

    /// <summary>A staged value the sound has since been given (it differed when staged) is PARKED, with the history
    /// node before that edit: Apply would otherwise write it over whatever the keyboard does next. Undoing back to that
    /// node, the sound holding its baseline again, brings it back; a later edit that only looks the same does not.
    /// False when nothing is left, parked or staged.</summary>
    private static bool RebaseTechnique(ITimedElement source, TechniqueDraft draft, int before, int now)
    {
        foreach (var (key, (value, baseline, parkedFrom)) in draft.Parked.ToArray())
        {
            if (now != parkedFrom || StagedTechnique.Read(source, key, out _) != baseline) continue;
            draft.Parked.Remove(key);
            draft.Values[key] = value;
            draft.Baselines[key] = baseline;
        }

        foreach (var (key, value) in draft.Values.ToArray())
        {
            var held = StagedTechnique.Read(source, key, out _);
            var baseline = draft.Baselines.GetValueOrDefault(key);
            if (held != value || baseline == value) continue;
            draft.Values.Remove(key);
            draft.Baselines.Remove(key);
            draft.Parked[key] = (value, baseline, before);
        }

        return draft.Reset || draft.Values.Count > 0 || draft.Parked.Count > 0;
    }

    /// <summary>Which level every sound of the project sits on: built once, kept until a level gains, loses or swaps a
    /// sound or the project's levels change (<see cref="ProjectSoundIndex" />).</summary>
    private ProjectSoundIndex Index()
    {
        var project = _projectState.ActiveProject;
        if (_index is { } index && index.IsCurrentFor(project)) return index;
        _index?.Dispose();
        SoundIndexBuilds++;
        return _index = new ProjectSoundIndex(project);
    }

    /// <summary>Another project drops every draft; another level or arrangement keeps them (a draft whose sound
    /// left the project goes).</summary>
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectState.ActiveProject))
        {
            DiscardAll();
            _index?.Dispose();
            _index = null;
            return;
        }

        if (e.PropertyName is nameof(ChartEditorViewModel.PlacedNotes) or nameof(ChartEditorViewModel.PlacedChords)
            or nameof(ChartEditorViewModel.ActiveArrangement))
        {
            Prune();
        }
    }

    private void Prune()
    {
        if (_placement.Count == 0 && _technique.Count == 0 && _bends.Count == 0) return;
        var inProject = Index();
        foreach (var note in _placement.Keys.Where(n => !inProject.Contains(n)).ToArray()) _placement.Remove(note);
        foreach (var source in _technique.Keys.Where(s => !inProject.Contains(s)).ToArray()) _technique.Remove(source);
        foreach (var source in _bends.Keys.Where(s => !inProject.Contains(s)).ToArray()) _bends.Remove(source);
    }

    /// <summary>The Apply step switched off with a draft waiting: write it (never drop it silently).</summary>
    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectState.InspectorApplyStep) && !ApplyStep && HasAnyDraft) ApplyAll();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!Bulk.IsEmpty && !_bulkSelection.SetEquals(SelectedSounds())) Bulk = BulkDraft.None;
    }

    private HashSet<ITimedElement> SelectedSounds() => new(
        [.. _editor.Selection.SelectedNotes, .. _editor.Selection.SelectedChords], ReferenceEqualityComparer.Instance);

    private TechniqueDraft DraftOf(ITimedElement source)
    {
        if (!_technique.TryGetValue(source, out var draft)) _technique[source] = draft = new TechniqueDraft();
        return draft;
    }

    private static List<BendValue> Copy(IEnumerable<BendValue> values) => [.. values.Select(v => new BendValue(v.Position, v.Step))];

    public void Dispose()
    {
        _history.HistoryChanged -= Rebase;
        _editor.PropertyChanged -= OnEditorChanged;
        _editor.Selection.SelectionChanged -= OnSelectionChanged;
        _projectState.PropertyChanged -= OnProjectStateChanged;
        _index?.Dispose();
    }

    private sealed class TechniqueDraft
    {
        public Dictionary<StagedKey, int?> Values { get; } = [];

        /// <summary>What the source held for each key when it was first staged.</summary>
        public Dictionary<StagedKey, int?> Baselines { get; } = [];

        /// <summary>Staged values the keyboard has since written (see <c>RebaseTechnique</c>): each with its baseline
        /// and the history node an undo returns to.</summary>
        public Dictionary<StagedKey, (int? Value, int? Baseline, int Before)> Parked { get; } = [];

        public bool Reset { get; set; }

        /// <summary>Something for Apply to write: parked values alone are not a draft.</summary>
        public bool IsLive => Reset || Values.Count > 0;
    }
}
