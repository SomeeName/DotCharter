using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
/// The export pre-pass for a 7-string guitar: a COPY of the arrangement
///     in the package's string order (<see cref="Rs7StringLayout" />) that the SNG mapper
///     writes as it is. The chart itself is never touched.
///     <list type="bullet">
///         <item>Strings, tuning offsets and template columns are renumbered to the
///         package order (string 6 = the 7th string, 7 = the 8th).</item>
///         <item>The breakup the mapper applies (<see cref="LinkLandingBreakup" />:
///         "split notes" chords and chords a link lands on) runs FIRST, so a chord
///         that ships as single notes keeps its extra strings as single notes.</item>
///         <item>A chord that sounds ONLY on extra strings (two or more notes) stays ONE
///         chord: its stock columns are unused and the extra columns go in CHDT/CHNT. One
///         extra note alone is a single note. A template that cannot be a chord is left
///         out with the hand shapes naming it.</item>
///         <item>Every other chord stays ONE chord with all its strings: the mapper writes
///         the six stock columns and <see cref="Rs7BlockBuilder" /> the rest into the
///         internal extension model. The RS Extended Range writer combines these columns; a 5-/6-string bass keeps its low B and high C as the
///         stock columns 4/5.</item>
///         <item>Chord names become printable ASCII of at most 30 characters (the shared template rule).</item>
/// <item>With <c>sixStringOnly</c> the copy is a plain SIX-string arrangement for
///         tools that cannot read the extra strings (EOF aborts on a <c>string</c> above 5,
///         DLC Builder's SNG build indexes six-wide arrays): the chords break up into single notes (the split rules below), then
///         every note on an extra string is dropped, no template keeps an extra column and the
///         tuning is the six stock entries. <see cref="Loss" /> says what was dropped.</item>
///     </list>
/// </summary>
internal sealed class Rs7Projection
{
    /// <summary>The longest chord name the shared model allows (the 32-byte field keeps its last byte 0).</summary>
    public const int MaxChordNameLength = 30;

    private readonly bool _sixStringOnly;
    private int _droppedChords;
    private int _trimmedChords;

    private Rs7Projection(Arrangement source, bool sixStringOnly)
    {
        _sixStringOnly = sixStringOnly;
        // Only the six-string copy breaks a chord up; every other export keeps a chord whole (a 5-/6-string
        // bass too: its low B and high C are columns 4/5 of the stock six-wide slots).
        StringCount = source.StringCount;
        Arrangement = ChartProjectCloner.CloneArrangement(source);
        Renumber(source);
        var splitTemplates = new HashSet<int>();
        foreach (var level in Arrangement.Levels) Project(level, splitTemplates);
        DropSplitOnlyTemplates(splitTemplates);
        DropTemplatesWithoutStockColumn();
        if (_sixStringOnly) StripExtraColumns();
        if (sixStringOnly) DropExtraStrings();
    }

    /// <summary>The package-order copy the SNG mapper writes.</summary>
    public Arrangement Arrangement { get; }

    /// <summary>The string count of the SOURCE chart (the copy has six in six-string mode).</summary>
    public int StringCount { get; }

    /// <summary>What the six-string copy dropped; null for every other projection.</summary>
    public SixStringLoss? Loss { get; private set; }

    public static Rs7Projection? TryCreate(Arrangement source, bool sixStringOnly = false) =>
        Rs7StringLayout.IsExtendedGuitar(source) ? new Rs7Projection(source, sixStringOnly) : null;

    /// <summary>The copy the SNG mapper writes: <see cref="TryCreate" />'s for a 7-/8-string guitar,
    /// and for a 5-/6-string bass the same copy in package order (low B at string 4, high C at 5,
    /// offsets measured from the package open strings). The XML writer and DLC Builder tuning
    /// vector use the same extended-bass projection.</summary>
    public static Rs7Projection? TryCreateForSng(Arrangement source) =>
        Rs7StringLayout.IsExtended(source) ? new Rs7Projection(source, false) : null;

    /// <summary>A chord name for the shared model: printable ASCII, at most 30 characters.</summary>
    public static string AsciiChordName(string name)
    {
        var ascii = new string([.. name.Where(c => c is >= ' ' and <= '~')]);
        return ascii.Length <= MaxChordNameLength ? ascii : ascii[..MaxChordNameLength];
    }

