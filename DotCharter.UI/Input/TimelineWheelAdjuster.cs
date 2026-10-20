using DotCharter.Services.Editing.Anchors;
using DotCharter.UI.Input.Actions;
using DotCharter.UI.Input.Tools.EventInteractionGestures;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Input;

/// <summary>
/// Outcome of a single mouse-wheel handler attempt.
/// </summary>
public enum WheelAdjustmentResult
{
    /// <summary>This handler is not the right target for the current state.</summary>
    NotTargeted,

    /// <summary>This handler owns the wheel event but the input produced no observable change.</summary>
    NoChange,

    /// <summary>This handler successfully mutated state.</summary>
    Changed
}

/// <summary>
/// Pure logic for mouse-wheel adjustments over the timeline (sustain
/// stretching, preview-fret nudging, lyric resizing, hand-shape resize).
/// Extracted from <see cref="Views.MainWindow"/> so each rule is unit-testable
/// without an Avalonia visual tree.
///
/// Each <c>TryAdjust*</c> returns:
/// <list type="bullet">
/// <item><see cref="WheelAdjustmentResult.NotTargeted"/> — the rule does not
///   apply right now (caller falls through to the next rule);</item>
/// <item><see cref="WheelAdjustmentResult.NoChange"/> — the rule owned the
///   gesture but the input produced no diff (caller stops, no redraw);</item>
/// <item><see cref="WheelAdjustmentResult.Changed"/> — state mutated, redraw
///   the timeline.</item>
/// </list>
/// The first non-<see cref="WheelAdjustmentResult.NotTargeted"/> result wins.
/// </summary>
public sealed class TimelineWheelAdjuster
{
    private readonly IHighwayEditingService _highwayEditing;
    private readonly IHistoryService _history;
    private readonly IConfigurationService _configService;
    private readonly IFingeringGenerationService _fingeringService;

    public TimelineWheelAdjuster(IHighwayEditingService highwayEditing, IHistoryService history,
        IConfigurationService configService, IFingeringGenerationService fingeringService)
    {
        _highwayEditing = highwayEditing;
        _history = history;
        _configService = configService;
        _fingeringService = fingeringService;
    }

    /// <summary>
    /// Walks the priority-ordered list of rules and returns the result of the
    /// first non-<see cref="WheelAdjustmentResult.NotTargeted"/> one. Visible
    /// for testing the overall priority chain.
    /// </summary>
    public WheelAdjustmentResult Apply(MainWindowViewModel vm, int deltaSteps)
    {
        var r = TryAdjustSelectedSoundSustain(vm, deltaSteps);
        if (r != WheelAdjustmentResult.NotTargeted) return r;
        r = TryAdjustHandShapeLength(vm, deltaSteps);
        if (r != WheelAdjustmentResult.NotTargeted) return r;
        r = TryAdjustPreviewNoteFret(vm, deltaSteps);
        if (r != WheelAdjustmentResult.NotTargeted) return r;
        r = TryAdjustPreviewAnchorFret(vm, deltaSteps);
        if (r != WheelAdjustmentResult.NotTargeted) return r;
        return TryAdjustLyricSustain(vm, deltaSteps);
    }

    /// <summary>What <see cref="ApplyToSelection" /> lengthens.</summary>
    public enum LengthTarget { None, Sounds, HandShapes, Lyric }

    /// <summary>
    ///     The keyboard / menu / palette Increase- and Decrease-Length target: the wheel's order
    ///     (<see cref="Apply" />) over the SELECTION only — a key press has no pointer, so nothing
    ///     hovered is a target.
    /// </summary>
    public static LengthTarget LengthTargetFor(bool soundsSelected, bool handShapesSelected, bool lyricSelected) =>
        soundsSelected ? LengthTarget.Sounds
        : handShapesSelected ? LengthTarget.HandShapes
        : lyricSelected ? LengthTarget.Lyric
        : LengthTarget.None;

