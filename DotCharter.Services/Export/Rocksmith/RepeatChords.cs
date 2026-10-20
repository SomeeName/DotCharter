using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     THE answer to "does this chord show as Rocksmith's frame-only repeat
///     box?" — the one the export ships. The game draws a chord without
///     <c>&lt;chordNote&gt;</c> children as the repeat box, and a chord ships
///     without them when <see cref="RsChordDensity" /> compacts it or the
///     charter forced it (<see cref="Chord.ForceNoNotes" />) — the writer's own
///     <c>withoutChordNotes</c> test. A chord the export does not write as a
///     chord at all (a link landing, split by <c>LinkLandingBreakup</c>; one
///     whose template resolves to nothing) shows no box either.
///     <para>
///         The 2D editor and the 3D preview receive this set as DATA
///         (<c>SnapshotUpdateContext.RepeatChords</c> → <c>ChordFlags.Repeat</c>):
///         <c>DotCharter.Rendering</c> references Core only and may not
///         re-derive the decision (<c>Rendering/RepeatChordSourceLintTests</c>).
///         A 3D preview with its own port of RsCharter's
///         <c>Level.shouldChordShowNotes</c> would run a different algorithm, and
/// a 2D timeline that drew every chord in full would disagree with it.
///     </para>
/// </summary>
public static class RepeatChords
{
    /// <summary>
    ///     The chords of ONE level that show as the repeat box. Reference
    ///     identity (<see cref="Chord" /> does not override equality).
    ///     <paramref name="templates" /> is the arrangement's template list: the
    ///     chord id half of the decision resolves through the same
    ///     <see cref="RsChordTemplateExport" /> table the writer emits.
    ///     Exactly the chords whose <see cref="Explain" /> verdict
    ///     <see cref="RepeatChordVerdict.Ships" />.
    /// </summary>
    public static HashSet<Chord> Compute(IEnumerable<ChordTemplate> templates, IList<GuitarNote> notes,
        IList<Chord> chords, IList<HandShape> handShapes, IList<BeatMarker> beats)
    {
        var repeat = new HashSet<Chord>();
        foreach (var (chord, verdict) in Explain(templates, notes, chords, handShapes, beats))
        {
            if (verdict.Ships)
                repeat.Add(chord);
        }

        return repeat;
    }

    /// <summary>
    ///     Every chord of ONE level with WHY it does or does not ship as the
    /// repeat box — the inspector's line. <see cref="Compute" /> is
    ///     derived from this, so the two cannot disagree. Precedence follows the
    ///     export: a chord the writer does not write as a chord at all ("Split
    ///     notes", its template resolves to nothing, or a link lands on it) is
    ///     never a repeat; then "No notes" forces one; then
    ///     <see cref="RsChordDensity" /> decides.
    /// </summary>
    public static Dictionary<Chord, RepeatChordVerdict> Explain(IEnumerable<ChordTemplate> templates,
        IList<GuitarNote> notes, IList<Chord> chords, IList<HandShape> handShapes, IList<BeatMarker> beats)
    {
        var ids = RsChordTemplateExport.Build(templates, [chords], handShapes);
        var verdicts = RsChordDensity.Explain(notes, chords, handShapes, beats, ids.IndexFor);
        var landed = LinkLandingBreakup.LandedChords(notes, chords);

        foreach (var chord in chords)
        {
            // A split chord ships as notes whatever its template does: the export
            // leaves a template only split chords play out of the list, so its
            // index is -1 by design, not unresolved (RsChordTemplateExport).
            if (LinkLandingBreakup.IsSplit(chord))
                verdicts[chord] = new RepeatChordVerdict(RepeatChordReason.SplitIntoNotes);
            else if (ids.IndexFor(chord) < 0)
                verdicts[chord] = new RepeatChordVerdict(RepeatChordReason.UnresolvedTemplate);
            else if (landed.Contains(chord))
                verdicts[chord] = new RepeatChordVerdict(RepeatChordReason.LinkLanding);
            else if (chord.ForceNoNotes)
                verdicts[chord] = new RepeatChordVerdict(RepeatChordReason.ForcedNoNotes);
        }

        return verdicts;
    }
}
