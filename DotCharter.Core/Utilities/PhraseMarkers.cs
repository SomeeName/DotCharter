using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     The one place that answers "where are this arrangement's phrases?".
///     <para>
///         <b>There are two phrase stores and both can be live at once.</b> The
///         timeline and song-overview editors author phrases as
///         <see cref="EventPoint.Phrase" /> (the EOF-style text marker the
///         <c>.dcproj</c> format keeps), while the RS-XML and psarc readers fill
///         <see cref="Arrangement.Phrases" /> +
///         <see cref="Arrangement.PhraseIterations" /> (the
///         <c>&lt;phraseIterations&gt;</c> element). Open an imported chart and
///         edit it and you have both.
///     </para>
///     <para>
///         <b>Reading only one of them is a silent, whole-feature failure.</b>
///         The RS-XML writer already learned this the hard way — its
///         <c>BuildPhraseExportData</c> carries a comment about phrases added in
///         the editor being discarded from the export entirely — and the same
///         trap caught the phrase/anchor validation: a rule keyed on
///         <c>EventPoint.Phrase</c> finds ZERO phrases in every imported chart,
///         so it never fires and reads as "no problems found". Measured against
///         the 89 hand-finished RS2 exports in <c>TestData/EOF</c>: <b>0 phrase
///         markers via <c>EventPoint</c>, 2,596 via <c>PhraseIterations</c></b>.
///         The pre-existing anchors-at-phrase-starts AUTO-FIX had the same bug
///         and had therefore never done anything to an imported chart.
///     </para>
/// </summary>
public static class PhraseMarkers
{
    /// <summary>One phrase marker: where it is and what it is called.</summary>
    public readonly record struct PhraseMarker(FractionalPosition Position, string Name);

    /// <summary>
    ///     Every phrase marker from BOTH stores, ordered by position and
    ///     deduplicated by position (an imported phrase the editor has since
    ///     re-authored is one marker, not two).
    /// </summary>
    public static List<PhraseMarker> All(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        var markers = new List<PhraseMarker>();
        var seen = new HashSet<FractionalPosition>();

        foreach (var ep in arrangement.EventPoints)
        {
            if (string.IsNullOrWhiteSpace(ep.Phrase)) continue;
            if (seen.Add(ep.Position)) markers.Add(new PhraseMarker(ep.Position, ep.Phrase.Trim()));
        }

        foreach (var iteration in arrangement.PhraseIterations)
        {
            if (!seen.Add(iteration.Position)) continue;

            var name = iteration.PhraseId >= 0 && iteration.PhraseId < arrangement.Phrases.Count
                ? arrangement.Phrases[iteration.PhraseId]
                : "";
            markers.Add(new PhraseMarker(iteration.Position, name ?? ""));
        }

        markers.Sort((a, b) => a.Position.CompareTo(b.Position));
        return markers;
    }

    /// <summary>A phrase list and the iterations that index into it, in time order.</summary>
    public sealed record IterationSet(IReadOnlyList<string> Phrases, IReadOnlyList<PhraseIteration> Iterations);

