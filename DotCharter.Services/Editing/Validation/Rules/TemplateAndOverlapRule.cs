namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Chord-template fingering checks and structural overlap detection,
///     ported from Log Charter's ChordTemplatesValidator and its
///     overlapping-positions sweep: fingers on open strings, missing fingers
///     on fretted strings, index finger off the lowest fret, plus items
///     stacked on identical positions (notes per string, anchors,
///     hand shapes).
/// </summary>
public sealed class TemplateAndOverlapRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        var capo = context.Arrangement.Capo;

        ValidateTemplates(context, beats, capo, issues);
        ValidateOverlaps(context, beats, issues);
    }

    /// <summary>
    ///     Where a template is judged, and what the finding hangs off. A HAND
    ///     SHAPE is a use of a template exactly as a chord is — see
    ///     <see cref="ValidateTemplates" />.
    /// </summary>
    private readonly record struct TemplateUse(
        ChordTemplate Template, FractionalPosition Position, Chord? Chord, HandShape? Shape);

    /// <summary>
    ///     L06/L07/L08, once per referenced template, anchored at its first use.
    ///     <para>
    ///         <b>A hand shape is a USE.</b> This walked <c>level.Chords</c>
    ///         only, so a template that no chord points at was never
    ///         fingering-checked at all — the three wrong-fingering rules could
    ///         not structurally reach it. Those are exactly the let-ring shapes
    ///         <c>GpHandShapeBuilder.FindOrCreateTemplate</c> mints, whose
    ///         fingering is MACHINE-GUESSED by
    ///         <c>ChordTemplateFingerSetter.SetSuggestedFingers</c> (documented
    ///         at 74.6% exact): the shapes most likely to carry wrong fingering
    ///         were the only ones exempt from the check for it.
    ///     </para>
    ///     <para>
    ///         Chords are swept first so a template used by both still anchors
    ///         exactly where it always did — a shape only ever supplies an
    ///         anchor no chord offered.
    ///     </para>
    ///     <para>
    ///         <b>A chord on the <c>-1</c> sentinel is judged, not skipped.</b>
    ///         An opening <c>TemplateId &lt; 0 ⇒ continue</c>
    ///         would turn a missing input into "no issue";
    ///         <see cref="ArrangementValidationContext.ChordTemplateFor" />
    ///         resolves the shape the chord's notes actually play instead. The
    ///         residue it cannot answer for is reported once, by
    ///         <see cref="ChordRule" /> (D06) — stating it from both rules would
    ///         say the same thing twice.
    ///     </para>
    ///     <para>
    /// <b>A FULLY fret-hand-muted chord is no use of a grip</b>. Split chords,
    ///         including unmuted pick slides, are loose notes. EOF's rule, with its default
    ///         <c>eof_fingering_checks_include_mutes = 0</c> (<c>main.c:240</c>):
    ///         <c>eof_pro_guitar_note_fingering_valid</c> skips every muted string
    ///         and calls a note with only open or muted strings and no fingering
    ///         VALID (<c>rs.c:3628-3631</c>), and its chord-template export leaves a
    ///         muted string out of the grip altogether (<c>rs.c:926-927</c>). A template
    ///         that an ordinary chord or a hand shape also plays is still judged
    ///         there.
    ///     </para>
    ///     <para>
    ///         <b>A <see cref="ChordTemplate.Fingerless" /> template is not judged
    ///         at all</b> — EOF's fingerless status, whose fingering EOF calls
    ///         valid (<c>rs.c:3593-3596</c>) whatever plays it.
    ///     </para>
    /// </summary>
    private static void ValidateTemplates(ArrangementValidationContext context, IList<BeatMarker> beats,
        int capo, ICollection<ArrangementIssue> issues)
    {
        var firstUse = new Dictionary<int, TemplateUse>();
        foreach (var level in context.Arrangement.Levels)
        {
            var looseNotes = ChordWorkEligibility.SoundsWithoutTemplate(level);
            foreach (var chord in level.Chords)
            {
                // NOT `chord.TemplateId < 0 ⇒ continue`: that reported a chord
                // still on the -1 sentinel as having no fingering problem
                // instead of judging the shape it actually plays. The context
                // resolves it — see ArrangementValidationContext.ChordTemplateFor.
                if (!ChordWorkEligibility.UsesFingering(chord, looseNotes)) continue;
                var template = context.ChordTemplateFor(chord);
                if (template == null) continue;
                if (!firstUse.ContainsKey(template.Id))
                    firstUse[template.Id] = new TemplateUse(template, chord.Position, chord, null);
            }
        }

        foreach (var level in context.Arrangement.Levels)
        {
            foreach (var shape in level.HandShapes)
            {
                if (shape.TemplateId < 0) continue;
                var template = context.ChordTemplateById(shape.TemplateId);
                if (template == null) continue;
                if (!firstUse.ContainsKey(template.Id))
                    firstUse[template.Id] = new TemplateUse(template, shape.Position, null, shape);
            }
        }

        foreach (var (_, use) in firstUse)
        {
            var template = use.Template;
            if (template.Frets.Count == 0 || template.Fingerless) continue;

            var timeMs = use.Position.GetPositionMs(beats);
            var lowestFrettedFret = template.GetLowestNotOpenFret(capo);

            foreach (var (stringIndex, fret) in template.Frets)
            {
                var finger = template.GetFinger(stringIndex);
                var isOpen = fret <= capo;

                if (isOpen && finger > 0)
                {
                    issues.Add(Issue(ArrangementIssueType.FingerSetForOpenString, use, timeMs,
                        "Validation_FingerSetForOpenString", stringIndex, [Label(template), finger, stringIndex + 1]));
                }
                else if (ChordWorkEligibility.IsMissingFinger(template, stringIndex, capo))
                {
                    issues.Add(Issue(ArrangementIssueType.FingerMissingOnFrettedString, use, timeMs,
                        "Validation_FingerMissingOnFrettedString", stringIndex, [Label(template), stringIndex + 1, fret]));
                }
                else if (finger == 1 && lowestFrettedFret > 0 && fret > lowestFrettedFret)
                {
                    issues.Add(Issue(ArrangementIssueType.FirstFingerNotOnLowestFret, use, timeMs,
                        "Validation_FirstFingerNotOnLowestFret", stringIndex, [Label(template), fret, lowestFrettedFret]));
                }
            }
        }
    }

    /// <summary>The template's name, or its grip (<c>12-12-x-x-x-x</c>) when it has
    /// none, so a finding never prints <c>''</c>.</summary>
    private static string Label(ChordTemplate template)
    {
        if (template.Name.Length > 0) return template.Name;
        var count = Math.Max(6, template.Frets.Count == 0 ? 0 : template.Frets.Keys.Max() + 1);
        return string.Join("-", Enumerable.Range(0, count)
            .Select(i => template.Frets.TryGetValue(i, out var fret) ? fret.ToString() : "x"));
    }

    /// <summary>The finding hangs off whichever element the template was first
    /// used by, so a shape-only template is still click-to-select.</summary>
    private static ArrangementIssue Issue(ArrangementIssueType type, TemplateUse use, double timeMs,
        string key, int stringIndex, object[] args) =>
        use.Chord != null
            ? ArrangementIssues.Chord(type, use.Chord, timeMs, key, stringIndex, args)
            : ArrangementIssues.Make(type, key, timeMs, handShape: use.Shape, stringIndex: stringIndex, args: args);

    private static void ValidateOverlaps(ArrangementValidationContext context, IList<BeatMarker> beats,
        ICollection<ArrangementIssue> issues)
    {
        foreach (var level in context.Arrangement.Levels)
        {
            // Notes: two sounds on the same string at the same position.
            var seenNotes = new HashSet<(long ms, int stringIndex)>();
            foreach (var note in level.Notes)
            {
                var ms = (long)Math.Round(note.Position.GetPositionMs(beats));
                if (!seenNotes.Add((ms, note.StringIndex)))
                {
                    issues.Add(ArrangementIssues.Note(ArrangementIssueType.OverlappingNotes, note, ms,
                        "Validation_OverlappingNotes"));
                }
            }

            foreach (var chord in level.Chords)
            {
                var ms = (long)Math.Round(chord.Position.GetPositionMs(beats));
                foreach (var cn in chord.ChordNotes)
                {
                    if (!seenNotes.Add((ms, cn.StringIndex)))
                    {
                        issues.Add(ArrangementIssues.Chord(ArrangementIssueType.OverlappingNotes, chord, ms,
                            "Validation_OverlappingChordNotes", cn.StringIndex));
                        break;
                    }
                }
            }

            // Anchors: duplicates on identical positions.
            var seenAnchors = new HashSet<long>();
            foreach (var anchor in level.Anchors)
            {
                var ms = (long)Math.Round(anchor.Position.GetPositionMs(beats));
                if (!seenAnchors.Add(ms))
                {
                    issues.Add(ArrangementIssues.Make(ArrangementIssueType.OverlappingAnchors,
                        "Validation_OverlappingAnchors", ms, anchor: anchor));
                }
            }

            // Hand shapes: spans that intrude into the next shape.
            var shapes = level.HandShapes.OrderBy(h => h.Position).ToList();
            for (var i = 1; i < shapes.Count; i++)
            {
                if (shapes[i].Position < shapes[i - 1].EndPosition)
                {
                    issues.Add(ArrangementIssues.Make(ArrangementIssueType.OverlappingHandShapes,
                        "Validation_OverlappingHandShapes",
                        shapes[i].Position.GetPositionMs(beats), handShape: shapes[i]));
                }
            }
        }
    }
}
