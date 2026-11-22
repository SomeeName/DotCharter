using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.UI.Controls;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>The Shape page's verbs: draft edits, pages, Save / Save and next / Undo save, run navigation.</summary>
public sealed partial class GripInspectorViewModel
{
    private const int BoardPage = 0;
    private const int DoubleStopPage = 4;
    private int _savedNodeId = -1;
    private HandShape? _savedShape;

    // ---- pages -------------------------------------------------------------

    /// <summary>The panel beside the string rows: Board, Name, Scope, Flags, Double-stop.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoardPage), nameof(IsNamePage), nameof(IsScopePage), nameof(IsFlagsPage),
        nameof(IsDoubleStopPage), nameof(ShowsBoard))]
    private int _pageIndex;

    /// <summary>Full board: the fretboard takes the whole width and pages its frets; the pages become flyouts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBoard), nameof(PageRow), nameof(PageColumn), nameof(PageColumnSpan))]
    private bool _isFullBoard;

    public bool IsBoardPage => PageIndex == BoardPage;
    public bool IsNamePage => PageIndex == 1;
    public bool IsScopePage => PageIndex == 2;
    public bool IsFlagsPage => PageIndex == 3;
    public bool IsDoubleStopPage => PageIndex == DoubleStopPage;
    public bool ShowsBoard => IsFullBoard || IsBoardPage;

    // The Name page's lists are built while it shows; opening it brings them up to date at once.
    partial void OnPageIndexChanged(int value)
    {
        if (IsNamePage) NameOffersPass.RunImmediately();
    }

    /// <summary>Where the page sits in the row grid: beside the rows, or under rows and board on Full board.</summary>
    public int PageRow => IsFullBoard ? 1 : 0;
    public int PageColumn => IsFullBoard ? 0 : 1;
    public int PageColumnSpan => IsFullBoard ? 2 : 1;

    // The page toggles send their index as XAML text ("0".."4").
    [RelayCommand]
    private void ShowPage(string page) =>
        PageIndex = Math.Clamp(int.Parse(page, CultureInfo.InvariantCulture), 0, DoubleStopPage);

    // ---- draft edits --------------------------------------------------------

    internal void SetChordFret(int index, int fret)
    {
        if (!Mask.Includes(index) || !Rows.Any(row => row.StringIndex == index && row.HasChord)) return;
        _draft.ChordFrets[index] = SoundingPitch.SnapToNeck(fret, Capo);
        RefreshProjection();
    }

    internal void SetGripFret(int index, int? fret)
    {
        if (!CanEditGrip || !Mask.Includes(index)) return;
        _draft.GripFrets[index] = fret is { } value ? SoundingPitch.SnapToNeck(value, Capo) : null;
        if (!fret.HasValue)
        {
            _draft.Fingers.Remove(index);
            _draft.Ghosts.Remove(index);
        }
        RefreshProjection();
    }

    internal void SetGhost(int index, bool value)
    {
        if (!CanEditGrip || !Mask.Includes(index) || !Rows.Any(row => row.StringIndex == index && row.HasGrip)) return;
        _draft.Ghosts[index] = value;
        RefreshProjection();
    }

    internal void SetFinger(int index, int finger)
    {
        if (!Mask.Includes(index) || !Rows.Any(row => row.StringIndex == index && row.CanEditFinger)) return;
        _draft.Fingers[index] = finger;
        RefreshProjection();
    }

    [RelayCommand] private void ToggleMask(int index) => _state.ToggleStringEditMask(index);

    [RelayCommand] private void ClearMask() => _state.ClearStringEditMask();

    [RelayCommand]
    private void ToggleBoardFret((int StringIndex, int Fret) edit) =>
        SetGripFret(edit.StringIndex, FretboardTemplate.GetFret(edit.StringIndex) == edit.Fret ? null : edit.Fret);

    [RelayCommand] private void SetBoardFret(ChordBoxFretEdit edit) => SetGripFret(edit.StringIndex, edit.Fret);
    [RelayCommand] private void SetBoardFinger(ChordBoxFingerEdit edit) => SetFinger(edit.StringIndex, edit.Finger);
    [RelayCommand] private void ClearBoardString(int index) => SetGripFret(index, null);

    // ---- keyboard: Up/Down walk the strings, Left/Right step the finger --------

    /// <summary>The row the keyboard edits, as an index into <see cref="GripInspectorViewModel.Rows" />.</summary>
    [ObservableProperty] private int _focusedRow;

    internal void MoveRow(int delta)
    {
        if (Rows.Count == 0) return;
        FocusedRow = Math.Clamp(FocusedRow + delta, 0, Rows.Count - 1);
        MarkKeyRow();
    }

    private void MarkKeyRow()
    {
        for (var i = 0; i < Rows.Count; i++) Rows[i].IsKeyRow = i == FocusedRow;
    }

    /// <summary>Steps the focused row's finger through none, T, 1, 2, 3, 4.</summary>
    internal void StepFinger(int delta)
    {
        if (Rows.Count == 0) return;
        FocusedRow = Math.Clamp(FocusedRow, 0, Rows.Count - 1);
        MarkKeyRow();
        var row = Rows[FocusedRow];
        if (!row.CanEditFinger) return;
        SetFinger(row.StringIndex, Math.Clamp((row.Finger ?? -1) + delta, -1, 4));
    }

    // ---- templates -----------------------------------------------------------

    [RelayCommand]
    private void UseTemplate(GripTemplateOption? option)
    {
        if (!CanEditGrip || option?.Template is not { } template) return;
        _draft.Template = template;
        _draft.GripFrets.Clear();
        _draft.Fingers.Clear();
        _draft.Ghosts.Clear();
        RefreshProjection();
    }

    /// <summary>Drafts a suggested name (the Name page's "Use …" buttons).</summary>
    [RelayCommand]
    private void UseName(GripNameSuggestion? suggestion)
    {
        if (suggestion != null) ChordNameText = suggestion.Name;
    }

    [RelayCommand]
    private void NewTemplate()
    {
        if (!CanEditGrip) return;
        _draft.MintTemplate = true;
        SaveFingering();
    }

    // ---- double-stop -----------------------------------------------------------

    /// <summary>The strings the drafted grip frets, the choices of the Double-stop page.</summary>
    public IReadOnlyList<string> PairOptions { get; private set; } = [];
    private int[] _pairStrings = [];
    [ObservableProperty] private int _pairFirst;
    [ObservableProperty] private int _pairSecond = 1;

    private void RefreshDoubleStopPair()
    {
        var rows = Rows.Where(row => FretboardTemplate.Frets.ContainsKey(row.StringIndex)).ToArray();
        var strings = rows.Select(row => row.StringIndex).ToArray();
        if (strings.SequenceEqual(_pairStrings)) return;
        _pairStrings = strings;
        PairOptions = rows.Select(row => row.Label).ToArray();
        OnPropertyChanged(nameof(PairOptions));
        PairFirst = 0;
        PairSecond = Math.Min(1, strings.Length - 1);
        UseDoubleStopPairCommand.NotifyCanExecuteChanged();
    }

    private bool CanUsePair => CanEditGrip && _pairStrings.Length >= 2 && PairFirst != PairSecond &&
        PairFirst >= 0 && PairSecond >= 0 && PairFirst < _pairStrings.Length && PairSecond < _pairStrings.Length;

    partial void OnPairFirstChanged(int value) => UseDoubleStopPairCommand.NotifyCanExecuteChanged();
    partial void OnPairSecondChanged(int value) => UseDoubleStopPairCommand.NotifyCanExecuteChanged();

    /// <summary>Drafts the grip down to the two chosen strings and marks it a double-stop.</summary>
    [RelayCommand(CanExecute = nameof(CanUsePair))]
    private void UseDoubleStopPair()
    {
        var keep = new[] { _pairStrings[PairFirst], _pairStrings[PairSecond] };
        foreach (var index in FretboardTemplate.Frets.Keys.Except(keep).ToArray())
        {
            _draft.GripFrets[index] = null;
            _draft.Fingers.Remove(index);
            _draft.Ghosts.Remove(index);
        }
        _draft.DoubleStop = true;
        DraftChanged(nameof(DoubleStop));
    }

    [RelayCommand]
    private void DoubleStopOff() => DoubleStop = false;

    // ---- save ----------------------------------------------------------------------

    private bool CanSaveFingering => HasDraft && IsAvailable;

    /// <summary>Writes the draft in ONE undo step: the grip to every hand-shape target, Note frets to the
    /// chords that already sound those strings. Never adds a note.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveFingering))]
    private void SaveFingering()
    {
        if (_arrangement == null || !HasDraft) return;
        var actions = new List<IChartAction>();
        var minted = new List<ChordTemplate>();
        if (_draft.HasGripEdits) actions.AddRange(SaveShapes(minted));
        actions.AddRange(SaveChords(minted));
        _draft = new GripInspectorDraft();
        var saved = _shape;
        // A grip save is an editing gesture: in a linked measure it reaches every placement in the same step.
        Commit(actions, minted, "GripStudy_Save", LinkPolicy.Propagate);
        Sync(chartChanged: true);
        OfferIdenticalShapes(saved);
    }

    private IEnumerable<IChartAction> SaveShapes(List<ChordTemplate> minted)
    {
        foreach (var group in _targets.Shapes.GroupBy(t => t.Level))
        {
            var changes = group.Where(t => _draft.AppliesTo(t.Fields) && t.Level.HandShapes.Contains(t.Shape))
                .Select(t => (t.Shape, Next: SaveGrip(t, minted))).ToArray();
            yield return new MutateHandShapesAction(group.Key.HandShapes, changes.Select(c => c.Shape),
                changes.Select(c => c.Shape.Clone()), changes.Select(c => c.Next));
        }
    }

    private IEnumerable<IChartAction> SaveChords(List<ChordTemplate> minted)
    {
        var arrangement = _arrangement!;
        foreach (var group in _targets.Chords.GroupBy(t => t.Level))
        {
            var changes = group.Where(t => t.Level.Chords.Contains(t.Chord))
                .Select(t => (t.Chord, Next: GripEdit.Refret(t.Chord, _draft.ChordFrets)))
                .Where(t => t.Next != null).ToArray();
            foreach (var (chord, next) in changes)
            {
                var previous = arrangement.FindChordTemplate(chord.TemplateId);
                if (previous?.DescribesChordNotes(next!.ChordNotes) == true) continue;
                var frets = next!.ChordNotes.GroupBy(n => n.StringIndex).ToDictionary(g => g.Key, g => g.First().Fret);
                next.TemplateId = ChordTemplateResolver.Resolve(arrangement, frets,
                    ArpeggioFlavour.Of(previous), null, minted).Id;
            }
            yield return new MutateChordsAction(group.Key.Chords, changes.Select(c => c.Chord),
                changes.Select(c => c.Chord.Clone()), changes.Select(c => c.Next!));
        }
    }

    private HandShape SaveGrip(GripShapeTarget target, List<ChordTemplate> minted)
    {
        var arrangement = _arrangement!;
        var original = GripOf(target.Shape);
        var grip = _draft.Project(target.Shape, original, OpeningMasks(target.Level), target.Fields);
        var next = target.Shape.Clone();
        var preferred = target.Fields == GripShapeFields.All ? _draft.Template : null;
        var authoredGhosts = (preferred ?? original).GhostMask != 0 ||
            (preferred == null && next.GhostMask.HasValue) ||
            (target.Fields == GripShapeFields.All && _draft.Ghosts.Keys.Any(grip.Frets.ContainsKey));
        // An inferred ghost is never stored: it is read off the opening's sounds whenever it is shown, so a later
        // note edit under the shape cannot leave a stale mask for the exporters.
        next.GhostMask = authoredGhosts ? grip.GhostMask : null;
        if (!_draft.MintTemplate && preferred != null &&
            ReferenceEquals(arrangement.FindChordTemplate(preferred.Id), preferred))
        {
            if (preferred.GhostMask == 0) grip.GhostMask = 0;
            if (ChordTemplateResolver.MatchesAuthoredState(preferred, grip))
            {
                next.TemplateId = preferred.Id;
                return next;
            }
        }

        grip.GhostMask = 0;
        if (_draft.MintTemplate)
        {
            var last = arrangement.ChordTemplates.Concat(minted).Select(t => t.Id).DefaultIfEmpty(-1).Max();
            grip.Id = last + 1;
            minted.Add(grip);
            next.TemplateId = grip.Id;
        }
        else
        {
            next.TemplateId = ChordTemplateResolver.ResolveAuthored(arrangement, grip, minted).Id;
        }
        return next;
    }

    private bool NeedsRebind(Chord chord)
    {
        var next = GripEdit.Refret(chord, _draft.ChordFrets);
        return next != null && _arrangement?.FindChordTemplate(chord.TemplateId)?.DescribesChordNotes(next.ChordNotes) != true;
    }

    private void Commit(List<IChartAction> actions, List<ChordTemplate> minted, string label, LinkPolicy policy)
    {
        if (_arrangement == null) return;
        if (minted.Count > 0) actions.Insert(0, new AddChordTemplatesAction(_arrangement.ChordTemplates, minted));
        var action = new CompositeAction(actions)
        {
            LabelKey = label,
            LinkPolicyOverride = policy
        };
        if (!action.HasEffect) return;
        _history.Do(action);
        _savedNodeId = _history.CurrentNodeId;
        _savedShape = _shape;
        _editor.RequestRedraw();
        RefreshUndoSave();
    }

    // ---- undo save ---------------------------------------------------------------

    /// <summary>The last Save is still the newest history step, so Undo save takes back exactly it.</summary>
    public bool CanUndoSave => _savedNodeId >= 0 && _history.CurrentNodeId == _savedNodeId && _history.CanUndo;

    private void RefreshUndoSave()
    {
        if (_savedNodeId >= 0 && _history.CurrentNodeId != _savedNodeId) _savedNodeId = -1;
        if (_identicalNodeId >= 0 && _history.CurrentNodeId != _identicalNodeId) ClearIdenticalShapes();
        OnPropertyChanged(nameof(CanUndoSave));
        UndoSaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndoSave))]
    private void UndoSave()
    {
        if (!CanUndoSave) return;
        var shape = _savedShape;
        _savedNodeId = -1;
        _history.Undo();
        if (shape != null) JumpTo(shape);
        RefreshUndoSave();
    }

    // ---- auto-fill -------------------------------------------------------------------

    /// <summary>Fills the missing fingers and the name of the hand shapes on the Apply-to rung, as one undo step.</summary>
    [RelayCommand]
    private void AutoFill()
    {
        if (_arrangement is not { } arrangement) return;
        var minted = new List<ChordTemplate>();
        var actions = new List<IChartAction>();
        var pinning = _draft.Pin == true;
        foreach (var group in _targets.Shapes.Where(t => t.Level.HandShapes.Contains(t.Shape)).GroupBy(t => t.Level))
        {
            var openingMasks = GhostGems.IndexOpeningGhostMasks(group.Key);
            foreach (var target in group.DistinctBy(t => t.Shape))
            {
                if (pinning && target.Fields == GripShapeFields.All) continue;
                var shape = target.Shape;
                var grip = GripOf(shape);
                if (GripEdit.AutoFill(arrangement, grip, GripEdit.Ghosts(shape, grip, openingMasks)) is not { } next) continue;
                var after = shape.Clone();
                after.TemplateId = ChordTemplateResolver.ResolveAuthored(arrangement, next, minted).Id;
                actions.Add(new MutateHandShapesAction(group.Key.HandShapes, [shape], [shape.Clone()], [after]));
            }
        }
        Commit(actions, minted, "History_GenerateFingerings", LinkPolicy.ShareFirstChanged);
        Sync(chartChanged: true);
    }

    // ---- the let-ring run: Save and next, Save & next missing ---------------------------

    private void RefreshRun()
    {
        _run = _arrangement != null && _level != null && IsAvailable
            ? new GripRun(_level.HandShapes, _shape, _arrangement, _chord?.Position) : null;
        Notify(nameof(RunPosition), nameof(HasNext), nameof(HasPrevious), nameof(NextDestination), nameof(NextMissingDestination));
        PreviousShapeCommand.NotifyCanExecuteChanged();
        NextShapeCommand.NotifyCanExecuteChanged();
        SaveAndNextCommand.NotifyCanExecuteChanged();
        SaveAndNextMissingCommand.NotifyCanExecuteChanged();
        NextMissingCommand.NotifyCanExecuteChanged();
    }

    public bool HasNext => _run?.Next != null;
    public bool HasPrevious => _run?.Previous != null;
    private bool HasNextMissing => _run?.NextMissing != null;

    /// <summary>Where Save and next goes: the next shape's name and frets.</summary>
    public string NextDestination => Destination(_run?.Next);

    /// <summary>Where Save &amp; next missing goes: the next shape still missing a fingering.</summary>
    public string NextMissingDestination => Destination(_run?.NextMissing);

    private string Destination(HandShape? shape) =>
        shape == null || _run == null ? _loc["Inspector_Grip_NoDestination"] : _run.Describe(shape, _loc["GripInspector_Unnamed"]).Display;

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void SaveAndNext()
    {
        var next = _run?.Next;
        if (HasDraft) SaveFingering();
        JumpTo(next);
    }

    [RelayCommand(CanExecute = nameof(HasNextMissing))]
    private void SaveAndNextMissing()
    {
        if (HasDraft) SaveFingering();
        JumpTo(_run?.NextMissing);
    }

    [RelayCommand(CanExecute = nameof(HasNextMissing))]
    private void NextMissing() => JumpTo(_run?.NextMissing);

    /// <summary>
    ///     The "Save and next fingering" keybind and palette entry. On the Shape page they save the grip draft and
    ///     walk to the next shape still missing a fingering; with the page idle they keep the chord panel's verb.
    /// </summary>
    [RelayCommand]
    private void SaveAndNextFingering()
    {
        if (!IsActive || !IsAvailable)
        {
            _ladder.SaveAndNextFingeringCommand.Execute(null);
            return;
        }

        var next = _run?.NextMissing;
        if (HasDraft) SaveFingering();
        JumpTo(next);
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void PreviousShape() => JumpTo(_run?.Previous);

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void NextShape() => JumpTo(_run?.Next);

    private void JumpTo(HandShape? shape)
    {
        if (shape == null || _level?.HandShapes.Contains(shape) != true || shape == _shape) return;
        _editor.Selection.ClearSelections();
        _editor.Selection.SelectedHandShapes.Add(shape);
        if (_editor.Beats.Count > 0) _editor.ExecuteSeek(shape.Position.GetPositionMs(_editor.Beats));
        Sync();
        FocusDestination?.Invoke();
    }

    // ---- also apply to identical hand shapes ------------------------------------------
    // After a grip save, the other hand shapes of the level over the same sounds that do not hold the saved grip yet.
    // Nothing is applied until the click, which is ONE undo step; any other history step closes the offer.

    private HandShape[] _identical = [];
    private HandShape? _identicalSource;
    private ChartLevel? _identicalLevel;
    private int _identicalNodeId = -1;

    public int IdenticalShapeCount => _identical.Length;
    public bool HasIdenticalShapes => _identical.Length > 0;

    public string ApplyToIdenticalText => HasIdenticalShapes
        ? _loc.Plural("GripInspector_ApplyIdenticalOne", "GripInspector_ApplyIdentical", _identical.Length)
        : "";

    private void OfferIdenticalShapes(HandShape? saved)
    {
        ClearIdenticalShapes();
        if (saved == null || _level == null || _savedNodeId < 0 || !_level.HandShapes.Contains(saved)) return;
        var done = _targets.Shapes.Select(t => t.Shape).Append(saved);
        var identical = IdenticalHandShapes.Find(_level, saved, done);
        if (identical.Count == 0) return;
        (_identical, _identicalSource, _identicalLevel, _identicalNodeId) = ([.. identical], saved, _level, _history.CurrentNodeId);
        NotifyIdenticalShapes();
    }

    private void ClearIdenticalShapes()
    {
        if (_identicalNodeId < 0 && _identical.Length == 0) return;
        (_identical, _identicalSource, _identicalLevel, _identicalNodeId) = ([], null, null, -1);
        NotifyIdenticalShapes();
    }

    private void NotifyIdenticalShapes()
    {
        Notify(nameof(IdenticalShapeCount), nameof(HasIdenticalShapes), nameof(ApplyToIdenticalText));
        ApplyToIdenticalShapesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Gives every offered shape the saved shape's grip (its template and ghost mask) as one undo step.</summary>
    [RelayCommand(CanExecute = nameof(HasIdenticalShapes))]
    private void ApplyToIdenticalShapes()
    {
        if (_identicalSource is not { } source || _identicalLevel is not { } level) return;
        var shapes = _identical.Where(level.HandShapes.Contains).ToArray();
        ClearIdenticalShapes();
        var next = shapes.Select(shape =>
        {
            var copy = shape.Clone();
            copy.TemplateId = source.TemplateId;
            copy.GhostMask = source.GhostMask;
            return copy;
        }).ToArray();
        var action = new CompositeAction([new MutateHandShapesAction(level.HandShapes, shapes, shapes.Select(s => s.Clone()), next)])
        {
            LabelKey = "GripStudy_ApplyIdentical",
            LinkPolicyOverride = LinkPolicy.Propagate
        };
        if (!action.HasEffect) return;
        _history.Do(action);
        _editor.RequestRedraw();
    }
}
