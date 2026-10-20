using System.Globalization;
using System.Text;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
/// Collects the RS7X block (<see cref="RsExtensionBlock" />) while
///     <c>SngFromDomainMapper</c> writes a 7-string guitar: one <c>CHDT</c> row per chord
///     template it emits and one <c>CHNT</c> row per ChordNotes record it ADDS, so both
///     counts equal the stock sections' by construction. The mapper keeps writing the
///     six stock columns exactly as for six strings; this type only ever sees the
///     strings past the sixth.
///     <para>
///         The writer rules this upholds (the mod's <c>docs/sng-extension.md</c> §4):
///         the stock columns stay a six-string projection and no extended pitch lands in a
///         stock MIDI slot (the mapper writes strings 0..5 only); templates are not merged
///         on their stock columns alone (the export emits one per chart template, and a
///         synthesized one is matched on every string); a ChordNotes record is created when
///         ANY string has a mask bit — an OPEN 7th string included, as stock tools write one
///         for an open stock string, because the game draws the open-string gem from that
///         bit (as <c>rs7chart</c> does) — and is
///         deduplicated on stock AND extended data (<see cref="Key" />).
///     </para>
/// </summary>
internal sealed class Rs7BlockBuilder
{
    private readonly RsExtensionBlock _block;

    private Rs7BlockBuilder(int stringCount) => _block = RsExtensionBlock.ForStrings(stringCount);

    /// <summary>A builder for a 7-/8-string guitar; null for everything that stays stock.</summary>
    public static Rs7BlockBuilder? For(Arrangement arrangement) =>
        Rs7StringLayout.IsExtendedGuitar(arrangement) ? new Rs7BlockBuilder(arrangement.StringCount) : null;

    /// <summary>The <c>CHDT</c> row of the next emitted template (package string order).</summary>
    public void AddTemplate(ChordTemplate template) =>
        _block.Templates!.Add([.. _block.Strings.Select(s => Column(template, s))]);

    private static RsExtColumn Column(ChordTemplate template, int sngIndex)
    {
        if (!template.Frets.TryGetValue(sngIndex, out var fret) || fret is < 0 or > RsExtensionBlock.MaxFret)
            return RsExtColumn.None;
        var finger = template.Fingers.TryGetValue(sngIndex, out var f) && f is >= 0 and <= 4 ? f : -1;
        return new RsExtColumn((sbyte)fret, (sbyte)finger);
    }

    /// <summary>A chord note's DOUBLE_STOP bit counted over EVERY sounding column, the
    /// extended ones included: the C#5 of the 7th string + low E is a double stop, and
    /// low B + two stock strings is not (as <c>rs7chart</c> writes it).</summary>
    public uint WithDoubleStop(uint mask, RsChordTemplate? template, int chordId)
    {
        if (template == null || chordId < 0 || chordId >= _block.Templates!.Count) return mask;
        var strings = template.Frets.Count(f => f != -1) + _block.Templates[chordId].Count(c => c.Fret >= 0);
        return strings == 2 ? mask | RsNoteMask.DoubleStop : mask & ~RsNoteMask.DoubleStop;
    }

    /// <summary>An empty <c>CHNT</c> row for one chord.</summary>
    public RsExtTech[] NewChordNotesRow() => [.. _block.Strings.Select(_ => RsExtTech.Empty)];

    /// <summary>Puts one extended string's chord-note data into <paramref name="row" />.
    /// True when it needs a ChordNotes record: any mask bit, OPEN included (the same rule
    /// as a stock string).</summary>
    public bool Set(RsExtTech[] row, int sngIndex, uint mask, sbyte slideTo, sbyte slideUnpitchTo, short vibrato,
        IReadOnlyList<RsBendValue> bends)
    {
        var column = _block.ColumnOf(sngIndex);
        if (column < 0) return false;
        row[column] = new RsExtTech(mask, slideTo, slideUnpitchTo, vibrato, [.. bends.Take(RsExtensionBlock.MaxBends)]);
        return mask != 0;
    }

    /// <summary>The extended half of a ChordNotes record's dedupe key ("" for none).</summary>
    public static string Key(RsExtTech[]? row)
    {
        if (row == null) return "";
        var key = new StringBuilder("|rs7x");
        foreach (var tech in row)
        {
            key.Append('|').Append(tech.Mask).Append(',').Append(tech.SlideTo).Append(',')
                .Append(tech.SlideUnpitchTo).Append(',').Append(tech.Vibrato);
            foreach (var bend in tech.Bends)
            {
                key.Append(';').Append(bend.Time.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(bend.Step.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        return key.ToString();
    }

    /// <summary>The <c>CHNT</c> row of the ChordNotes record just added.</summary>
    public void AddChordNotes(RsExtTech[]? row) => _block.ChordNotes!.Add(row ?? NewChordNotesRow());

    public RsExtensionBlock Build() => _block;
}
