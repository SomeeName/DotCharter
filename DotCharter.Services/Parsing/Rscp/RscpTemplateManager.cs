using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using ChordTemplate = DotCharter.Core.Domain.Guitar.ChordTemplate;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Manages chord template regeneration and synchronization for RSCP arrangements
/// </summary>
public class RscpTemplateManager
{
    /// <summary>
    ///     Regenerates chord templates for an arrangement, ensuring all chords reference valid templates
    ///     and handshapes are properly linked to chord templates
    /// </summary>
    /// <remarks>
    ///     Every template is renumbered to its POSITION, because the <c>.rscp</c> is positional:
    ///     a <c>chordTemplate</c> element carries no id and the parser numbers templates by index.
    /// Every user is renumbered with it — see <see cref="RemapUsers" />.
    /// </remarks>
    public void RegenerateTemplates(Arrangement arr)
    {
        var oldTemplates = arr.ChordTemplates.ToList();
        // Keyed by the ids AS THEY ARE, before the renumbering below mutates them:
        // what each user's id resolved to is the template it must still name after.
        var byOldId = new ChordTemplateIdIndex(oldTemplates);
        arr.ChordTemplates.Clear();

        var nextId = 0;
        var signatureMap = new Dictionary<string, ChordTemplate>();

        // Re-add existing templates with new IDs
        foreach (var t in oldTemplates)
        {
            t.Id = nextId++;
            arr.ChordTemplates.Add(t);
            var sig = SignatureOf(t);
            if (!signatureMap.ContainsKey(sig)) signatureMap[sig] = t;
        }

        RemapUsers(arr, byOldId);

        // ONE id -> template map for the whole pass. A
        // linear FirstOrDefault over arr.ChordTemplates would run once per chord
        // and twice per hand shape across every level; see ChordTemplateIdIndex.
        var byId = new ChordTemplateIdIndex(arr.ChordTemplates);

        // Process all chords and ensure they have valid templates
        foreach (var level in arr.Levels)
        {
            foreach (var chord in level.Chords)
            {
                if (chord.ChordNotes.Count == 0) continue;

                var sortedNotes = chord.ChordNotes.OrderBy(n => n.StringIndex).ToList();
                var signature = SignatureOf(sortedNotes.Select(n => (n.StringIndex, n.Fret)), arpeggio: false);

                var templateIsValid = ValidateChordTemplate(chord, byId, sortedNotes);

                if (!templateIsValid)
                    AssignOrCreateTemplate(chord, signature, signatureMap, arr, byId, sortedNotes, ref nextId);
            }

            // Link handshapes to chord templates
            LinkHandShapesToTemplates(level, byId);
        }

        // Ensure at least one template exists
        if (arr.ChordTemplates.Count == 0) arr.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "" });
    }

    /// <summary>
    ///     Moves every user of a template — chords and hand shapes on every level, and
    ///     notation-voice chords — from its OLD id to the new position of the template that id
    ///     resolved to (first template with the id wins, as everywhere else).
    /// </summary>
    /// <remarks>
    ///     <para>
    /// Without this, a chart whose ids were not positions — a gap left by a
    ///         removal, a twin sharing an earlier template's id, ids from another source —
    ///         reopened with its users pointing elsewhere. A chord survived only because its old
    ///         id stopped validating and the pass below re-resolved it by its frets; a hand shape
    ///         over no chord (a let-ring bar) kept the old NUMBER and named whichever template now
    ///         sat at that position, and a notation-voice chord likewise.
    ///     </para>
    ///     <para>
    ///         An id that resolved to NO template becomes the <c>-1</c> sentinel: left alone, it
    ///         could now name a renumbered template it never meant (or one this pass mints). On a
    ///         chart whose ids already are positions this is the identity for every id that
    ///         resolves, so such a chart writes the same bytes as before; only a reference that
    ///         named nothing is now written as the sentinel.
    ///     </para>
    /// </remarks>
    private static void RemapUsers(Arrangement arr, ChordTemplateIdIndex byOldId)
    {
        foreach (var level in arr.Levels)
        {
            foreach (var chord in level.Chords) chord.TemplateId = Renumbered(chord.TemplateId);
            foreach (var handShape in level.HandShapes) handShape.TemplateId = Renumbered(handShape.TemplateId);
        }

        foreach (var voice in arr.NotationVoices)
        {
            foreach (var chord in voice.Chords) chord.TemplateId = Renumbered(chord.TemplateId);
        }

        int Renumbered(int oldId) => byOldId.Find(oldId)?.Id ?? -1;
    }

    /// <summary>
    ///     The save-path find-or-create key: FRETS within one ARPEGGIO flavour —
    ///     the rule documented in <c>docs/agents/services/editing-invariants.md</c> and used by
    ///     <c>ChordTemplateResolver</c> and the tab importers' hand-shape builders.
    ///     <para>
    ///         The arpeggio half is needed here, or this pass could repoint
    ///         a plain chord whose template had gone stale at an ARPEGGIO template
    ///         that merely shares its frets. <c>ChordTemplate.Arpeggio</c> is
    ///         load-bearing — it sets <c>ChordMask.Arpeggio</c> in the SNG export,
    ///         picks the arpeggio section type in <c>.eof</c>, and draws purple
    ///         arpeggio brackets in the 3D highway instead of a hand-shape bar — so
    ///         the chord would silently become an arpeggio on save. Reachable from any
    ///         freshly imported chord (they carry <c>TemplateId</c> -1 until this
    ///         pass runs) whose shape an arpeggio template already describes.
    ///     </para>
    /// </summary>
    private static string SignatureOf(IEnumerable<(int StringIndex, int Fret)> frets, bool arpeggio) => (arpeggio ? "arp|" : "|") + string.Join(",", frets.Select(f => $"{f.StringIndex}:{f.Fret}"));

    private static string SignatureOf(ChordTemplate template)
    {
        return SignatureOf(template.Frets.OrderBy(kvp => kvp.Key).Select(kvp => (kvp.Key, kvp.Value)),
            template.Arpeggio);
    }

    /// <summary>
    ///     True when the chord's currently assigned template really describes the
    ///     shape it plays. An AUTHORED arpeggio template passes: the frets match and
    ///     the charter meant it, so it is left alone — only a chord whose template is
    ///     dangling or the wrong shape is re-assigned.
    /// </summary>
    private static bool ValidateChordTemplate(
        Chord chord, ChordTemplateIdIndex byId, List<ChordNote> sortedNotes)
    {
        // By ID, never by list POSITION. RegenerateTemplates happens to keep the
        // two equal today, but "templates[templateId]" is the confusion that
        // silently resolved a chord onto an unrelated shape everywhere else it
        // appeared (see ChartSpacingEnforcer.FindTemplate).
        var assigned = byId.Find(chord.TemplateId);
        if (assigned == null) return false;

        return SignatureOf(assigned) == SignatureOf(
            sortedNotes.Select(n => (n.StringIndex, n.Fret)), assigned.Arpeggio);
    }

    private static void AssignOrCreateTemplate(
        Chord chord,
        string signature,
        Dictionary<string, ChordTemplate> signatureMap,
        Arrangement arr,
        ChordTemplateIdIndex byId,
        List<ChordNote> sortedNotes,
        ref int nextId)
    {
        if (signatureMap.TryGetValue(signature, out var existingTmpl))
        {
            chord.TemplateId = existingTmpl.Id;
        }
        else
        {
            var newTmpl = new ChordTemplate { Id = nextId++, Name = "" };
            foreach (var n in sortedNotes) newTmpl.Frets[n.StringIndex] = n.Fret;
            arr.ChordTemplates.Add(newTmpl);
            byId.Add(newTmpl);
            signatureMap[signature] = newTmpl;
            chord.TemplateId = newTmpl.Id;
        }
    }

    /// <summary>
    ///     Gives a hand shape that has no usable template one, taken from the
    ///     first chord inside its span. An AUTHORED template is never overwritten.
    /// </summary>
    /// <remarks>
    ///     This must not reassign EVERY hand shape unconditionally — to the first
    ///     chord's template, or to <c>0</c> when no chord lies inside the span.
    ///     Both halves would destroy data, on every save:
    ///     <list type="bullet">
    ///         <item>
    ///             A shape with no chord inside it was repointed at template
    ///             <c>0</c> — an unrelated chord's frets, fingering and name. That
    ///             is exactly the shape
    ///             <c>GpHandShapeBuilder.BuildLetRingHandShapes</c> produces: a
    ///             let-ring run over SINGLE notes, for which 0.34.1 deliberately
    ///             find-or-creates a charting-ready template. Every one of them
    ///             lost it on the first save.
    ///         </item>
    ///         <item>
    ///             Forcing a chord's shape onto that chord's template undid
    ///             <c>ChartSpacingEnforcer</c>'s documented contract — "the chord's
    ///             hand shape deliberately keeps its own (full-shape) template, the
    ///             RS hand-shape bar describes the fingering region, not the
    ///             individual strum" — two passes after the enforcer established it.
    ///         </item>
    ///     </list>
    ///     A shape with no chord and no template keeps the <c>-1</c> "untemplated"
    ///     sentinel (<see cref="HandShape.TemplateId" />); <c>0</c> is a real
    ///     template id and must never be used as a fallback.
    /// </remarks>
    private static void LinkHandShapesToTemplates(ChartLevel level, ChordTemplateIdIndex byId)
    {
        foreach (var hs in level.HandShapes)
        {
            if (byId.Find(hs.TemplateId) != null) continue;

            var firstChord = level.Chords.FirstOrDefault(c =>
                c.Position >= hs.Position && c.Position < hs.EndPosition);
            if (firstChord == null) continue;

            hs.TemplateId = firstChord.TemplateId;

            // Display name only — HandShape.Name is not serialized (the parser
            // hardcodes "Chord"), so this is a live-graph nicety. The reverse
            // write (tmpl.Name = hs.Name) is GONE: a ChordTemplate is SHARED and
            // renaming one in place renames it for every other chord and shape
            // with that shape. Resolve-or-create is not the answer here either —
            // templates dedupe by FRETS, so minting a name-only duplicate would
            // just be re-collapsed by RegenerateTemplates above.
            var tmpl = byId.Find(hs.TemplateId);
            if (!string.IsNullOrEmpty(tmpl?.Name)) hs.Name = tmpl.Name;
        }
    }
}