    /// <summary>
    ///     Increase / Decrease Length on whatever is selected. The verb must not call
    ///     <c>AdjustSelectedNotesSustain</c> directly: with a hand shape or a lyric selected the
    ///     key would do nothing, although the wheel lengthens both.
    /// </summary>
    public WheelAdjustmentResult ApplyToSelection(MainWindowViewModel vm, int deltaSteps) =>
        LengthTargetFor(vm.ChartEditor.SelectedNotes.Any() || vm.ChartEditor.SelectedChords.Any(),
                vm.ChartEditor.SelectedHandShapes.Any(), vm.ChartEditor.Selection.SelectedLyric != null) switch
        {
            LengthTarget.Sounds => TryAdjustSelectedSoundSustain(vm, deltaSteps),
            LengthTarget.HandShapes => TryAdjustHandShapeLength(vm, deltaSteps, includeHovered: false),
            LengthTarget.Lyric => TryAdjustLyricSustain(vm, deltaSteps, includeHovered: false),
            _ => WheelAdjustmentResult.NotTargeted
        };

    public WheelAdjustmentResult TryAdjustSelectedSoundSustain(MainWindowViewModel vm, int deltaSteps)
    {
        if (!vm.ChartEditor.SelectedNotes.Any() && !vm.ChartEditor.SelectedChords.Any())
            return WheelAdjustmentResult.NotTargeted;

        return _highwayEditing.AdjustSelectedNotesSustain(deltaSteps, vm.ProjectState.TotalAudioLengthMs,
            vm.Transport.GridDivisor, vm.ChartEditor.Beats)
            ? WheelAdjustmentResult.Changed
            : WheelAdjustmentResult.NoChange;
    }

    public static WheelAdjustmentResult TryAdjustPreviewNoteFret(MainWindowViewModel vm, int deltaSteps)
    {
        if (!vm.ChartEditor.PreviewNotes.Any()) return WheelAdjustmentResult.NotTargeted;

        // The notch walks the frets the neck HAS: behind a capo the ladder is
        // {0} ∪ [capo, 24], so wheeling down off the capo lands on the open
        // string and wheeling up off it lands back on the capo, with nothing in
        // between. A plain Clamp(…, 0, 24) armed the ghost — and therefore the
        // created note — at a fret the arrangement cannot play (L01), and then
        // stalled there, because ProjectState.PreviewNoteFret snaps the write.
        // The DIRECTION is why this cannot live in the setter alone.
        var nextFret = SoundingPitch.SnapToNeck(vm.ProjectState.PreviewNoteFret + deltaSteps,
            Math.Max(0, vm.ChartEditor.ActiveArrangement?.Capo ?? 0), Math.Sign(deltaSteps));
        var changed = vm.ProjectState.PreviewNoteFret != nextFret ||
                      vm.ChartEditor.PreviewNotes.Any(note => note.Fret != nextFret);
        if (!changed) return WheelAdjustmentResult.NoChange;

        vm.ProjectState.PreviewNoteFret = nextFret;
        foreach (var note in vm.ChartEditor.PreviewNotes)
            note.Fret = vm.ProjectState.PreviewNoteFret;

        return WheelAdjustmentResult.Changed;
    }

