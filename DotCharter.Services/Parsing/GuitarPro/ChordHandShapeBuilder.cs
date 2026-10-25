using DotCharter.Services.Editing.HandShapes;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Post-import pass shared by every TAB importer (GP6–8 gpif, GP3–5 binary,
///     Songsterr — all route through <see cref="GpPostProcessor.Run"/>): gives
///     each imported chord a <see cref="HandShape"/> spanning its authored
///     duration (the region the sustain covered) and then trims the chord's own
///     sustain to a short strike — the Rocksmith charting convention where the
///     handshape bar carries the duration and the chord renders as a strike.
///     <para>
///     Runs BEFORE <see cref="GpPostProcessor.AdjustSustains"/> so the span
///     reflects the AUTHORED length rather than a post-trim one. Chord templates
///     are find-or-create (never mutated in place); the created template carries
///     the chord's fret shape with NO fingering data for now.
///     </para>
/// </summary>
public static class ChordHandShapeBuilder
{
    /// <param name="level">The difficulty level whose chords get hand shapes.</param>
    /// <param name="arrangement">The owning arrangement (chord templates, tuning,
    /// string count).</param>
    /// <param name="authoredFingers">Optional import-only per-(string, position)
    /// LEFT-hand fingering read from the source file. A new template prefers the
    /// authored finger over <see cref="ChordTemplateFingerSetter" />'s suggestion,
    /// per string. Null ⇒ suggestion only, i.e. the historical behaviour.</param>
    /// <param name="authoredChordNames">Optional import-only per-position chord
    /// NAME read from the source file's chord diagram. A new template takes the
    /// authored name over <see cref="ChordNameSuggester" />'s guess. Null (or no
    /// entry for the position) ⇒ suggestion only, i.e. the historical
    /// behaviour.</param>
    /// <param name="splitRunsOnRests">Break a repeated-strum run wherever a REST
    /// separates two strums, instead of letting <see cref="ChordHandShapePlanner.MaxMergeGap" /> span
    /// it. A hand-shape bar carries duration, so a bar drawn across a rest tells
    /// the player to keep holding through silence — owner report,
    /// measures 74+ of a real GP8 file, where a quarter strum, an eighth rest and
    /// four more strums became ONE bar over the whole measure. The product
    /// default lives on <see cref="GpImportSettings.PreserveShortRests" /> (on);
    /// this parameter defaults OFF so a direct caller keeps the historical
    /// merge.</param>
    public static void BuildAndTrim(ChartLevel level, Arrangement arrangement,
        GpAuthoredFingering? authoredFingers = null,
        GpAuthoredChordNames? authoredChordNames = null,
        bool splitRunsOnRests = false)
    {
        // The grouping is the shared planner's — the editor's "Generate hand
        // shapes" runs the same one, so the two can never drift apart. What stays
        // HERE is what only an import does: the file's own fingering and names on a
        // minted template, and the strike trim.
        var grips = new ImportGripSource(arrangement, authoredFingers, authoredChordNames);
        var minted = new List<ChordTemplate>();
        var plan = ChordHandShapePlanner.Plan(level, grips, minted,
            new ChordHandShapePlanOptions(SplitRunsOnRests: splitRunsOnRests));

        foreach (var template in minted) arrangement.ChordTemplates.Add(template);

        foreach (var bar in plan)
        {
            level.HandShapes.Add(bar.ToHandShape());

            // Point every chord in the run at the shape it plays. Setting TemplateId
            // on the HAND SHAPE only and leaving the chords
            // on the -1 sentinel until a save round-trip fills it in fails, because ANCHORS
            // are generated during the import, and FretAnchorGenerationService reads
            // a chord's fingering through exactly this id. With -1 every chord note
            // comes through with finger -1 and the fingering-aware root logic (P4/P5)
            // is DEAD on every tab import (owner report: a {5,7,7,6,5} barre
            // fingered 1 at fret 5 sat under an anchor at fret 4).
            foreach (var chord in bar.Chords)
                chord.TemplateId = bar.Template.Id;

            // The hand shape now carries the duration; the chords become short
            // strikes — UNLESS one wears a sustain-visible technique OR is a slide's
            // landing chord (both keep ringing, so both keep sustain).
            foreach (var chord in bar.StrikeChords)
            {
                foreach (var cn in chord.ChordNotes)
                    cn.Sustain = FractionalPosition.Zero;
            }
        }
    }

    /// <summary>The trim exemption — <see cref="ChordHandShapePlanner.HasSustainVisibleTechnique" />,
    /// kept under its import-path name.</summary>
    public static bool HasSustainVisibleTechnique(Chord chord) => ChordHandShapePlanner.HasSustainVisibleTechnique(chord);

    /// <summary>
    ///     Containment, not start-equality: inside a MERGED bar the follower
    ///     chords don't sit at the shape's start, but a re-run must still see them
    ///     as covered (idempotency). Zero-width shapes (clamp leftovers) still
    ///     match by their start. <see cref="HandShapeCoverageIndex" /> answers
    ///     exactly this, without rescanning a list the pass is appending to.
    /// </summary>
    internal static bool HandShapeCoversPosition(ChartLevel level, FractionalPosition position)
    {
        foreach (var hs in level.HandShapes)
        {
            if (hs.Position.CompareTo(position) == 0 ||
                (hs.Position.CompareTo(position) < 0 && position.CompareTo(hs.EndPosition) < 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The import path's grip source: reuse ANY non-arpeggio template with the
    ///     chord's frets, whether hand-authored (its deliberate name and fingering win
    ///     — existing templates are never mutated) or created by an earlier pass; else
    ///     mint one, preferring the file's authored fingering and name.
    /// </summary>
    private sealed class ImportGripSource(
        Arrangement arrangement,
        GpAuthoredFingering? authoredFingers,
        GpAuthoredChordNames? authoredChordNames) : IHandShapeGripSource
    {
        // Minting must not re-scan every template for its Max(Id). Ids only ever grow
        // here, so carry the next one. `Count == 0 ? 0 : Max(Id) + 1` and this agree
        // by construction, including a template list whose ids are sparse.
        private int _nextTemplateId = arrangement.ChordTemplates.Count == 0
            ? 0
            : arrangement.ChordTemplates.Max(t => t.Id) + 1;

        public bool Holds(ChordTemplate grip, Chord chord) => FretsMatch(grip, chord);

        public ChordTemplate GripFor(Chord chord, IList<ChordTemplate> minted)
        {
            // Identical frets = identical chord. The arrangement first, then this
            // pass's own mints — the order the list had when a mint was appended to
            // the arrangement on the spot.
            foreach (var t in arrangement.ChordTemplates)
            {
                if (!t.Arpeggio && FretsMatch(t, chord))
                    return t;
            }

            foreach (var t in minted)
            {
                if (!t.Arpeggio && FretsMatch(t, chord))
                    return t;
            }

            var template = new ChordTemplate { Name = string.Empty };
            foreach (var cn in chord.ChordNotes)
                template.Frets[cn.StringIndex] = cn.Fret;

            // New templates arrive charting-ready (v0.26 ease-of-use): a suggested
            // fingering (shape catalogue + fret-offset fallback) and a suggested name
            // from the sounding pitches. Both are tuning-aware — importers must
            // populate Arrangement.Tuning before the post-processor runs.
            ChordTemplateFingerSetter.SetSuggestedFingers(template, arrangement.StringCount);

            // ...but an AUTHORED finger always beats a suggested one, per string.
            // Guitar Pro carries a left-hand finger per note and both readers
            // keep it: discarding it would have every imported chord fingered by a guesser with a
            // measured 74.6% exact rate while the file says what the hand did.
            // Creation only: a template found by shape is never mutated. Such a
            // template is MARKED, so the FHP pass never re-fingers it, even where the
            // file's fingers equal the suggestion.
            if (authoredFingers != null)
            {
                var authored = false;
                foreach (var cn in chord.ChordNotes)
                {
                    if (!authoredFingers.TryGet(cn.StringIndex, chord.Position, out var finger)) continue;
                    template.Fingers[cn.StringIndex] = finger;
                    authored = true;
                }

                if (authored) authoredFingers.MarkAuthored(template);
            }

            // …and an AUTHORED chord NAME always beats a suggested one. Guitar Pro
            // hangs a chord diagram on the strum's beat and its first real field is the
            // name the arranger typed — a slash chord, an inversion, a "no-chord"
            // marker or a particular enharmonic spelling the pitch-class suggester
            // cannot know. Creation only, like the fingering: a template found by
            // shape is shared and is never mutated, so the first name a shape is seen
            // with wins.
            if (authoredChordNames != null && authoredChordNames.TryGet(chord.Position, out var authoredName))
                template.Name = authoredName;
            else
                template.Name = NewChordNames.For(arrangement, template.Frets);

            template.Id = _nextTemplateId++;
            minted.Add(template);
            return template;
        }
    }

    private static bool FretsMatch(ChordTemplate template, Chord chord)
    {
        if (template.Frets.Count != chord.ChordNotes.Count) return false;
        foreach (var cn in chord.ChordNotes)
        {
            if (!template.Frets.TryGetValue(cn.StringIndex, out var fret) || fret != cn.Fret)
                return false;
        }

        return true;
    }
}
