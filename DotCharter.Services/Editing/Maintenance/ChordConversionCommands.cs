using DotCharter.Localization;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
/// The verbs that turn notes into chords and back, set a chord's flags or fingering, and convert
///     muted strums: each reads the selection, plans, commits ONE undo node through the history
///     and says what happened. A collaborator <see cref="ChartMaintenanceService" /> owns and
///     forwards to; it holds none of that class's snapshot machinery.
/// </summary>
internal sealed class ChordConversionCommands(
    ProjectState projectState,
    IChartEditorSurface chartEditor,
    IHistoryService historyService,
    ILocalizationService localization,
    IAppLogger logger)
{
    private readonly IChartEditorSurface _chartEditor = chartEditor;
    private readonly IHistoryService _historyService = historyService;
    private readonly ILocalizationService _localization = localization;
    private readonly IAppLogger _logger = logger;
    private readonly ProjectState _projectState = projectState;

    public void ToggleTwoFretOctaveFingering() => ToggleTwoFretPairFingering(TwoFretPair.TwoFretOctave,
        new PairFingeringKeys("Status_OctaveFingeringNone", "Status_OctaveFingeringAlready",
            "Status_OctaveFingeringSetOne", "Status_OctaveFingeringSet", "History_OctaveFingering"));

    public void TogglePowerChordFingering() => ToggleTwoFretPairFingering(TwoFretPair.PowerChord,
        new PairFingeringKeys("Status_PowerChordFingeringNone", "Status_PowerChordFingeringAlready",
            "Status_PowerChordFingeringSetOne", "Status_PowerChordFingeringSet", "History_PowerChordFingering"));

    // every target template of the pair in the active arrangement,
    // edited in place (no fret moves, the index stays on the root) in ONE undo
    // node.
    private void ToggleTwoFretPairFingering(TwoFretPair pair, PairFingeringKeys keys)
    {
        var arrangement = _projectState.ActiveArrangement;
        if (arrangement == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return;
        }

        var targets = TwoFretPairFingering.Targets(arrangement, pair);
        if (targets.Count == 0)
        {
            _projectState.MainDisplayMessage = L(keys.None);
            return;
        }

        var next = TwoFretPairFingering.Next(arrangement, pair, targets);
        var actions = new List<IChartAction>();
        var changedIds = new HashSet<int>();
        foreach (var template in targets)
        {
            TwoFretPairFingering.Classify(arrangement, template, out var low, out var high);
            var action = new SetTemplateFingersAction(template, new Dictionary<int, int>(template.Fingers),
                TwoFretPairFingering.FingersFor(next, low, high), _chartEditor);
            if (!action.HasEffect) continue;
            actions.Add(action);
            changedIds.Add(template.Id);
        }

        var label = next == PairFingering.IndexRing ? "1·3" : "1·4";
        if (actions.Count == 0)
        {
            _projectState.MainDisplayMessage = L(keys.Already, label);
            return;
        }

        if (!_historyService.Do(new CompositeAction(actions) { LabelKey = keys.History })) return;

        // Count the chords the user hears about, on every level: a template is
        // arrangement-wide, so the fingering changed for all of them.
        var chords = arrangement.Levels.Sum(level => level.Chords.Count(c => changedIds.Contains(c.TemplateId)));
        _projectState.MainDisplayMessage =
            _localization.Plural(keys.SetOne, keys.Set, chords, label);
        _logger.Info(AppLogCategory.Editing,
            $"{pair} fingering set to {label} on {actions.Count} template(s), {chords} chord(s), arrangement '{arrangement.Name}'.");
    }

    // muted single-note strums after a chord become fret-hand-muted copies of
    // it inside its hand shape (MutedStrumChords), as ONE undo node. The selection's
    // notes, or with nothing selected every note of the phrase under the playhead.
    public void ConvertMutedStrumsToChords()
    {
        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        if (arrangement == null || level == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoActiveLevel");
            return;
        }

        var selectedNotes = _chartEditor.SelectedNotes.Where(level.Notes.Contains).ToHashSet();
        var selected = selectedNotes.Select(n => n.Position)
            .Concat(_chartEditor.SelectedChords.Where(level.Chords.Contains).Select(c => c.Position))
            .ToList();
        HashSet<GuitarNote> candidates;
        Func<FractionalPosition, bool> inSpan;
        string scope;
        if (selected.Count > 0)
        {
            var first = selected.Min();
            var last = selected.Max();
            candidates = selectedNotes;
            inSpan = p => p.CompareTo(first) >= 0 && p.CompareTo(last) <= 0;
            scope = "selection";
        }
        else
        {
            var (start, end) = PhraseSpanAtPlayhead(arrangement);
            inSpan = p => (start is not { } s || p.CompareTo(s) >= 0) && (end is not { } e || p.CompareTo(e) < 0);
            candidates = level.Notes.Where(n => inSpan(n.Position)).ToHashSet();
            scope = start is { } at ? $"phrase at {FormatPosition(at)}" : "level";
        }

        var plan = ChordShapes.MutedStrumChords.Plan(arrangement, level, candidates, inSpan);
        if (plan.Action is not { } action)
        {
            _projectState.MainDisplayMessage = L("Status_MutedStrums_None");
            return;
        }

        if (!_historyService.Do(action)) return;

        // The converted notes left the level; a selection still pointing at them dangles.
        _chartEditor.Selection.ClearAll();
        _chartEditor.RequestRedraw();
        _projectState.MainDisplayMessage =
            _localization.Plural("Status_MutedStrums_ConvertedOne", "Status_MutedStrums_Converted", plan.Converted);
        _logger.Info(AppLogCategory.Editing,
            $"Muted strums to chord ({scope}): {plan.Converted} strum(s) converted, {plan.MintedTemplates} template(s) minted, " +
            $"{plan.HandShapesExtended} hand shape(s) extended, {plan.HandShapesAdded} added, arrangement '{arrangement.Name}'.");
    }

    /// <summary>The span of the phrase iteration under the playhead (the last one at or before it, else
    /// the first), end exclusive; both null when the arrangement has no phrase iterations.</summary>
    private (FractionalPosition? Start, FractionalPosition? End) PhraseSpanAtPlayhead(Arrangement arrangement)
    {
        var ordered = arrangement.PhraseIterations.OrderBy(pi => pi.Position).ToList();
        if (ordered.Count == 0) return (null, null);
        var playhead = TimelineMath.PositionFromMs(_projectState.CurrentTimeMs, _chartEditor.Beats);
        var index = Math.Max(0, ordered.FindLastIndex(pi => pi.Position <= playhead));
        return (ordered[index].Position, index + 1 < ordered.Count ? ordered[index + 1].Position : null);
    }

    public void ConvertSelectedNotesToPowerChord(bool big)
    {
        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        if (arrangement == null || level == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoActiveLevel");
            return;
        }

        var requiredStrings = big ? 3 : 2;
        var maxStringIndex = arrangement.StringCount - requiredStrings;
        var selectedNotes = _chartEditor.SelectedNotes
            .Where(n => n.StringIndex >= 0 && n.StringIndex <= maxStringIndex)
            .ToList();
        if (selectedNotes.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_PowerChord_NoCandidates");
            return;
        }

        var newTemplates = new List<ChordTemplate>();
        var newChords = new List<Chord>();
        var notesToRemove = new List<GuitarNote>();

        foreach (var note in selectedNotes)
        {
            var frets = PowerChordFrets.Build(arrangement, note.StringIndex, note.Fret, big);
            if (frets == null) continue;

            // Find-or-create goes through the ONE documented editing resolver,
            // same as MergeSelectedNotesIntoChords above and for the same
            // reasons. This open-coded its own, keyed on ChordTemplate.Equals —
            // which is VALUE equality over every field, NAME and FINGERS
            // included. So the shape the chart already declares as "A5", with
            // the charter's fingering on it, never matched the bare shape built
            // here: converting a note minted a SECOND, nameless, unfingered
            // template at the identical frets, the chord adopted that one, and
            // the game showed it with no name and no fingers. The key is FRETS
            // within one ARPEGGIO FLAVOUR, a power chord is always the PLAIN
            // one, and a minted template arrives charting-ready (suggested
            // fingering + name) with its id allocated by the resolver — so
            // there is no local id arithmetic to drift either.
            var finalTemplate = ChordTemplateResolver.Resolve(
                arrangement, frets, ArpeggioFlavour.Plain, null, newTemplates);

            var chord = new Chord
            {
                Position = note.Position,
                TemplateId = finalTemplate.Id
            };
            foreach (var (s, f) in finalTemplate.Frets.OrderBy(kv => kv.Key))
            {
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = s,
                    Fret = f,
                    Sustain = note.Sustain,
                    Mute = note.Mute,
                    Hopo = note.Hopo,
                    BassPicking = note.BassPicking,
                    LinkNext = note.LinkNext,
                    Vibrato = note.Vibrato,
                    Tremolo = note.Tremolo
                });
            }

            newChords.Add(chord);
            notesToRemove.Add(note);
        }

        if (newChords.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_PowerChord_NoCandidates");
            return;
        }

        var actions = new List<IChartAction>();
        if (newTemplates.Count > 0)
            actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, newTemplates));
        actions.Add(new RemoveNotesAction(level.Notes, notesToRemove));
        actions.Add(new AddChordsAction(level.Chords, newChords));

        var composite = actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_PowerChord" };
        if (!_historyService.Do(composite)) return;

        _chartEditor.RequestRedraw();
        _projectState.MainDisplayMessage = big
            ? _localization.Plural("Status_PowerChordBigCreatedOne", "Status_PowerChordBigCreated", newChords.Count)
            : _localization.Plural("Status_PowerChordSmallCreatedOne", "Status_PowerChordSmallCreated", newChords.Count);
        _logger.Info(AppLogCategory.Editing,
            $"Converted {newChords.Count} note(s) to {(big ? "big" : "small")} power chord(s).");
    }

    /// <summary>
    ///     Merges the selected single notes into chords: notes sharing one
    ///     position (two or more, distinct strings) become one chord. The template
    ///     is find-or-create through <see cref="ChordTemplateResolver" /> — frets
    ///     within one arpeggio flavour, here always the PLAIN one — and a NEW
    ///     template arrives charting-ready with suggested fingering + name, the
    ///     same contract as the tab-import pass. One undoable action; notes that
    ///     don't form a chord stay untouched.
    /// </summary>
    public void MergeSelectedNotesIntoChords()
    {
        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        if (arrangement == null || level == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoActiveLevel");
            return;
        }

        var groups = _chartEditor.SelectedNotes
            .GroupBy(n => n.Position)
            .Select(g => g.DistinctBy(n => n.StringIndex).ToList())
            .Where(g => g.Count >= 2)
            .ToList();
        if (groups.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_MergeChord_NoCandidates");
            return;
        }

        var newTemplates = new List<ChordTemplate>();
        var newChords = new List<Chord>();
        var notesToRemove = new List<GuitarNote>();

        foreach (var group in groups)
        {
            var frets = group.ToDictionary(n => n.StringIndex, n => n.Fret);

            // Find-or-create goes through the ONE documented editing resolver. Its
            // key is FRETS within one ARPEGGIO FLAVOUR, and a flavour is BOTH flags:
            // the hand-rolled copy this replaces asked `!t.Arpeggio` alone, which is
            // TRUE for every DLC-Builder/EOF arpeggio (those spell themselves
            // ForceArpeggioInRs), so merging plain notes adopted an arpeggio template
            // that merely shared their frets — and the new chord then exported with
            // ChordMask.Arpeggio under an `-arp` name. Merged single notes are always
            // the PLAIN flavour. The resolver also mints charting-ready (suggested
            // fingering + name) and allocates the id, so there is no local id
            // arithmetic to drift either.
            var template = ChordTemplateResolver.Resolve(
                arrangement, frets, ArpeggioFlavour.Plain, null, newTemplates);

            var chord = new Chord
            {
                Position = group[0].Position,
                TemplateId = template.Id
            };
            // The sheet hint survives only when the merged notes agree on it.
            var durations = group.Select(n => n.NotationDuration).Distinct().ToList();
            if (durations.Count == 1) chord.NotationDuration = durations[0];

            foreach (var note in group.OrderBy(n => n.StringIndex))
            {
                var cn = new ChordNote
                {
                    StringIndex = note.StringIndex,
                    Fret = note.Fret,
                    // Notation-only grace ornament: per-string on a chord, so
                    // each merged note keeps its own
                    // (ChordNote has a per-string grace field).
                    GraceFret = note.GraceFret,
                    Sustain = note.Sustain,
                    Mute = note.Mute,
                    Hopo = note.Hopo,
                    BassPicking = note.BassPicking,
                    LinkNext = note.LinkNext,
                    Vibrato = note.Vibrato,
                    Tremolo = note.Tremolo,
                    Harmonic = note.Harmonic,
                    SlideTo = note.SlideTo,
                    UnpitchedSlide = note.UnpitchedSlide
                };
                foreach (var bend in note.BendValues) cn.BendValues.Add(bend);
                chord.ChordNotes.Add(cn);
            }

            newChords.Add(chord);
            notesToRemove.AddRange(group);
        }

        var actions = new List<IChartAction>();
        if (newTemplates.Count > 0)
            actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, newTemplates));
        actions.Add(new RemoveNotesAction(level.Notes, notesToRemove));
        actions.Add(new AddChordsAction(level.Chords, newChords));

        var composite = actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_MergeIntoChords" };
        if (!_historyService.Do(composite)) return;

        _chartEditor.RequestRedraw();
        _projectState.MainDisplayMessage = _localization.PluralWith("Status_NotesMergedIntoChordsOne", "Status_NotesMergedIntoChords", newChords.Count,
            notesToRemove.Count, newChords.Count);
        _logger.Info(AppLogCategory.Editing,
            $"Merged {notesToRemove.Count} note(s) into {newChords.Count} chord(s) ({newTemplates.Count} new template(s)).");
    }

    /// <summary>
    ///     The inverse of <see cref="MergeSelectedNotesIntoChords"/>: each
    ///     selected chord is replaced by its constituent notes (techniques,
    ///     sustains and bends carried over). Hand shapes are left in place —
    ///     they are independent authored elements. One undoable action.
    /// </summary>
    public void SplitSelectedChordsIntoNotes()
    {
        var level = _projectState.ActiveLevel;
        if (level == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoActiveLevel");
            return;
        }

        var selectedChords = _chartEditor.SelectedChords.Where(c => c.ChordNotes.Count > 0).ToList();
        if (selectedChords.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_NoChordsSelected");
            return;
        }

        var newNotes = new List<GuitarNote>();
        foreach (var chord in selectedChords)
        {
            foreach (var cn in chord.ChordNotes)
            {
                var note = new GuitarNote
                {
                    Position = chord.Position,
                    StringIndex = cn.StringIndex,
                    Fret = cn.Fret,
                    GraceFret = cn.GraceFret, // the inverse of the merge above
                    Sustain = cn.Sustain,
                    Mute = cn.Mute,
                    Hopo = cn.Hopo,
                    BassPicking = cn.BassPicking,
                    LinkNext = cn.LinkNext,
                    Vibrato = cn.Vibrato,
                    Tremolo = cn.Tremolo,
                    Harmonic = cn.Harmonic,
                    SlideTo = cn.SlideTo,
                    UnpitchedSlide = cn.UnpitchedSlide,
                    NotationDuration = chord.NotationDuration
                };
                foreach (var bend in cn.BendValues) note.BendValues.Add(bend);
                newNotes.Add(note);
            }
        }

        var actions = new List<IChartAction>
        {
            new RemoveChordsAction(level.Chords, selectedChords),
            new AddNotesAction(level.Notes, newNotes)
        };
        IChartAction composite = new CompositeAction(actions) { LabelKey = "History_SplitChords" };
        if (!_historyService.Do(composite)) return;

        _chartEditor.RequestRedraw();
        _projectState.MainDisplayMessage = _localization.Plural("Status_ChordsSplitIntoNotesOne", "Status_ChordsSplitIntoNotes", selectedChords.Count,
            newNotes.Count);
        _logger.Info(AppLogCategory.Editing,
            $"Split {selectedChords.Count} chord(s) into {newNotes.Count} note(s).");
    }

    public void ToggleChordFlag(Action<Chord> mutate, string label)
    {
        var level = _projectState.ActiveLevel;
        if (level == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoActiveLevel");
            return;
        }
        var selected = _chartEditor.SelectedChords.ToList();
        if (selected.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_NoChordsSelected");
            return;
        }

        var oldStates = selected.Select(c => c.Clone()).ToList();
        var newStates = selected.Select(c =>
        {
            var clone = c.Clone();
            mutate(clone);
            return clone;
        }).ToList();

        var action = new MutateChordsAction(level.Chords, selected, oldStates, newStates);
        if (!_historyService.Do(action)) return;
        _chartEditor.RequestRedraw();
        _logger.Info(AppLogCategory.Editing, $"Toggled {label} on {selected.Count} chord(s).");
    }

    private string FormatPosition(FractionalPosition position)
        => position.FormatWithMs(_chartEditor.Beats);

    private string L(string key) => _localization[key];

    private string L(string key, params object[] args) => _localization.Format(key, args);

    private sealed record PairFingeringKeys(string None, string Already, string SetOne, string Set, string History);
}