    public static WheelAdjustmentResult TryAdjustPreviewAnchorFret(MainWindowViewModel vm, int deltaSteps)
    {
        if (!vm.ChartEditor.PreviewAnchors.Any()) return WheelAdjustmentResult.NotTargeted;

        var currentFret = vm.ProjectState.PreviewAnchorFret > 0
            ? vm.ProjectState.PreviewAnchorFret
            : vm.ChartEditor.PreviewAnchors[0].Fret;
        // The floor is capo + 1, not 1: an anchor on or below the capo is
        // inspector rule L02, and the placement verb takes this value VERBATIM
        // ("an explicit preview fret always wins", ahead of the capo-aware
        // AnchorFretSuggester). ProjectState clamps the write too; this keeps the
        // ghost from stalling one fret short of what it can be set to.
        var floor = AnchorFretSuggester.MinFret(vm.ChartEditor.ActiveArrangement?.Capo ?? 0);
        var nextFret = Math.Clamp(currentFret + deltaSteps, floor, 24);
        var changed = vm.ProjectState.PreviewAnchorFret != nextFret ||
                      vm.ChartEditor.PreviewAnchors.Any(anchor => anchor.Fret != nextFret);
        if (!changed) return WheelAdjustmentResult.NoChange;

        vm.ProjectState.PreviewAnchorFret = nextFret;
        foreach (var anchor in vm.ChartEditor.PreviewAnchors)
            anchor.Fret = vm.ProjectState.PreviewAnchorFret;

        return WheelAdjustmentResult.Changed;
    }

    public WheelAdjustmentResult TryAdjustLyricSustain(MainWindowViewModel vm, int deltaSteps,
        bool includeHovered = true)
    {
        // Stretch / shrink either the selected or hovered lyric pill by one
        // grid division so users can fix sustain without opening a dialog.
        var lyric = vm.ChartEditor.Selection.SelectedLyric ??
                    (includeHovered ? vm.ChartEditor.Selection.HoveredLyric : null);
        if (lyric == null) return WheelAdjustmentResult.NotTargeted;

        var delta = new FractionalPosition(new Fraction(deltaSteps, vm.Transport.GridDivisor));
        var newEnd = lyric.EndPosition + delta;
        if (newEnd <= lyric.Position)
            newEnd = lyric.Position + new FractionalPosition(new Fraction(1, Math.Max(vm.Transport.GridDivisor, 16)));

        // The end may never cross into the next syllable's minimum gap — the
        // same cap note sustains get from ChartSpacingEnforcer, anchored at the
        // earlier sound's position. The neighbour may live in the next phrase.
        // A pill that already overlaps (older data) snaps back into legality on
        // the first wheel tick, mirroring the enforcer's only-ever-shortens rule.
        var phrases = vm.ChartEditor.ActiveVocalTrack?.Phrases;
        var beats = vm.ChartEditor.Beats;
        if (phrases != null && beats.Count > 0)
        {
            var (_, next) = LyricDragBounds.FindNeighbors(phrases, lyric);
            if (next != null)
            {
                var config = _configService.LoadAppConfig();
                var maxEnd = next.Position - ChartSpacingEnforcer.GetMinDistance(lyric.Position, beats, config);
                if (maxEnd > lyric.Position && newEnd > maxEnd) newEnd = maxEnd;
            }
        }

        var oldEnd = lyric.EndPosition;
        if (oldEnd.Equals(newEnd)) return WheelAdjustmentResult.NoChange;

        // One wheel run = one undo node, exactly like the hand-shape resize two
        // rules up. Without the verb this pushed a node per TICK — the last
        // wheel gesture in the editor still doing so.
        return _history.Do(new ResizeLyricAction(lyric, oldEnd, newEnd, MergeVerbs.LyricLength))
            ? WheelAdjustmentResult.Changed
            : WheelAdjustmentResult.NoChange;
    }

