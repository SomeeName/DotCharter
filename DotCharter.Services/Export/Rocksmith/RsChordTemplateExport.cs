using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     The <c>&lt;chordTemplates&gt;</c> list as it will be EMITTED, plus the
///     map from domain <see cref="ChordTemplate.Id" /> to that list's INDEX.
///     <para>
///         <b>RS-XML's <c>chordId</c> is positional, not an id.</b>
///         <c>RsXmlSongReader</c> assigns <c>Id = id++</c> while walking the
///         list, so the attribute means "the Nth template". Writing
///         <see cref="Chord.TemplateId" /> straight through only agreed with
///         that by coincidence — <c>ChordTemplateResolver.NextId</c> is
///         <c>max + 1</c> and undo paths remove templates by instance, so one
///         deleted template shifts every later shape onto the wrong chord.
///         The SNG path has always done it correctly, via
///         <c>SngFromDomainMapper</c>'s <c>TemplateIndexById</c>, and these
///         two artifacts are built from the same chart.
///     </para>
///     <para>
///         Unresolvable references are handled the way the SNG handles them,
///         not by falling back to <c>0</c> (an unrelated shape): a chord with
///         chord notes but no live template gets a SYNTHESIZED nameless
///         template appended here, one with neither is dropped by the caller,
///         and a hand shape pointing at nothing ships as <c>-1</c>.
///     </para>
///     <para>
/// <b>A template that only split chords play is left out</b>.
///         A chord marked "Split notes" ships as single notes
///         (<c>LinkLandingBreakup.IsSplit</c>), so nothing in the file names its
///         template: EOF marks such a chord ignored and its chord list skips it
///         (<c>EOF_SRC/rs.c:2124</c>, <c>:219</c>). A template some other chord
///         or a hand shape names still ships, and one nothing names at all ships
///         as before. Every index below is into the list AFTER the cut, so the
///         later templates' <c>chordId</c>s move down with it — in the
///         <c>&lt;chord&gt;</c>s, the <c>&lt;handShape&gt;</c>s and the SNG alike,
///         because all of them resolve through this table. A split chord is
///         never given a synthesized template either.
///     </para>
///     <para>
///         Built by <c>RsXmlSongWriter.BuildChordTemplateExport</c> for the
///         file, by <c>SngFromDomainMapper.BuildChordTemplates</c> for the SNG,
///         and by <see cref="RepeatChords" /> for the editor's repeat-box
///         preview — the chord id is half of the repeat decision, so the preview
///         asks the same table the writer does.
///     </para>
/// </summary>
internal sealed class RsChordTemplateExport
{
    public List<ChordTemplate> Templates { get; } = [];

    /// <summary>Domain template id → index into <see cref="Templates" />.</summary>
    public Dictionary<int, int> IndexById { get; } = [];

    /// <summary>Chords whose <c>TemplateId</c> resolved to nothing and were
    /// given a synthesized template; keyed by REFERENCE (<c>Chord</c> does not
    /// override equality), like the SNG mapper's own override table.</summary>
    public Dictionary<Chord, int> ChordIndexOverrides { get; } = [];

    public int IndexFor(Chord chord) =>
        ChordIndexOverrides.TryGetValue(chord, out var overridden)
            ? overridden
            : IndexById.GetValueOrDefault(chord.TemplateId, -1);

    /// <summary>
    ///     The arrangement's own templates first — minus the ones only split
    ///     chords play — then a synthesized template per chord whose id resolves
    ///     to nothing, reusing an identical earlier template when there is one.
    ///     <paramref name="chordsByLevel" /> is walked in the order given — the
    ///     writer passes its levels by difficulty. <paramref name="handShapes" />
    ///     are every hand shape the file will write: a template one of them names
    ///     ships.
    /// </summary>
    public static RsChordTemplateExport Build(IEnumerable<ChordTemplate> templates,
        IEnumerable<IEnumerable<Chord>> chordsByLevel, IEnumerable<HandShape> handShapes)
    {
        var export = new RsChordTemplateExport();
        var levels = chordsByLevel.Select(chords => chords.ToList()).ToList();
        var splitOnly = SplitOnlyTemplateIds(levels, handShapes);

        foreach (var template in templates)
        {
            if (splitOnly.Contains(template.Id)) continue;
            export.IndexById[template.Id] = export.Templates.Count;
            export.Templates.Add(template);
        }

        foreach (var chords in levels)
        {
            foreach (var chord in chords)
            {
                if (LinkLandingBreakup.IsSplit(chord)) continue;
                if (export.IndexById.ContainsKey(chord.TemplateId)) continue;
                if (export.ChordIndexOverrides.ContainsKey(chord)) continue;
                if (chord.ChordNotes.Count == 0) continue;

                var synthesized = new ChordTemplate { Name = "" };
                foreach (var chordNote in chord.ChordNotes)
                    synthesized.Frets[chordNote.StringIndex] = chordNote.Fret;

                var existingIndex = export.Templates.FindIndex(t => t.Equals(synthesized));
                if (existingIndex >= 0)
                {
                    export.ChordIndexOverrides[chord] = existingIndex;
                    continue;
                }

                export.ChordIndexOverrides[chord] = export.Templates.Count;
                export.Templates.Add(synthesized);
            }
        }

        return export;
    }

    /// <summary>The template ids a split chord plays and nothing else names: no
    /// chord that ships as a chord, no hand shape.</summary>
    private static HashSet<int> SplitOnlyTemplateIds(List<List<Chord>> levels, IEnumerable<HandShape> handShapes)
    {
        var split = new HashSet<int>();
        var named = new HashSet<int>();
        foreach (var chord in levels.SelectMany(chords => chords))
            (LinkLandingBreakup.IsSplit(chord) ? split : named).Add(chord.TemplateId);
        foreach (var shape in handShapes)
            named.Add(shape.TemplateId);

        split.ExceptWith(named);
        return split;
    }
}