    private void Renumber(Arrangement source)
    {
        var count = StringCount;
        var offsets = new int[count];
        var names = new string[count];
        for (var d = 0; d < count; d++)
        {
            var s = ToSng(d);
            offsets[s] = Rs7StringLayout.IsExtendedBass(source) ? BassPackageOffset(source, d, s) : TuningOffsetResolution.For(source, d);
            names[s] = d < source.Tuning.Length ? source.Tuning[d] : "";
        }

        Arrangement.TuningPitches = [.. offsets.Select(o => new TuningOffset(o))];
        Arrangement.Tuning = names;

        foreach (var template in Arrangement.ChordTemplates)
        {
            template.Frets = Renumbered(template.Frets);
            template.Fingers = Renumbered(template.Fingers);
            // The -nop suffix folded into its flag first, so the cut never eats it.
            var (name, nop) = RsChordDisplaySuffix.ForExport(template);
            template.Name = AsciiChordName(name);
            template.NopInRs = nop;
        }

        // The XML spells these string-indexed too; the SNG mapper never reads them.
        foreach (var template in Arrangement.FretHandMuteTemplates)
        {
            template.Frets = Renumbered(template.Frets);
            template.Fingers = Renumbered(template.Fingers);
        }

        foreach (var level in Arrangement.Levels)
        {
            foreach (var note in level.Notes) note.StringIndex = ToSng(note.StringIndex);
            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes) chordNote.StringIndex = ToSng(chordNote.StringIndex);
            }
        }
    }

    /// <summary>The offset of an extended bass string against its PACKAGE open string (B0 23, C3 48):
    /// the chart measures from E1 A1 D2 G2 B2 E3 (<see cref="StandardOpenStrings" />), so the
    /// sounding pitch is kept, not the number.</summary>
    private int BassPackageOffset(Arrangement source, int domainIndex, int sngIndex) =>
        StandardOpenStrings.BassLowEMidi + StandardOpenStrings.SemitonesAboveLowE(StringCount, domainIndex) +
        TuningOffsetResolution.For(source, domainIndex) - Rs7StringLayout.BassOpenMidi(sngIndex);

    private Dictionary<int, int> Renumbered(Dictionary<int, int> byString) =>
        byString.ToDictionary(kv => ToSng(kv.Key), kv => kv.Value);

    /// <summary>A string outside the arrangement keeps its index: the validator
    /// reports it as an error, and the mapper's clamp is the last resort.</summary>
    private int ToSng(int domainIndex) =>
        domainIndex >= 0 && domainIndex < StringCount
            ? Rs7StringLayout.ToSng(domainIndex, StringCount, Arrangement.ArrangementType)
            : domainIndex;

    private void Project(ChartLevel level, HashSet<int> splitTemplates)
    {
        // A chord with no chord notes plays its template; spell the template out
        // when it reaches an extra string, or those strings would be cut silently.
        foreach (var chord in level.Chords.Where(c => c.ChordNotes.Count == 0))
        {
            var template = Arrangement.FindChordTemplate(chord.TemplateId);
            if (template == null || !template.Frets.Keys.Any(IsExtra)) continue;
            foreach (var (stringIndex, fret) in template.Frets.OrderBy(kv => kv.Key))
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        }

        var (projectedNotes, projectedChords) = LinkLandingBreakup.Project(level);
        var notes = projectedNotes.ToList();
        var chords = new List<Chord>(projectedChords.Count);
        foreach (var chord in projectedChords)
        {
            var extra = chord.ChordNotes.Where(cn => IsExtra(cn.StringIndex)).ToList();
            if (extra.Count == 0 || (!_sixStringOnly && extra.Count < chord.ChordNotes.Count))
            {
                chords.Add(chord);
                continue;
            }

            if (extra.Count == chord.ChordNotes.Count)
            {
                if (!_sixStringOnly && extra.Count >= 2)
                {
                    chords.Add(chord);
                    continue;
                }

                _droppedChords++;
                notes.AddRange(extra.Select(cn => LinkLandingBreakup.ToLooseNote(chord, cn)));
                if (_sixStringOnly) splitTemplates.Add(chord.TemplateId);
                continue;
            }

            _trimmedChords++;

            // Split: the native columns stay a chord (a single note when one is left).
            var native = chord.ChordNotes.Where(cn => !IsExtra(cn.StringIndex)).ToList();
            if (native.Count == 1)
            {
                notes.Add(LinkLandingBreakup.ToLooseNote(chord, native[0]));
                splitTemplates.Add(chord.TemplateId);
            }
            else
            {
                foreach (var chordNote in extra) chord.ChordNotes.Remove(chordNote);
                chords.Add(chord);
            }

            notes.AddRange(extra.Select(cn => LinkLandingBreakup.ToLooseNote(chord, cn)));
        }

        level.Notes = [.. notes.OrderBy(n => n.Position).ThenBy(n => n.StringIndex)];
        level.Chords = [.. chords];
    }

    /// <summary>
    ///     Six-string copy: a template that only chords now shipped as single notes played
    ///     is left out of the SNG, as <c>rs7chart</c> leaves it out and as the export
    ///     already drops a template only "split notes" chords play
    ///     (<see cref="RsChordTemplateExport" />). One a remaining chord or a hand shape
    ///     names stays. This is the export copy; the chart keeps its templates.
    /// </summary>
    private void DropSplitOnlyTemplates(HashSet<int> splitTemplates)
    {
        if (splitTemplates.Count == 0) return;
        foreach (var level in Arrangement.Levels)
        {
            foreach (var chord in level.Chords) splitTemplates.Remove(chord.TemplateId);
            foreach (var shape in level.HandShapes) splitTemplates.Remove(shape.TemplateId);
        }

        foreach (var template in Arrangement.ChordTemplates.Where(t => splitTemplates.Contains(t.Id)).ToList())
            Arrangement.ChordTemplates.Remove(template);
    }

    /// <summary>
    ///     A template on the extra strings only stays a chord template when it holds two
    ///     or more columns (projection mode). One that cannot be a chord (a single extra
    ///     column, or any extra-only template in the six-string copy) is left out with the hand
    ///     shapes naming it; a chord that still points at it has six-string notes, and
    ///     the export synthesizes its template from those notes.
    /// </summary>
    private void DropTemplatesWithoutStockColumn()
    {
        var dropped = Arrangement.ChordTemplates.Where(t =>
            t.Frets.Count > 0 && t.Frets.Keys.All(IsExtra) && (_sixStringOnly || t.Frets.Count < 2)).ToList();
        if (dropped.Count == 0) return;
        var ids = dropped.Select(t => t.Id).ToHashSet();
        foreach (var template in dropped) Arrangement.ChordTemplates.Remove(template);
        foreach (var level in Arrangement.Levels)
        {
            foreach (var shape in level.HandShapes.Where(h => ids.Contains(h.TemplateId)).ToList())
                level.HandShapes.Remove(shape);
        }
    }

    /// <summary>Six-string copy: no chord plays an extra string any more (they all became
    /// single notes), so no template may claim one — the internal model's template rows stays
    /// -1.</summary>
    private void StripExtraColumns()
    {
        var templates = _sixStringOnly
            ? Arrangement.ChordTemplates.Concat(Arrangement.FretHandMuteTemplates)
            : Arrangement.ChordTemplates;
        foreach (var template in templates)
        {
            foreach (var s in template.Frets.Keys.Where(IsExtra).ToList()) template.Frets.Remove(s);
            foreach (var s in template.Fingers.Keys.Where(IsExtra).ToList()) template.Fingers.Remove(s);
        }
    }

    /// <summary>
    /// The six-string copy: the extra strings' notes (loose ones and the ones
    ///     the split above turned out of a chord) are gone and the arrangement is six
    ///     strings wide with the six stock tuning entries. Chords that sounded only on
    ///     extra strings were counted as dropped chords, ones that lost some strings as
    ///     trimmed — counted before the split flattened them to notes.
    /// </summary>
    private void DropExtraStrings()
    {
        var droppedNotes = 0;
        foreach (var level in Arrangement.Levels)
        {
            var kept = level.Notes.Where(n => !IsExtra(n.StringIndex)).ToList();
            droppedNotes += level.Notes.Count - kept.Count;
            level.Notes = [.. kept];
        }

        Loss = new SixStringLoss(droppedNotes, _droppedChords, _trimmedChords);
        Arrangement.StringCount = Rs7StringLayout.NativeStrings;
        Arrangement.Tuning = [.. Arrangement.Tuning.Take(Rs7StringLayout.NativeStrings)];
        Arrangement.TuningPitches = [.. Arrangement.TuningPitches.Take(Rs7StringLayout.NativeStrings)];
    }

    /// <summary>An "extra" string: past the sixth. A bass has none (its low B and high C are stock slots 4/5).</summary>
    private static bool IsExtra(int sngIndex) => sngIndex >= Rs7StringLayout.NativeStrings;
}
