using BendValue = DotCharter.Core.Domain.Guitar.BendValue;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
/// Stateless helpers shared by the GP3/4/5 binary importer. Extracted to keep
/// <see cref="GpLegacyImporter"/> focused on stream traversal, and to allow
/// the bug-prone version-conditional slide / harmonic / bend-materialisation
/// logic to be unit-tested without spinning up a full parse.
/// </summary>
internal static class GpLegacyNoteEffectsHelpers
{
    /// <summary>
    /// Translates a Guitar Pro slide value into <see cref="GuitarNote"/> fields.
    /// The on-wire encoding is completely different between GP3/4 and GP5, and
    /// the value is SIGNED in both (the caller reads it with
    /// <c>GpLegacyBinaryReader.ReadSByte</c>):
    /// <list type="bullet">
    /// <item>GP3/4 — one discrete value: 1 = shift (out, with pluck), 2 = legato
    /// (out, link-next), 3 = out down 5 frets, 4 = out up 5 frets, <b>-1 = slide
    /// IN from below, -2 = slide IN from above</b>. The two negative cases are
    /// the ones this reader must not drop on the floor; they are read exactly
    /// this way by Log Charter's <c>GP5FileReader.readSlide()</c> (the pre-500
    /// branch) and are the pre-GP5 spelling of GP5's 0x10 / 0x20 bits.</item>
    /// <item>GP5 — a bitmask: 0x01 shift, 0x02 legato, 0x04 out down 5fr,
    /// 0x08 out up 5fr, 0x10 slide IN from below, 0x20 slide IN from above,
    /// <b>0x40 / 0x80 pick scrape down / up</b> (<see cref="GpPickScrape"/>; it
    /// outranks every other bit, exactly as on the gpif path).</item>
    /// </list>
    /// A slide-in parks the <see cref="GpNoteParser.SlideInFromBelow"/> /
    /// <see cref="GpNoteParser.SlideInFromAbove"/> sentinel on the note for
    /// <see cref="GpPostProcessor.MaterializeSlideIns"/> to turn into a real
    /// lead-in note.
    /// <para>
    /// <b>Out and in are independent slots in the FORMAT but not in our model.</b>
    /// Log Charter reads them into a two-element array (<c>types[0]</c> out,
    /// <c>types[1]</c> in) because GP5 can flag both on one note. Our
    /// <see cref="GuitarNote.SlideTo"/> is a single <c>int?</c> and there is no
    /// second slide field, so exactly ONE of the two can survive: the slide OUT
    /// wins, and a slide-in only claims a note no other slide already claimed.
    /// That is not an accident of this reader — it is the identical precedence
    /// the GPIF path applies in <c>GpNoteParser.ApplySlideFlags</c>, and the GPIF
    /// path is the one measured to match EOF and Log Charter note-for-note on the
    /// cross-tool reference file. Letting the in-slide win here (Log Charter's
    /// own downstream choice, <c>GP5SoundsTransformer.addSlideIn</c>) would make
    /// the two DotCharter importers disagree about the same score. Keep them
    /// identical; if the model ever grows a real second slide slot, change both.
    /// </para>
    /// </summary>
    /// <param name="slideFlag">The raw signed slide value read from the note's
    /// effect block (a bitmask in GP5, one discrete value in GP3/4).</param>
    /// <param name="note">The note the slide is applied to; its
    /// <see cref="GuitarNote.Fret"/> must already be set (the unpitched
    /// out-slides are relative to it).</param>
    /// <param name="isVersion5">True for GP5 files (bitmask encoding), false for
    /// GP3/GP4 (discrete-value encoding).</param>
    /// <param name="capo">The track's capo. The unpitched out-slide's landing is
    /// a SYNTHESIZED absolute fret, so it floors at
    /// <see cref="GpCapoFrets.MinFrettedFret"/> rather than at a hard <c>1</c> —
    /// the identical rule the gpif twin applies, and a fret below the capo is an
    /// export-blocking <c>KeyNoteBelowCapo</c>. Default <c>0</c> ⇒ the historical
    /// fret-1 floor.</param>
    /// <returns><c>true</c> when the value was a GP5 pick scrape — the caller
    /// must then not let a later palm-mute bit override the scrape's fret-hand
    /// mute.</returns>
    public static bool ApplySlide(int slideFlag, GuitarNote note, bool isVersion5, int capo = 0)
    {
        var minFret = GpCapoFrets.MinFrettedFret(capo);
        if (!isVersion5)
        {
            // A slide OUT of an open string is dropped, as GpNoteParser.ApplySlideFlags
            // drops it (L09, SlideFromOpenString); the note itself stays.
            if (note.Fret <= 0 && slideFlag is >= 1 and <= 4) return false;
            switch (slideFlag)
            {
                case 1: note.SlideTo = -1; break;
                case 2: note.SlideTo = -1; note.LinkNext = true; break;
                case 3:
                    note.UnpitchedSlide = true;
                    note.SlideTo = Math.Max(minFret, note.Fret - 5);
                    break;
                case 4:
                    note.UnpitchedSlide = true;
                    note.SlideTo = Math.Min(GpCapoFrets.MaxAbsoluteFret, note.Fret + 5);
                    break;
                // GP3/4 spell the two slide-INs as negative values. They are
                // mutually exclusive with the out-slides above (one value, not a
                // mask), so no precedence question arises on this branch.
                case -1: note.SlideTo = GpNoteParser.SlideInFromBelow; break;
                case -2: note.SlideTo = GpNoteParser.SlideInFromAbove; break;
            }
        }
        else
        {
            if (GpPickScrape.IsPickScrape(slideFlag))
            {
                GpPickScrape.Apply(note, slideFlag, capo);
                return true;
            }

            // A slide OUT of an open string is dropped, exactly as the gpif path does
            // (GpNoteParser.ApplySlideFlags): it is L09 (SlideFromOpenString) on every
            // save. Keeping it here made a GP5 import and the same score
            // round-tripped through Guitar Pro 7 disagree on every such note.
            if (note.Fret <= 0) slideFlag &= ~0x0F;

            if ((slideFlag & 0x01) != 0) note.SlideTo = -1;
            if ((slideFlag & 0x02) != 0)
            {
                note.SlideTo = -1;
                note.LinkNext = true;
            }

            if ((slideFlag & 0x04) != 0)
            {
                note.UnpitchedSlide = true;
                note.SlideTo = Math.Max(minFret, note.Fret - 5);
            }

            if ((slideFlag & 0x08) != 0)
            {
                note.UnpitchedSlide = true;
                note.SlideTo = Math.Min(GpCapoFrets.MaxAbsoluteFret, note.Fret + 5);
            }

            // Out wins over in — see the remarks above. One int? cannot hold both.
            if (note.SlideTo.HasValue) return false;
            if ((slideFlag & 0x10) != 0) note.SlideTo = GpNoteParser.SlideInFromBelow;
            else if ((slideFlag & 0x20) != 0) note.SlideTo = GpNoteParser.SlideInFromAbove;
        }

        return false;
    }

