namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Editing-time ports of three pre-export gates that also run in
///     the project-level <c>ChartExportValidator</c>. Surfacing them as live
///     <see cref="IArrangementRule"/>s means the FEHLER inspector flags them
///     while charting (with click-to-select of the offending element) instead
///     of only when the author explicitly runs the export readiness check:
///     <list type="bullet">
///         <item>tone gaps — a <c>ToneChange</c> pointing at a tone the
///         arrangement never declares (starting tone / base / A–D / tone names);</item>
///         <item>first-note silence — content begins before the 1&#160;s lead-in
///         the in-game intro count needs;</item>
///         <item>empty phrases — a non-structural phrase region that contains no
///         notes or chords.</item>
///     </list>
///     Pure: derives everything from the domain model + beat grid, no I/O.
/// </summary>
public sealed class ToneSilenceAndPhraseRule : IArrangementRule
{
    /// <summary>Minimum lead-in before the first note (mirrors <c>ChartExportValidator</c>).</summary>
    private const double MinFirstNoteMs = 1000.0;

    // Position slop (ms) so beat-grid round-trips don't create false boundaries.
    private const double Epsilon = 0.5;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;

        CheckToneGaps(arr, beats, issues);
        CheckToneBodies(arr, beats, context.Project, issues);
        CheckFirstNoteSilence(arr, beats, issues);
        CheckEmptyPhrases(context, issues);
    }

    // ---- tone gaps -------------------------------------------------------

    private static void CheckToneGaps(Arrangement arr, IList<BeatMarker> beats, ICollection<ArrangementIssue> issues)
    {
        if (arr.ToneChanges.Count == 0) return;

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { arr.StartingTone, arr.ToneBase, arr.ToneA, arr.ToneB, arr.ToneC, arr.ToneD })
        {
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name.Trim());
        }

        foreach (var name in arr.ToneNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name.Trim());
        }

        // Rocksmith switches only to a tone the arrangement DECLARES, and it has
        // exactly four switchable slots. Which of those two facts the charter has
        // run into decides what they can do about it, so the message has to say
        // which — "no matching tone declared" told them neither, and the owner
        // read it as the app inventing a concept ("tf are tone slots a to d").
        var free = new[] { arr.ToneA, arr.ToneB, arr.ToneC, arr.ToneD }
            .Count(string.IsNullOrWhiteSpace);

        foreach (var change in arr.ToneChanges)
        {
            var name = change.ToneName;
            if (!string.IsNullOrWhiteSpace(name) && declared.Contains(name.Trim()))
                continue;

            var timeMs = change.Position.GetPositionMs(beats);
            var shown = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name.Trim();
            var remedy = free > 0
                ? "Put it in a free tone slot in the Tone designer (a base tone plus A–D), or run Auto-fix."
                : "All four tone slots (A–D) are taken and Rocksmith allows no more — reuse one of " +
                  string.Join(", ", new[] { arr.ToneA, arr.ToneB, arr.ToneC, arr.ToneD }
                      .Where(s => !string.IsNullOrWhiteSpace(s))) +
                  ", or free a slot in the Tone designer.";

            issues.Add(new ArrangementIssue(
                ArrangementIssueType.ToneChangeWithoutMatchingTone,
                $"Tone change to '{shown}' is not declared on this arrangement, so the game cannot " +
                $"switch to it. {remedy}",
                timeMs));
        }
    }

    // ---- tone bodies -----------------------------------------------------

    /// <summary>
    ///     A tone change whose name the arrangement DECLARES but the project owns
    ///     no BODY for. The chart is internally consistent — <see cref="CheckToneGaps" />
    ///     is perfectly happy with it — and it still plays nothing at that marker,
    ///     because there is no tone JSON to build a rig from.
    ///
    ///     <para><b>Why this was invisible:</b> the gap check compares tone changes
    ///     against the arrangement's SLOTS and stops there. Bodies live on the
    ///     PROJECT, which validation could not see at all, so a psarc-opened chart
    ///     — which carried no bodies whatsoever until the loader imported them —
    ///     validated completely clean while not one of its tone changes could be
    ///     heard. <c>ChartExportValidator</c> caught it, but only at export.</para>
    ///
    ///     <para>Skipped entirely when the project is unknown (null): a caller that
    ///     did not supply one must not be told every tone in the chart is
    ///     missing.</para>
    /// </summary>
    private static void CheckToneBodies(
        Arrangement arr, IList<BeatMarker> beats, ChartProject? project,
        ICollection<ArrangementIssue> issues)
    {
        if (project is null || arr.ToneChanges.Count == 0) return;

        foreach (var change in arr.ToneChanges)
        {
            var name = change.ToneName;
            if (string.IsNullOrWhiteSpace(name)) continue;          // CheckToneGaps owns the unnamed case
            if (!ChartToneNames.IsDeclared(arr, name)) continue;    // …and the undeclared one
            if (ChartToneNames.HasBody(project, name)) continue;

            issues.Add(new ArrangementIssue(
                ArrangementIssueType.ToneChangeWithoutDefinition,
                $"Tone change to '{name.Trim()}' has no tone body in this project, so it will play " +
                "silently. Author it in the Tone designer.",
                change.Position.GetPositionMs(beats)));
        }
    }

    // ---- first-note silence ----------------------------------------------

    private static void CheckFirstNoteSilence(
        Arrangement arr, IList<BeatMarker> beats, ICollection<ArrangementIssue> issues)
    {
        GuitarNote? firstNote = null;
        Chord? firstChord = null;
        var best = double.MaxValue;

        foreach (var level in arr.Levels)
        {
            foreach (var note in level.Notes)
            {
                var t = note.Position.GetPositionMs(beats);
                if (t < best)
                {
                    best = t;
                    firstNote = note;
                    firstChord = null;
                }
            }

            foreach (var chord in level.Chords)
            {
                var t = chord.Position.GetPositionMs(beats);
                if (t < best)
                {
                    best = t;
                    firstChord = chord;
                    firstNote = null;
                }
            }
        }

        if (firstNote == null && firstChord == null) return;
        if (best >= MinFirstNoteMs) return;

        issues.Add(new ArrangementIssue(
            ArrangementIssueType.FirstNoteTooEarly,
            $"The first note begins at {best:F0} ms; leave at least {MinFirstNoteMs:F0} ms of lead-in " +
            "silence so the in-game intro count has room.",
            best, Note: firstNote, Chord: firstChord));
    }

    // ---- empty phrases ---------------------------------------------------

    /// <summary>
    ///     <b>Both phrase stores.</b> This keyed on <see cref="EventPoint.Phrase" />
    ///     alone and therefore found ZERO phrases in every RS-XML / psarc import,
    ///     reporting "no empty phrases" on charts full of them — the same trap
    ///     <c>PhraseRule</c> and the phrase-anchor fixer were in. The markers come
    ///     from the context, which builds them once through
    ///     <c>Core/Utilities/PhraseMarkers</c>.
    /// </summary>
    private static void CheckEmptyPhrases(
        ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;

        var phraseEvents = context.Phrases;
        if (phraseEvents.Count < 2) return;

        var contentTimes = new List<double>();
        foreach (var level in arr.Levels)
        {
            foreach (var note in level.Notes)
                contentTimes.Add(note.Position.GetPositionMs(beats));
            foreach (var chord in level.Chords)
                contentTimes.Add(chord.Position.GetPositionMs(beats));
        }

        // No content at all is an empty-arrangement problem, not an empty-phrase one.
        if (contentTimes.Count == 0) return;
        contentTimes.Sort();

        for (var i = 0; i < phraseEvents.Count; i++)
        {
            // The first phrase is meant to be empty (see FirstPhraseNotEmpty),
            // and structural / noguitar phrases legitimately hold no notes.
            if (i == 0) continue;
            var ep = phraseEvents[i];
            if (IsStructuralOrSilent(ep.Name)) continue;

            var startMs = ep.Position.GetPositionMs(beats);
            // A phrase inside a noguitar SECTION is silent by design, whatever its name.
            if (context.IsInsideNoguitar(startMs)) continue;
            var endMs = i + 1 < phraseEvents.Count
                ? phraseEvents[i + 1].Position.GetPositionMs(beats)
                : double.PositiveInfinity;

            if (HasContentInRange(contentTimes, startMs, endMs)) continue;

            issues.Add(new ArrangementIssue(
                ArrangementIssueType.EmptyPhrase,
                $"Phrase '{ep.Name}' spans no notes or chords — mark silent gaps as a noguitar phrase " +
                "or remove the empty phrase.",
                startMs));
        }
    }

    private static bool HasContentInRange(List<double> sortedTimes, double startMs, double endMs)
    {
        foreach (var t in sortedTimes)
        {
            if (t < startMs - Epsilon) continue;
            if (t >= endMs - Epsilon) break; // sorted → nothing further qualifies
            return true;
        }
        return false;
    }

    private static bool IsStructuralOrSilent(string phrase) =>
        phrase.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("END", StringComparison.OrdinalIgnoreCase)
        || phrase.StartsWith("mover", StringComparison.OrdinalIgnoreCase)
        || phrase.StartsWith("noguitar", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("ng", StringComparison.OrdinalIgnoreCase);
}
