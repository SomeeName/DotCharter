using System.Collections.ObjectModel;
using DotCharter.Services.State;
using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     Moves the selected notes, chords, hand shapes and anchors earlier or later as ONE history
///     entry. The group moves rigidly (<see cref="TimeShift.Plan" /> clamps it at the first beat);
///     a move that would land on an unselected item is refused whole.
/// </summary>
internal sealed class MoveSelectedInTimeCommand(
    MoveSelectedInTimeCommand.Scope scope,
    Func<IReadOnlyList<FractionalPosition>, TimeShiftPlan> planShift,
    Action? onPostExecute) : IHighwayEditCommand
{
    internal sealed record Scope(
        ObservableCollection<GuitarNote> Notes,
        ObservableCollection<Chord> Chords,
        ObservableCollection<HandShape> HandShapes,
        ObservableCollection<FretAnchor> Anchors,
        IReadOnlyList<GuitarNote> SelectedNotes,
        IReadOnlyList<Chord> SelectedChords,
        IReadOnlyList<HandShape> SelectedHandShapes,
        IReadOnlyList<FretAnchor> SelectedAnchors,
        IList<BeatMarker> Beats);

    public TimeMoveResult Result { get; private set; } = new(TimeMoveOutcome.NothingSelected, 0, 0);

    public bool Execute(IHistoryService history)
    {
        var count = scope.SelectedNotes.Count + scope.SelectedChords.Count
                                              + scope.SelectedHandShapes.Count + scope.SelectedAnchors.Count;
        if (count == 0) return false;

        var starts = scope.SelectedNotes.Select(n => n.Position)
            .Concat(scope.SelectedChords.Select(c => c.Position))
            .Concat(scope.SelectedHandShapes.Select(h => h.Position))
            .Concat(scope.SelectedAnchors.Select(a => a.Position))
            .ToList();
        var plan = planShift(starts);
        if (plan.Outcome == TimeShiftOutcome.AtStart)
        {
            Result = new TimeMoveResult(TimeMoveOutcome.AtStart, 0, 0);
            return false;
        }

        if (Collides(plan.Map))
        {
            Result = new TimeMoveResult(TimeMoveOutcome.Collision, 0, 0);
            return false;
        }

        if (OverlapsHandShape(plan.Map))
        {
            Result = new TimeMoveResult(TimeMoveOutcome.HandShapeOverlap, 0, 0);
            return false;
        }

        var actions = new List<IChartAction>();
        if (scope.SelectedNotes.Count > 0)
        {
            actions.Add(new MutateNotesAction(scope.Notes, scope.SelectedNotes, n => SoundTimeMove.MoveTo(n, plan.Map(n.Position)),
                onPostExecute));
        }

        if (scope.SelectedChords.Count > 0)
        {
            var olds = scope.SelectedChords.Select(c => c.Clone()).ToList();
            var news = scope.SelectedChords.Select(c =>
            {
                var clone = c.Clone();
                SoundTimeMove.MoveTo(clone, plan.Map(c.Position));
                return clone;
            }).ToList();
            actions.Add(new MutateChordsAction(scope.Chords, scope.SelectedChords, olds, news, onPostExecute));
        }

        if (scope.SelectedHandShapes.Count > 0)
        {
            var olds = scope.SelectedHandShapes.Select(h => h.Clone()).ToList();
            var news = scope.SelectedHandShapes.Select(h =>
            {
                var clone = h.Clone();
                clone.Position = plan.Map(h.Position);
                clone.EndPosition = plan.Map(h.EndPosition);
                return clone;
            }).ToList();
            actions.Add(new MutateHandShapesAction(scope.HandShapes, scope.SelectedHandShapes, olds, news, onPostExecute));
        }

        if (scope.SelectedAnchors.Count > 0)
        {
            var olds = scope.SelectedAnchors.Select(a => a.Clone()).ToList();
            var news = scope.SelectedAnchors.Select(a =>
            {
                var clone = a.Clone();
                clone.Position = plan.Map(a.Position);
                return clone;
            }).ToList();
            actions.Add(new MutateAnchorsAction(scope.Anchors, scope.SelectedAnchors, olds, news, onPostExecute));
        }

        var earliest = starts.Min();
        var delta = plan.Map(earliest).GetPositionMs(scope.Beats) - earliest.GetPositionMs(scope.Beats);
        var done = history.Do(actions.Count == 1
            ? actions[0]
            : new CompositeAction(actions) { LabelKey = "History_NudgeTime" });
        if (!done) return false;

        Result = new TimeMoveResult(
            plan.Outcome == TimeShiftOutcome.Clamped ? TimeMoveOutcome.Clamped : TimeMoveOutcome.Moved, count, delta);
        return true;
    }

    /// <summary>
    ///     True when a moved hand shape's span would overlap an unselected one. Chords are not checked:
    ///     they bind to a hand shape by position alone, and the drag editor lets a chord cross a shape edge.
    /// </summary>
    private bool OverlapsHandShape(Func<FractionalPosition, FractionalPosition> map)
    {
        var others = scope.HandShapes.Except(scope.SelectedHandShapes).ToList();
        foreach (var moved in scope.SelectedHandShapes)
        {
            var start = map(moved.Position);
            var end = map(moved.EndPosition);
            if (others.Any(o => start < o.EndPosition && o.Position < end)) return true;
        }

        return false;
    }

    private bool Collides(Func<FractionalPosition, FractionalPosition> map)
    {
        var soundTaken = scope.Notes.Except(scope.SelectedNotes).Select(n => n.Position)
            .Concat(scope.Chords.Except(scope.SelectedChords).Select(c => c.Position))
            .ToHashSet();
        if (scope.SelectedNotes.Any(n => soundTaken.Contains(map(n.Position)))) return true;
        if (scope.SelectedChords.Any(c => soundTaken.Contains(map(c.Position)))) return true;

        var shapeTaken = scope.HandShapes.Except(scope.SelectedHandShapes).Select(h => h.Position).ToHashSet();
        if (scope.SelectedHandShapes.Any(h => shapeTaken.Contains(map(h.Position)))) return true;

        var anchorTaken = scope.Anchors.Except(scope.SelectedAnchors).Select(a => a.Position).ToHashSet();
        return scope.SelectedAnchors.Any(a => anchorTaken.Contains(map(a.Position)));
    }
}
