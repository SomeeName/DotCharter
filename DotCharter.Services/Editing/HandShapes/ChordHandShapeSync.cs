using System.Collections.ObjectModel;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     Keeps a hand shape on the template of the chord it is drawn over, for every edit that moves a
///     chord onto another template. The switch is <c>AppConfig.SyncHandShapeToChordTemplate</c>; a
///     caller that finds it off simply never asks.
///     <para>
///         A bar follows only when it WAS in step: its template is the template the edited chord left.
///         A bar on another template was put there on purpose (the spacing pass leaves a bar on the
///         full shape of a reduced chord) and an edit must not overwrite that. It also follows only
///         when every templated chord under it ends up on the SAME template; a bar over two chords
///         that now disagree has no single answer, so it stays and the inspector reports the drift.
///     </para>
///     <para>
///         Pure: it reads and answers. The caller puts the answer into the SAME undo step as the chord
///         edit (<see cref="BuildAction" />), so one undo takes the chord and its bar back together.
///     </para>
/// </summary>
public static class ChordHandShapeSync
{
    /// <summary>One chord that moved: the live chord, the template it had and the one it will have.</summary>
    public readonly record struct ChordMove(Chord Chord, int OldTemplateId, int NewTemplateId);

    /// <summary>A bar and the template it should carry.</summary>
    public readonly record struct Retarget(HandShape Shape, int TemplateId);

    /// <summary>
    ///     The bars that follow <paramref name="moves" />. <paramref name="levelChords" /> is every chord of
    ///     the level the bars sit on (the moved ones included, by reference), because a bar's answer depends
    ///     on the chords under it that did NOT move.
    /// </summary>
    public static List<Retarget> Follow(
        IEnumerable<HandShape> handShapes, IEnumerable<Chord> levelChords, IReadOnlyList<ChordMove> moves)
    {
        var result = new List<Retarget>();
        if (moves.Count == 0) return result;

        var movedBy = new Dictionary<Chord, ChordMove>(ReferenceEqualityComparer.Instance);
        foreach (var move in moves)
        {
            if (move.OldTemplateId != move.NewTemplateId) movedBy[move.Chord] = move;
        }

        if (movedBy.Count == 0) return result;

        // Every templated chord of the level with the template it has AFTER the edit, in time order,
        // so a bar finds the chords under it with a binary search instead of a scan per bar.
        var after = new List<(FractionalPosition Position, int TemplateId, int OldTemplateId, bool Moved)>();
        foreach (var chord in levelChords)
        {
            if (movedBy.TryGetValue(chord, out var move))
            {
                after.Add((chord.Position, move.NewTemplateId, move.OldTemplateId, true));
                continue;
            }

            if (chord.TemplateId >= 0) after.Add((chord.Position, chord.TemplateId, chord.TemplateId, false));
        }

        after.Sort(static (a, b) => a.Position.CompareTo(b.Position));

        foreach (var bar in handShapes)
        {
            var template = AgreedTemplate(bar, after);
            if (template is { } agreed && agreed != bar.TemplateId)
                result.Add(new Retarget(bar, agreed));
        }

        return result;
    }

    /// <summary>
    ///     The one action that carries <paramref name="retargets" />, or null when there is nothing to move.
    ///     <paramref name="mergeVerb" /> is the chord edit's own, so a held key stays one gesture.
    /// </summary>
    public static IChartAction? BuildAction(
        ObservableCollection<HandShape> collection, IReadOnlyList<Retarget> retargets, Action? onPostExecute,
        string mergeVerb = "")
    {
        if (retargets.Count == 0) return null;

        var targets = new List<HandShape>(retargets.Count);
        var oldStates = new List<HandShape>(retargets.Count);
        var newStates = new List<HandShape>(retargets.Count);
        foreach (var (shape, templateId) in retargets)
        {
            targets.Add(shape);
            oldStates.Add(shape.Clone());
            var next = shape.Clone();
            next.TemplateId = templateId;
            newStates.Add(next);
        }

        return new MutateHandShapesAction(collection, targets, oldStates, newStates, onPostExecute, mergeVerb);
    }

    /// <summary>
    ///     The template of the first chord in [<paramref name="start" />, <paramref name="end" />) whose frets
    ///     are exactly <paramref name="seed" />'s, or null. A bar whose notes are one chord's notes belongs on
    ///     THAT chord's template, not on whichever equal-fret template a find-or-create happens to meet first.
    /// </summary>
    public static ChordTemplate? TemplateOfChordPlaying(
        Arrangement arrangement, IEnumerable<Chord> chords, FractionalPosition start, FractionalPosition end,
        ChordTemplate seed)
    {
        Chord? first = null;
        ChordTemplate? match = null;
        foreach (var chord in chords)
        {
            if (chord.TemplateId < 0 || chord.Position < start || chord.Position >= end) continue;
            if (first != null && chord.Position >= first.Position) continue;

            var template = arrangement.ChordTemplates.FirstOrDefault(t => t.Id == chord.TemplateId);
            if (template == null || !template.HasFrets(seed.Frets)) continue;
            first = chord;
            match = template;
        }

        return match;
    }

    /// <summary>The template every chord under <paramref name="bar" /> ends on, when the bar was following one.</summary>
    private static int? AgreedTemplate(
        HandShape bar, List<(FractionalPosition Position, int TemplateId, int OldTemplateId, bool Moved)> after)
    {
        var lo = 0;
        var hi = after.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (after[mid].Position.CompareTo(bar.Position) < 0) lo = mid + 1;
            else hi = mid;
        }

        int? agreed = null;
        var following = false;
        for (var i = lo; i < after.Count && bar.Covers(after[i].Position); i++)
        {
            var (_, templateId, oldTemplateId, moved) = after[i];
            if (agreed != null && agreed != templateId) return null;
            agreed = templateId;
            if (moved && oldTemplateId == bar.TemplateId) following = true;
        }

        return following ? agreed : null;
    }
}