    /// <summary>
    /// Reads a harmonic-effect chunk and applies it to <paramref name="note"/>.
    /// Type 0 = none, type 2 = artificial (3 trailing bytes), type 3 = tapped
    /// (1 trailing byte). All non-zero types produce a Normal harmonic — the
    /// chart format only distinguishes presence/absence.
    /// </summary>
    public static void ReadHarmonic(GpLegacyBinaryReader reader, GuitarNote note)
    {
        var type = reader.ReadByte();
        note.Harmonic = type == 0 ? HarmonicType.None : HarmonicType.Normal;
        if (type == 2) reader.Skip(3);
        else if (type == 3) reader.ReadByte();
    }

    /// <summary>
    /// Converts pending GP-format bend points (offset 0..60 along a beat,
    /// step in tenths-of-a-tone-times-five) into absolute <see cref="BendValue"/>
    /// entries on <paramref name="target"/>. <paramref name="sustainFraction"/>
    /// is the beat's note value as a fraction of a whole note.
    /// </summary>
    public static void MaterializeBends(IGuitarSound target,
        IReadOnlyList<(int Offset, double Step)> points,
        FractionalPosition absolutePos,
        Fraction sustainFraction)
    {
        if (points.Count == 0) return;
        foreach (var pt in points)
        {
            var offset = Math.Clamp(pt.Offset, 0, 60);
            var bpFrac = sustainFraction * new Fraction(offset, 60);
            var beats = bpFrac.Numerator / bpFrac.Denominator;
            var rem = new Fraction(bpFrac.Numerator % bpFrac.Denominator, bpFrac.Denominator);
            var bp = absolutePos + new FractionalPosition(beats, rem);
            target.BendValues.Add(new BendValue(bp, pt.Step));
        }
        GpBendUtilities.SortUniqueBends(target.BendValues);
    }

    /// <summary>
    ///     Returns the largest power of two not greater than <paramref name="n" />.
    ///     For rounding irregular GP measure subdivisions down to a binary
    ///     denominator for fractional-position math.
    ///     <para>
    ///         The 2^30 ceiling is not defensive tidying — without it this HUNG.
    ///         <c>n</c> arrives as a raw <c>ReadInt32</c> from the file
    ///         (<c>GpLegacyImporter</c>'s <c>nTuplet</c>), and <c>p * 2</c> is
    ///         unchecked: at <c>p == 2^30</c> the doubling wraps to
    ///         <see cref="int.MinValue" /> (still <c>&lt;= n</c>), then to <c>0</c>
    ///         (still <c>&lt;= n</c>), and <c>0 * 2 == 0</c> forever. One
    ///         four-byte field at or above <c>0x40000000</c> pinned the import
    ///         thread at 100% CPU with no exception and no timeout. The answer is
    ///         also the correct one: 2^30 IS the largest power of two that fits an
    ///         <see cref="int" />.
    ///     </para>
    /// </summary>
    public static int PreviousPowerOfTwo(int n)
    {
        if (n < 1) return 1;
        if (n >= 1 << 30) return 1 << 30;
        var p = 1;
        while (p * 2 <= n) p *= 2;
        return p;
    }
}