    public WheelAdjustmentResult TryAdjustHandShapeLength(MainWindowViewModel vm, int deltaSteps,
        bool includeHovered = true)
    {
        var targets = vm.ChartEditor.SelectedHandShapes.Any()
            ? vm.ChartEditor.SelectedHandShapes.ToList()
            : includeHovered && vm.ChartEditor.Selection.HoveredHandShape != null
                ? [vm.ChartEditor.Selection.HoveredHandShape]
                : [];
        if (!targets.Any()) return WheelAdjustmentResult.NotTargeted;

        var delta = new FractionalPosition(new Fraction(deltaSteps, vm.Transport.GridDivisor));
        var oldStates = targets.Select(h => h.Clone()).ToList();
        var newStates = targets.Select(h =>
        {
            var clone = h.Clone();
            clone.EndPosition = FractionalPosition.Max(clone.Position, clone.EndPosition + delta);
            return clone;
        }).ToList();
        var changed = oldStates.Count != newStates.Count;
        for (var i = 0; !changed && i < oldStates.Count; i++)
            changed = !ChartActionStateComparer.HandShapesEqual(oldStates[i], newStates[i]);

        // The bar's GRIP follows its length: a bar dragged over more notes
        // declares what those notes play, fingering included. Only ever on a
        // template the bar owns alone — see HandShapeAutoTemplate.
        var grips = new List<ResizeHandShapesAction.GripRewrite>();
        var minted = new List<ChordTemplate>();
        var arrangement = vm.ChartEditor.ActiveArrangement;
        if (arrangement != null) RebuildGrips(vm, arrangement, targets, newStates, grips, minted);

        if (!changed && grips.Count == 0 && minted.Count == 0) return WheelAdjustmentResult.NoChange;

        // One wheel run = one undo node — spans AND grips together. A resize is
        // dozens of ticks and the charter thinks of it as a single "make this
        // bar longer"; before the merge verb, one real session logged a 111-node
        // run here, and a grip edit pushed separately would undo on its own.
        return _history.Do(new ResizeHandShapesAction(vm.ChartEditor.HandShapes, targets, oldStates, newStates,
            arrangement?.ChordTemplates ?? [], grips, minted,
            _highwayEditing.GetLinkNextUpdater()))
            ? WheelAdjustmentResult.Changed
            : WheelAdjustmentResult.NoChange;
    }

    /// <summary>
    ///     Derives each resized bar's new grip and, for a bar that has no
    ///     template at all, mints the one it needs. <paramref name="newStates" />
    ///     is REPOINTED at a minted template, so the span action carries the id
    ///     change and undo takes it back with everything else.
    /// </summary>
    private void RebuildGrips(
        MainWindowViewModel vm, Arrangement arrangement,
        IReadOnlyList<HandShape> targets, IReadOnlyList<HandShape> newStates,
        List<ResizeHandShapesAction.GripRewrite> grips, List<ChordTemplate> minted)
    {
        var stringCount = StringDigitTargeting.StringCountOf(arrangement);
        var notes = vm.ChartEditor.Notes;
        var chords = vm.ChartEditor.Chords;
        var shapes = vm.ChartEditor.HandShapes;

        for (var i = 0; i < targets.Count; i++)
        {
            var shape = targets[i];
            var next = newStates[i];
            var current = arrangement.ChordTemplates.FirstOrDefault(t => t.Id == shape.TemplateId);

            var owned = shape.TemplateId < 0 ||
                        HandShapeAutoTemplate.OwnsItsTemplateAlone(shape, chords, shapes);
            if (!owned) continue;
            // A pinned grip (authored, or holding ghost gems) keeps its shape however far
            // the bar is dragged; HandShapeAutoTemplate.Rebuild refuses it too.
            if (current is { IsPinned: true }) continue;

            var seed = _fingeringService.BuildTemplateForRange(arrangement, notes, chords,
                next.Position, next.EndPosition);
            var rebuilt = ArrangementFingeringStyle.RebuildHandShapeGrip(arrangement, current, seed, stringCount);
            if (rebuilt == null) continue;

            if (current != null)
            {
                grips.Add(new ResizeHandShapesAction.GripRewrite(current, current.Clone(), rebuilt));
                continue;
            }

            // No template yet — one mint for the whole gesture, because every
            // later tick finds the bar owning the template this one created.
            rebuilt.Id = _fingeringService.NextTemplateId(arrangement) + minted.Count;
            minted.Add(rebuilt);
            next.TemplateId = rebuilt.Id;
        }
    }
}
