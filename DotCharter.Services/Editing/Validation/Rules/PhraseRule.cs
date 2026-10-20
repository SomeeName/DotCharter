using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Arrangement-level phrase checks (DLC Builder / Rocksmith2014.NET I-series):
///     first-phrase-not-empty (I25), missing END (I26), more than 100 phrases (I27).
///     <para>
///         <b>Phrases live in TWO stores and this rule reads both.</b> Keying on
///         <see cref="EventPoint.Phrase" /> alone — the editor's store —
///         would miss that an RS-XML or psarc import puts its phrases in
///         <c>Arrangement.Phrases</c> + <c>PhraseIterations</c> instead. With
///         fewer than two markers found the whole rule returns early, so all
///         three checks would be SILENT on every imported chart and the inspector
///         would read as "no problems found". Measured over the 89 hand-finished RS2
///         exports in <c>TestData/EOF</c>: 0 markers via <c>EventPoint</c>, 2,596
///         via <c>PhraseIterations</c>. See <c>Core/Utilities/PhraseMarkers</c>,
///         which is the one place that answers the question.
///     </para>
/// </summary>
public sealed class PhraseRule : IArrangementRule
{
    /// <summary>Rocksmith's ceiling is on the PHRASE LIST, not on how often the
    /// phrases recur — the same reading as <c>ChartExportValidator</c>'s
    /// <c>KeyTooManyPhrases</c> (<c>arrangement.Phrases.Count</c>) and as
    /// Rocksmith2014.NET's own I27.</summary>
    private const int MaxPhrases = 100;

    /// <summary>Reports a missing END phrase. Split out so it can run BEFORE the
    /// two-phrase guard the rest of this rule needs — see the call site.</summary>
    private static void ReportMissingEndPhrase(
        IReadOnlyList<DotCharter.Core.Utilities.PhraseMarkers.PhraseMarker> phrases, ICollection<ArrangementIssue> issues)
    {
        foreach (var phrase in phrases)
        {
            if (phrase.Name.Equals("END", StringComparison.OrdinalIgnoreCase))
                return;
        }

        issues.Add(new ArrangementIssue(
            ArrangementIssueType.NoEndPhrase,
            "The arrangement is missing an END phrase."));
    }

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;

        var phrases = context.Phrases;

        // The END check runs FIRST and is NOT gated on having two phrases. An
        // arrangement with none trivially has no END — and that is the commonest
        // real fault, because a tab import only produces phrases when the tab
        // carried section markers. Behind the `< 2` guard the loudest case said
        // nothing, while ChartExportValidator reported it: the two disagreed in
        // the direction that lets a broken chart through.
        // Two or more phrases: report unconditionally (the historic behaviour).
        // FEWER than two — including NONE, the commonest real case, because a tab
        // import only produces phrases when the tab carried section markers —
        // report only when the arrangement actually SOUNDS. An empty arrangement
        // has nothing to end, and not spamming one is a deliberate contract
        // (`EmptyArrangement_ProducesNoIssues`).
        if (phrases.Count >= 2 || GetFirstNoteOrChordTimeMs(arr, beats) is not null)
            ReportMissingEndPhrase(phrases, issues);

        ReportRewrittenNames(phrases, beats, issues);

        if (phrases.Count < 2) return;

        var secondPhraseTime = phrases[1].Position.GetPositionMs(beats);
        var firstNoteTimeMs = GetFirstNoteOrChordTimeMs(arr, beats);
        if (firstNoteTimeMs is { } t && t < secondPhraseTime)
        {
            issues.Add(new ArrangementIssue(
                ArrangementIssueType.FirstPhraseNotEmpty,
                "The first phrase contains notes; it should be empty so the song fades in cleanly.",
                t));
        }

        var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phrase in phrases) distinctNames.Add(phrase.Name);

        // Counting MARKERS here instead of distinct phrases made every song past
        // a couple of minutes fail a limit it is nowhere near — and put the
        // inspector in open disagreement with the export dialog, which asks the
        // same question of arrangement.Phrases.
        if (distinctNames.Count > MaxPhrases)
        {
            issues.Add(new ArrangementIssue(
                ArrangementIssueType.MoreThan100Phrases,
                $"Arrangement has {distinctNames.Count} distinct phrases; the in-game limit is {MaxPhrases}."));
        }
    }

    /// <summary>
    ///     D10: one finding per phrase whose name Rocksmith will see spelled
    ///     differently. The chart is never rewritten — this is the warning that
    ///     replaced the save-time rename (see <see cref="RsPhraseName" />). The
    ///     spelling shown is the export's own: <see cref="RsPhraseName.SanitizeAll" />
    ///     over the distinct names, so a de-duplicated <c>Riff 2</c> reads as such.
    /// </summary>
    private static void ReportRewrittenNames(
        IReadOnlyList<DotCharter.Core.Utilities.PhraseMarkers.PhraseMarker> phrases, IList<BeatMarker> beats,
        ICollection<ArrangementIssue> issues)
    {
        var names = new List<string>();
        var firstAt = new List<FractionalPosition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phrase in phrases)
        {
            if (!seen.Add(phrase.Name)) continue;
            names.Add(phrase.Name);
            firstAt.Add(phrase.Position);
        }

        var exported = RsPhraseName.SanitizeAll(names);
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], exported[i], StringComparison.Ordinal)) continue;
            issues.Add(new ArrangementIssue(
                ArrangementIssueType.PhraseNameRewrittenForRocksmith,
                $"Phrase \"{names[i]}\" is exported to Rocksmith as \"{exported[i]}\": Rocksmith only accepts " +
                "the letters A-Z, digits, spaces, _ and #. The chart keeps your name.",
                firstAt[i].GetPositionMs(beats)));
        }
    }

    private static double? GetFirstNoteOrChordTimeMs(Arrangement arr, IList<BeatMarker> beats)
    {
        double? best = null;
        foreach (var lvl in arr.Levels)
        {
            foreach (var n in lvl.Notes)
            {
                var t = n.Position.GetPositionMs(beats);
                if (best == null || t < best.Value) best = t;
            }
            foreach (var c in lvl.Chords)
            {
                var t = c.Position.GetPositionMs(beats);
                if (best == null || t < best.Value) best = t;
            }
        }
        return best;
    }
}
