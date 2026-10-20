namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     E06 — every populated phrase must own an anchor, rather than inheriting
///     one that started in an earlier phrase.
///     <para>
///         Ported from Editor on Fire's
///         <c>eof_enforce_rs_phrase_begin_with_fret_hand_position</c>
///         (<c>EOF_SRC/rs.c:5035</c>), which refuses a clean save over it and
///         offers the same one-click fix.
///     </para>
///     <para>
///         <b>Why an anchor may not simply span a phrase boundary.</b>
///         Rocksmith builds each phrase independently for dynamic difficulty —
///         it assembles the levels a phrase needs from that phrase's own
///         content. An anchor that merely overlaps the boundary is therefore
///         not carried in, and the phrase is played with no fret hand position
///         at all: the game has nothing to draw the hand from and nothing to
///         derive fingering against. In the editor it looks fine, because the
///         anchor is visibly right there.
///     </para>
///     <para>
///         The correction restates the SAME fret at the phrase start, so the
///         chart sounds and looks identical — the anchor is split in two rather
///         than moved. That is why it is safe to apply in bulk
///         (<c>ArrangementAutoFixService.AddMissingAnchorsAtPhraseStarts</c>).
///     </para>
///     <para>
///         Two details follow EOF exactly and both suppress false positives:
///         the check is <b>per difficulty level</b> and only fires for a phrase
///         that has at least one sound IN THAT LEVEL (EOF's
///         <c>eof_time_range_is_populated</c>), and an anchor anywhere between
///         the phrase start and the phrase's first sound satisfies it — it need
///         not sit exactly on the boundary.
///     </para>
///     <para>
///         <b>Measured before it shipped.</b> Over the 89 hand-finished RS2
///         exports in <c>TestData/EOF</c> — 2,596 playable phrase markers,
///         74,871 sounds — it fires <b>11 times, all inside a single
///         arrangement</b> (0.42%). That is the licence to have it: EOF enforces
///         the same rule at save but lets the charter answer "No", so a real
///         chart CAN ship with them, and a rule that fired across the corpus
///         would be measuring itself rather than the charts.
///         Harness: <c>LocalOnly_PhraseAnchorFalsePositiveProbe</c>.
///     </para>
/// </summary>
public sealed class PhraseAnchorRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;

        // BOTH phrase stores — see PhraseMarkers. Keying on EventPoint alone
        // finds zero phrases in any imported chart, so the rule would silently
        // never fire there. Built once per pass by the context.
        var phrases = context.PlayablePhrases;
        if (phrases.Count == 0) return;

        foreach (var level in arr.Levels)
        {
            if (level.Notes.Count == 0 && level.Chords.Count == 0) continue;

            for (var i = 0; i < phrases.Count; i++)
            {
                var start = phrases[i].Position;
                var hasEnd = i + 1 < phrases.Count;
                var end = hasEnd ? phrases[i + 1].Position : default;

                // The first sound this level plays inside the phrase. No sound
                // here means the phrase is empty AT THIS LEVEL and Rocksmith
                // asks nothing of the hand — exactly EOF's populated test.
                FractionalPosition? firstSound = null;

                foreach (var note in level.Notes)
                {
                    if (note.Position >= start && (!hasEnd || note.Position < end)
                                               && (firstSound == null || note.Position < firstSound.Value))
                    {
                        firstSound = note.Position;
                    }
                }

                foreach (var chord in level.Chords)
                {
                    if (chord.Position >= start && (!hasEnd || chord.Position < end)
                                                && (firstSound == null || chord.Position < firstSound.Value))
                    {
                        firstSound = chord.Position;
                    }
                }

                if (firstSound == null) continue;

                var covered = level.Anchors.Any(a => a.Position >= start && a.Position <= firstSound.Value);
                if (covered) continue;

                var inherited = context.ActiveAnchor(level, firstSound.Value);
                var suffix = inherited == null
                    ? " No earlier anchor exists to carry in, so this phrase has no hand position at all."
                    : $" The anchor in force starts in an earlier phrase (fret {inherited.Fret}); restate it " +
                      "at this phrase's start so it stops crossing the boundary.";

                issues.Add(new ArrangementIssue(
                    ArrangementIssueType.AnchorMissingAtPhraseStart,
                    $"Phrase '{phrases[i].Name}' plays notes in this difficulty but defines no anchor." + suffix,
                    start.GetPositionMs(beats),
                    Anchor: inherited));
            }
        }
    }

}
