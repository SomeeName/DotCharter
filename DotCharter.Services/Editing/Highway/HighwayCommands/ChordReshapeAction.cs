using System.Collections.ObjectModel;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     The one way to commit a chord whose NOTE SET or FRETS changed.
///     <para>
///         A <see cref="ChordTemplate" /> is SHARED — one instance backs every
///         chord AND hand shape with that shape — so the new shape must be
///         resolve-or-created and the chord repointed, never the template
///         mutated (the v0.33.2 corruption). And in an <c>.rscp</c> a
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
        Action? onPostExecute)
    {
        var mutate = new MutateChordsAction(placedChords, new[] { target }, new[] { oldState }, new[] { newState },
            onPostExecute);

        if (arrangement == null || target.TemplateId < 0) return mutate;

        var created = new List<ChordTemplate>();
        newState.TemplateId = ChordTemplateResolver.ResolveForChord(arrangement, newState, created).Id;
        if (created.Count == 0) return mutate;

        // The template add runs FIRST: MutateChordsAction's post-execute is the
        // spacing enforcer, which resolves template ids.
        return new CompositeAction(new IChartAction[]
        {
            new AddChordTemplatesAction(arrangement.ChordTemplates, created),
            mutate
        });
    }
}
