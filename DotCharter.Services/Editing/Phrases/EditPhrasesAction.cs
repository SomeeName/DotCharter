using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Phrases;

/// <summary>
///     A phrase edit of one arrangement as a before/after snapshot of both phrase stores: the
///     phrase list, the iterations with their ids, the phrase metadata and the event points with
///     their phrase text. Undo and redo restore a snapshot whole, so every id renumbering and
///     marker removal comes back exactly.
/// </summary>
public sealed class EditPhrasesAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: phrases are song structure, not measure content.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly Arrangement _arrangement;
    private readonly PhraseState _before;
    private readonly PhraseState _after;

    /// <summary>Runs <paramref name="edit" /> once to learn its result, then puts the arrangement back.</summary>
    public EditPhrasesAction(Arrangement arrangement, Action<Arrangement> edit)
    {
        _arrangement = arrangement;
        _before = PhraseState.Of(arrangement);
        edit(arrangement);
        _after = PhraseState.Of(arrangement);
        _before.Restore(arrangement);
    }

    public bool HasEffect => !_before.SameAs(_after);

    public void Execute() => _after.Restore(_arrangement);

    public void Undo() => _before.Restore(_arrangement);

    private sealed record PhraseState(
        List<string> Phrases,
        List<PhraseIteration> Iterations,
        List<int> IterationIds,
        List<KeyValuePair<string, PhraseInfo>> Infos,
        List<EventPoint> Events,
        List<string> EventPhrases)
    {
        public static PhraseState Of(Arrangement a) => new(
            [.. a.Phrases], [.. a.PhraseIterations], [.. a.PhraseIterations.Select(i => i.PhraseId)],
            [.. a.PhraseInfos], [.. a.EventPoints], [.. a.EventPoints.Select(e => e.Phrase)]);

        public bool SameAs(PhraseState other) =>
            Phrases.SequenceEqual(other.Phrases, StringComparer.Ordinal)
            && Iterations.SequenceEqual(other.Iterations, ReferenceEqualityComparer.Instance)
            && IterationIds.SequenceEqual(other.IterationIds)
            && Infos.SequenceEqual(other.Infos)
            && Events.SequenceEqual(other.Events, ReferenceEqualityComparer.Instance)
            && EventPhrases.SequenceEqual(other.EventPhrases, StringComparer.Ordinal);

        public void Restore(Arrangement a)
        {
            a.Phrases.Clear();
            a.Phrases.AddRange(Phrases);
            Replace(a.PhraseIterations, Iterations);
            for (var i = 0; i < Iterations.Count; i++) Iterations[i].PhraseId = IterationIds[i];
            a.PhraseInfos.Clear();
            foreach (var (name, info) in Infos) a.PhraseInfos[name] = info;
            Replace(a.EventPoints, Events);
            for (var i = 0; i < Events.Count; i++) Events[i].Phrase = EventPhrases[i];
        }

        private static void Replace<T>(IList<T> live, List<T> items) where T : class
        {
            if (live.SequenceEqual(items, ReferenceEqualityComparer.Instance)) return;
            live.Clear();
            foreach (var item in items) live.Add(item);
        }
    }
}
