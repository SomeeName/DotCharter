using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Guitar;

public class ChordNote : IGuitarSound
{
    public bool Accent { get; set; }
    public BassPickingType BassPicking { get; set; } = BassPickingType.None;
    public int Fret { get; set; }

    /// <summary>
    /// Optional BEFORE-BEAT grace-note hint for THIS string of the strum: the
    /// fret of a small ornamental note (same string) played just ahead of the
    /// chord, captured from score imports — Guitar Pro graces a chord by putting
    /// one grace note per string on the grace beat. Notation-only: the sheet
    /// renders it as a grace glyph, gameplay ignores it. Null = no grace.
    /// Serialized as the backward-tolerant rscp attribute <c>gf</c> on
    /// <c>chordNote</c>, exactly like <see cref="GuitarNote.GraceFret"/>.
    /// (A grace that SLIDES into the chord is not a hint — the importer
    /// materializes it as a real origin chord; see <c>GpArrangementParser</c>.)
    /// </summary>
    public int? GraceFret { get; set; }

    public HarmonicType Harmonic { get; set; } = HarmonicType.None;
    public HopoType Hopo { get; set; } = HopoType.None;
    public bool LinkNext { get; set; }
    public MuteType Mute { get; set; } = MuteType.None;
    public int? SlideTo { get; set; }
    public int StringIndex { get; set; }
    public FractionalPosition Sustain { get; set; } = FractionalPosition.Zero;

    /// <summary>
    ///     This string's sustain was AUTHORED as a Rocksmith chord-note sustain,
    ///     rather than merely inherited from the strum's length. It is the port of
    ///     EOF's <c>EOF_PRO_GUITAR_NOTE_EFLAG_SUSTAIN</c> (EOF_SRC/song.h:131 — "a
    ///     note that will export to RS2 format with its sustain even when it's a
    ///     chord without techniques that normally require chordNote tags"), an
    ///     explicit per-note status EOF exposes as the note dialog's "Sustain"
    ///     checkbox and sets on import from RS XML.
    ///     <para>
    ///         <b>It exists only to hold a repeated chord at its FULL box.</b>
    ///         Rocksmith draws the second and later repeats of a chord inside one
    ///         hand shape as a compact placeholder with no chord name
    ///         (<c>highDensity="1"</c>); a chord carrying this flag on any string
    ///         keeps the full box and the name instead. The sustain itself is
    ///         written to the XML either way, so nothing sounds different — this is
    ///         purely which box the game draws.
    ///     </para>
    ///     <para>
    ///         <b>Do not infer it from <see cref="Sustain" /> being non-zero.</b>
    ///         Every chord a charter draws with any length has a sustain, so reading
    ///         the flag off the length makes EVERY repeated chord low density and no
    ///         chart ever compacts — which is exactly what DotCharter shipped up to
    ///         v0.48.0 (a real GP import measured 81 chords, 35 hand shapes,
    ///         highDensity=0).
    ///     </para>
    /// </summary>
    public bool ExplicitSustain { get; set; }
    public bool Tremolo { get; set; }
    public bool UnpitchedSlide { get; set; }
    public bool Vibrato { get; set; }
    public int VibratoStrength { get; set; }
    public bool Ignore { get; set; }

    public ObservableCollection<BendValue> BendValues { get; set; } = [];

    public ChordNote Clone()
    {
        var clone = new ChordNote();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(ChordNote other)
    {
        Accent = other.Accent;
        StringIndex = other.StringIndex;
        Fret = other.Fret;
        GraceFret = other.GraceFret;
        Sustain = other.Sustain;
        ExplicitSustain = other.ExplicitSustain;
        BassPicking = other.BassPicking;
        Mute = other.Mute;
        Hopo = other.Hopo;
        Harmonic = other.Harmonic;
        Vibrato = other.Vibrato;
        VibratoStrength = other.VibratoStrength;
        Tremolo = other.Tremolo;
        LinkNext = other.LinkNext;
        SlideTo = other.SlideTo;
        UnpitchedSlide = other.UnpitchedSlide;
        Ignore = other.Ignore;

        BendValues.Clear();
        foreach (var b in other.BendValues) BendValues.Add(b.Clone());
    }
}
