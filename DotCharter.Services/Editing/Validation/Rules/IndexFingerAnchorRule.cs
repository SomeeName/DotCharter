using DotCharter.Services.Editing.Anchors;
namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Editor on Fire's finger/hand-position agreement check, ported per note
///     (<c>EOF_SRC/song.c:12577</c>):
///     <para>
///         <i>"If there is a fret hand position in effect, this string is defined
///         as being fretted with the index finger, but is also defined as using
///         the index finger at a different fret than the FHP"</i> — EOF returns
///         <c>-2</c>, refuses to save cleanly, and shows
///         <i>"This note is specified as using the index finger, but the fret
///         hand position is on a different fret. It's recommended to change the
///         fret hand position to match."</i>
///     </para>
///     <para>
///         <b>The FHP fret IS where the index finger is.</b> That is not a
///         convention, it is the arithmetic Rocksmith itself uses to decide which
///         finger plays a fret: <c>1 + (fret − anchor.fret) − (anchor.width − 4)</c>.
///         So a chord authored with finger 1 on fret 1 under an anchor on fret 12
///         does not merely look wrong — the game derives a different finger than
///         the one the chart says, and the on-screen fingering contradicts the
///         tab. The owner hit exactly this on the first note of their Polkadot
///         Stingray chart: finger-1 barre, anchor at 12.
///     </para>
///     <para>
///         Only the INDEX finger's fret is checked — it is the only one whose
///         position the FHP fixes, and an unfingered string carries no claim to
///         contradict. EOF reads an authored finger 1 alone; a chord fingered
///         with no finger 1 (2-3-4 on fret 5) states the index just as surely, so
///         <c>IndexFingerPosition.Find</c> infers it from the lowest finger
/// and the message says so.
///     </para>
///     <para>
///         <b>A chord with no finger 1 under a hand shape I46 reports is left to
/// I46</b> — the same grip, the same anchor, one mistake. Two
///         findings offered two different fixes for it; the I46 one names both.
///     </para>
/// </summary>
public sealed class IndexFingerAnchorRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;

        foreach (var level in arr.Levels)
        {
            if (level.Anchors.Count == 0) continue;

            foreach (var chord in level.Chords)
            {
                // The negative-id guard is ChordTemplateResolver.FindById's, kept
                // here because the context's index does not carry it.
                var template = chord.TemplateId < 0 ? null : context.ChordTemplateById(chord.TemplateId);
                if (template == null || template.Fingers.Count == 0) continue;

                var anchor = context.ActiveAnchor(level, chord.Position);
                if (anchor == null) continue;

                // Shared with the CORRECTOR (AppConfig.SyncAnchorToChordFingering)
                // so the two cannot drift: a fixer that "corrects" an anchor this
                // rule still flags — or flags one it just wrote — is worse than
                // having neither. Open-string and barre handling live there.
                if (IndexFingerPosition.Find(template, arr.Capo) is not { } index || index.Allows(anchor.Fret)) continue;
                // One mistake, one finding: a grip with no finger 1 under a
                // hand shape I46 reports is that shape's missing barre, and the
                // I46 sentence already names the inferred fret and both fixes.
                if (index.Inferred && UnderReportedShape(context, level, chord, anchor)) continue;

                var stated = index.Inferred
                    ? $"This chord has no finger 1, but its finger {index.FromFinger} on fret {index.FromFret} puts " +
                      $"the index finger on {index.Frets} (inferred)"
                    : $"This chord is fingered with the index finger on fret {index.Fret}";
                issues.Add(new ArrangementIssue(
                    ArrangementIssueType.IndexFingerNotOnAnchorFret,
                    $"{stated}, but the fret-hand " +
                    $"position is on fret {anchor.Fret}. Rocksmith derives the playing finger from the " +
                    "anchor, so it will show a different fingering than the chart states — move the " +
                    "anchor to match, or re-finger the chord.",
                    chord.Position.GetPositionMs(beats),
                    Chord: chord,
                    Anchor: anchor));
            }
        }
    }

    /// <summary>
    ///     True when a hand shape on <paramref name="level" /> covering the chord,
    ///     bound to the chord's own template, is reported by I46 against the same
    ///     anchor (<see cref="HandShapeRule.MissingBarreAnchor" />, the predicate
    ///     the rule itself runs). Asked only for a chord E04 would otherwise
    ///     report, so the scan of the level's shapes stays off the clean path.
    /// </summary>
    private static bool UnderReportedShape(ArrangementValidationContext context, ChartLevel level, Chord chord,
        FretAnchor anchor)
    {
        for (var i = 0; i < level.HandShapes.Count; i++)
        {
            var hs = level.HandShapes[i];
            if (hs.TemplateId != chord.TemplateId || !hs.Covers(chord.Position)) continue;
            if (ReferenceEquals(HandShapeRule.MissingBarreAnchor(context, level, i), anchor)) return true;
        }

        return false;
    }
}
