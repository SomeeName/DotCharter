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
///         <c>.rscp</c> format keeps), while the RS-XML and psarc readers fill
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
