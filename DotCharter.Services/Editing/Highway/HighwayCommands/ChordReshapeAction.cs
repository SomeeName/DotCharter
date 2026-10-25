using System.Collections.ObjectModel;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     The one way to commit a chord whose NOTE SET or FRETS changed.
///     <para>
///         A <see cref="ChordTemplate" /> is SHARED — one instance backs every
///         chord AND hand shape with that shape — so the new shape must be
///         resolve-or-created and the chord repointed, never the template
///         mutated (the v0.33.2 corruption). And in a <c>.dcproj</c> a
///         <c>&lt;chordNote&gt;</c> carries no <c>fret</c> attribute at all: the
///         reader takes the fret from the template, so a shape change that does
///         not repoint <c>TemplateId</c> is DISCARDED on the next save
///         (format audit 02 §6.5). <c>ChartSpacingEnforcer</c> does not repair
///         this — it deliberately never repoints a chord
///         (<c>FixLinkNexts</c>' doc, note (2)).
///     </para>
///     <para>
///         An UNTEMPLATED chord (<c>TemplateId -1</c>) stays untemplated: it has
///         nothing to disagree with. A null arrangement (headless harness) is
///         left alone rather than minting into nothing.
///     </para>
///     <para>
///         With <c>syncedHandShapes</c> (the level's bars, passed only while
///         <c>AppConfig.SyncHandShapeToChordTemplate</c> is on) the hand shape over the chord
///         moves onto the chord's new template in the SAME action, so one undo takes both back.
///     </para>
/// </summary>
internal static class ChordReshapeAction
{
    /// <summary>
    ///     <paramref name="newState" />'s <c>TemplateId</c> is REWRITTEN in place
    ///     when a template has to be resolved — pass the clone the caller
    ///     intends to commit, not a live chord.
    /// </summary>
    public static IChartAction Build(
        ObservableCollection<Chord> placedChords,
        Arrangement? arrangement,
        Chord target,
        Chord oldState,
        Chord newState,
        Action? onPostExecute,
        ObservableCollection<HandShape>? syncedHandShapes = null)
    {
        var mutate = new MutateChordsAction(placedChords, new[] { target }, new[] { oldState }, new[] { newState },
            onPostExecute);

        if (arrangement == null || target.TemplateId < 0) return mutate;

        var created = new List<ChordTemplate>();
        newState.TemplateId = ChordTemplateResolver.ResolveForChord(arrangement, newState, created).Id;

        // The hand shape over the chord follows it onto the new template, in the same undo node.
        var follow = syncedHandShapes == null
            ? null
            : ChordHandShapeSync.BuildAction(syncedHandShapes,
                ChordHandShapeSync.Follow(syncedHandShapes, placedChords,
                    [new ChordHandShapeSync.ChordMove(target, target.TemplateId, newState.TemplateId)]),
                onPostExecute);
        if (created.Count == 0 && follow == null) return mutate;

        // The template add runs FIRST: MutateChordsAction's post-execute is the
        // spacing enforcer, which resolves template ids.
        var actions = new List<IChartAction>(3);
        if (created.Count > 0) actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, created));
        actions.Add(mutate);
        if (follow != null) actions.Add(follow);
        return new CompositeAction(actions);
    }
}
