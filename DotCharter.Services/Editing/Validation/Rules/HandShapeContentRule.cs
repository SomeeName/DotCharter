namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     The other half of Editor on Fire's finger/handshape validator
///     (<c>EOF_SRC/song.c:12596-12612</c>, its <c>-1</c>): a sound played INSIDE
///     a hand shape must agree with the shape it is played under.
///     <para>
///         EOF phrases it against the first note of the arpeggio phrase — "this
///         gem is within an arpeggio/handshape that does not use the specified
///         string", "…but does not use the same fret as the base chord". We
///         compare against the shape's own <see cref="ChordTemplate" /> instead,
///         which is strictly better: in this model a hand shape's template IS its
///         shape, so the declaration is explicit rather than inferred from
///         whichever note happened to come first.
///     </para>
///     <para>
///         <b>Why it matters in-game:</b> the shape is what Rocksmith draws the
///         hand and the fingering from for the whole span. A note inside it on a
///         string the shape never declares, or on a different fret, is rendered
///         with a hand position that cannot play it — the player is shown one
///         thing and asked to do another.
///     </para>
///     <para>
///         <b>Two corpora, two very different answers — quote both.</b> Over the
///         98 hand-finished RS2 exports in <c>TestData/EOF</c>: 9,878 hand shapes,
///         <b>89,267 (sound, string) pairs inside one and 15 disagreements in 5
///         arrangements</b> (the "6 — 0.01%" once quoted here does
///         not reproduce; the order of magnitude does). Over 691 stock
///         Rocksmith arrangements read from the base game's own packages: <b>642,429 pairs and 4,443
///         disagreements — 0.69%</b>, which raised 1,396 findings across 97
///         arrangements before the two guards below.
///     </para>
///     <para>
///         <b>The corpus number could not have measured what this rule reasons
///         about.</b> <c>TestData/EOF</c> contains <b>zero arpeggio templates</b>
///         (0 of 1,681 chordTemplates carry a <c>-arp</c> displayName), so the
///         validation that shipped this rule never exercised the construct the
///         paragraph below is about — and on stock content an arpeggio's
///         sounds routinely fall OUTSIDE its declared grip: <b>2.54% of pairs
///         under an arpeggio disagree against 0.59% under a plain shape</b>, a
///         4.3x rate. EOF also lays a hand shape tight around each chord, while a
///         Rocksmith fingerprint spans the whole ring-out, so the corpus is
///         quiet for a second structural reason as well.
///     </para>
///     <para>
///         The check is ONE-DIRECTIONAL on purpose. Every string the SOUND plays
///         must be declared by the shape; the shape may declare more — requiring
///         the reverse would flag every arpeggio in every chart. But
///         one-directional is NOT enough to make an arpeggio safe: the
///         notes that ring under an arpeggio are
///         frequently on strings the grip never names at all, and that direction
///         is the one this rule checks. Arpeggio templates are therefore SKIPPED
///         outright, as are templates declaring fewer than three strings (RS's
///         hand-POSITION bar, which is where 64% of the stock-content findings
///         came from). Together those take stock content from 1,396 findings
///         to 446 while keeping 9 of the corpus's 15 — the only true positives
///         this rule has ever been shown to catch.
///     </para>
/// </summary>
public sealed class HandShapeContentRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;

        foreach (var level in arr.Levels)
        {
            if (level.HandShapes.Count == 0) continue;

            foreach (var handShape in level.HandShapes)
            {
                // The SAME lookup HandShapeRule uses — the two disagreed about a
                // negative id, so an untemplated shape was silent here and
                // judged against an unnumbered template there.
                var template = context.HandShapeTemplate(handShape);
                // A shape with no template declares nothing, so there is nothing
                // for a note to contradict. HandShapeRule reports it once (D07);
                // saying it from both rules would say it twice.
                if (template == null || template.Frets.Count == 0) continue;

                // An ARPEGGIO's declared grip is not a claim about which sounds
                // may be played under it — stock Rocksmith content routinely
                // lets open strings ring and walks notes through a fingerprint
                // whose template names only the grip. Measured on 691 stock
                // arrangements: arpeggios disagree at 2.54% of (sound, string)
                // pairs against 0.59% for plain shapes.
                //
                // BOTH flags are required. The two import paths each set exactly
                // one of them: `RsXmlSongReader.ReadChordTemplates` sets only
                // `ForceArpeggioInRs` (from a `-arp` displayName) and
                // the package reader's SNG mapper sets only `Arpeggio`
                // (from chord mask 0x1). Keying on either alone works on one
                // path and is silently dead on the other.
                if (template.IsArpeggio) continue;

                // A one- or two-string template is Rocksmith's hand-POSITION
                // bar, not a grip: it says where the hand sits, and the sounds
                // under it are free to use the rest of the neck the hand can
                // reach. 64% of the findings on stock content came from
                // these, and no chord shape worth contradicting is narrower
                // than three strings.
                if (template.Frets.Count < 3) continue;

                foreach (var note in level.Notes)
                {
                    if (!handShape.Covers(note.Position)) continue;
                    var issue = Check(template, note.StringIndex, note.Fret, handShape);
                    if (issue == null) continue;

                    issues.Add(new ArrangementIssue(
                        ArrangementIssueType.SoundViolatesHandShape, issue,
                        note.Position.GetPositionMs(beats),
                        Note: note, HandShape: handShape, StringIndex: note.StringIndex));
                }

                foreach (var chord in level.Chords)
                {
                    if (!handShape.Covers(chord.Position)) continue;

                    // One finding per SOUND, like EOF's per-note return — a
                    // three-string disagreement is one mistake, not three.
                    foreach (var chordNote in chord.ChordNotes)
                    {
                        var issue = Check(template, chordNote.StringIndex, chordNote.Fret, handShape);
                        if (issue == null) continue;

                        issues.Add(new ArrangementIssue(
                            ArrangementIssueType.SoundViolatesHandShape, issue,
                            chord.Position.GetPositionMs(beats),
                            Chord: chord, HandShape: handShape, StringIndex: chordNote.StringIndex));
                        break;
                    }
                }
            }
        }
    }

    private static string? Check(ChordTemplate template, int stringIndex, int fret, HandShape handShape)
    {
        // A ghost gem's string is HELD, not played: a tapped harmonic sounds at fret 17
        // over a grip that holds fret 5 there, so whatever the string plays says nothing.
        if (template.IsGhost(stringIndex)) return null;

        if (!template.Frets.TryGetValue(stringIndex, out var declared))
        {
            return $"This sound plays string {stringIndex + 1}, which the hand shape " +
                   $"'{Describe(handShape, template)}' covering it does not use — the shape drawn in-game " +
                   "cannot play it.";
        }

        return declared == fret
            ? null
            : $"This sound plays string {stringIndex + 1} at fret {fret}, but the hand shape " +
              $"'{Describe(handShape, template)}' covering it declares fret {declared} there.";
    }

    private static string Describe(HandShape handShape, ChordTemplate template) =>
        !string.IsNullOrWhiteSpace(template.Name) ? template.Name
        : !string.IsNullOrWhiteSpace(handShape.Name) ? handShape.Name
        : "unnamed";
}