    /// <summary>
    ///     The arrangement's phrase iterations from BOTH stores: what every per-iteration
    ///     consumer reads (the RS-XML writer, the SNG mapper, the Phrases inspector and
    ///     propagation), so two artifacts built from one chart cannot disagree.
    ///     <list type="bullet">
    ///         <item>Explicit <see cref="Arrangement.Phrases" /> and
    ///         <see cref="Arrangement.PhraseIterations" />: kept as they are (the same
    ///         objects), plus an iteration for every <see cref="EventPoint.Phrase" /> marker at
    ///         a position no explicit iteration covers, minting a phrase name only when it is
    ///         new. ADDITIVE, so an imported chart whose markers mirror its iterations is
    ///         unchanged.</item>
    ///         <item>Markers only (every chart placed in the editor or imported from EOF): one
    ///         iteration per marker, phrase ids by first appearance. A marker's name does NOT
    ///         have to be in <see cref="Arrangement.Phrases" />: nothing in the editor adds it
    ///         there, and the SNG mapper dropped every such marker.</item>
    ///         <item>Neither: the explicit lists as they are.</item>
    ///     </list>
    ///     <paramref name="endPosition" /> is for an EXPORT of a markers-only chart: COUNT is
    ///     added at the start and END at that position when the chart has none. Nothing is
    ///     padded when explicit iterations exist.
    /// </summary>
    public static IterationSet Iterations(Arrangement arrangement, Func<FractionalPosition>? endPosition = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        var explicitPhrases = arrangement.Phrases.ToList();
        var explicitIterations = arrangement.PhraseIterations.ToList();
        if (explicitPhrases.Count > 0 && explicitIterations.Count > 0)
            return MergeMarkers(arrangement, explicitPhrases, explicitIterations);

        var markers = arrangement.EventPoints
            .Where(ep => !string.IsNullOrWhiteSpace(ep.Phrase))
            .OrderBy(ep => ep.Position)
            .Select(ep => (ep.Position, Name: ep.Phrase.Trim()))
            .ToList();
        if (markers.Count == 0) return new IterationSet(explicitPhrases, explicitIterations);

        if (endPosition != null)
        {
            if (!markers.Any(m => m.Name.Equals("COUNT", StringComparison.OrdinalIgnoreCase)))
                markers.Insert(0, (FractionalPosition.Zero, "COUNT"));
            if (!markers.Any(m => m.Name.Equals("END", StringComparison.OrdinalIgnoreCase)))
                markers.Add((endPosition(), "END"));
        }

        markers = markers.OrderBy(m => m.Position).ThenBy(m => StructuralSortKey(m.Name)).ToList();

        var phrases = new List<string>();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, name) in markers)
        {
            if (ids.ContainsKey(name)) continue;
            ids[name] = phrases.Count;
            phrases.Add(name);
        }

        return new IterationSet(phrases, markers.Select(m => new PhraseIteration(m.Position, ids[m.Name])).ToList());
    }

    private static IterationSet MergeMarkers(Arrangement arrangement, List<string> phrases,
        List<PhraseIteration> iterations)
    {
        var covered = new HashSet<FractionalPosition>(iterations.Select(i => i.Position));
        var extra = arrangement.EventPoints
            .Where(ep => !string.IsNullOrWhiteSpace(ep.Phrase) && !covered.Contains(ep.Position))
            .OrderBy(ep => ep.Position)
            .ToList();
        if (extra.Count == 0) return new IterationSet(phrases, iterations);

        var idByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < phrases.Count; i++) idByName.TryAdd(phrases[i], i);

        foreach (var point in extra)
        {
            var name = point.Phrase.Trim();
            if (!idByName.TryGetValue(name, out var id))
            {
                id = phrases.Count;
                phrases.Add(name);
                idByName[name] = id;
            }

            iterations.Add(new PhraseIteration(point.Position, id));
        }

        iterations.Sort((a, b) => a.Position.CompareTo(b.Position));
        return new IterationSet(phrases, iterations);
    }

    private static int StructuralSortKey(string name)
    {
        if (name.Equals("COUNT", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("END", StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    /// <summary>
    ///     Every phrase marker that represents PLAYING, i.e. excluding the
    ///     structural ones. COUNT, END and noguitar carry no sounds, so anything
    ///     that asks a phrase about the fret hand should skip them.
    /// </summary>
    public static List<PhraseMarker> Playable(Arrangement arrangement) =>
        All(arrangement).Where(m => !IsStructural(m.Name)).ToList();

    public static bool IsStructural(string? phrase) =>
        !string.IsNullOrWhiteSpace(phrase)
        && (phrase.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
            || phrase.Equals("END", StringComparison.OrdinalIgnoreCase)
            || phrase.StartsWith("noguitar", StringComparison.OrdinalIgnoreCase)
            || phrase.Equals("ng", StringComparison.OrdinalIgnoreCase));
}
