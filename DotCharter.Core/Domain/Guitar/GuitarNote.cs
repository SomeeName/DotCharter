using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Guitar;

public class GuitarNote : IGuitarSound, ITimedElement
{
    public bool Accent { get; set; }
    public BassPickingType BassPicking { get; set; } = BassPickingType.None;
    public bool Ignore { get; set; }

    /// <summary>
    ///     True when <see cref="Ignore" /> was DERIVED by
    ///     <c>ChartSpacingEnforcer</c> (an unplayable fret, a harmonic whose
    ///     sustain a later pass zeroed) rather than set by the user.
    ///     <para>
    ///         The enforcer runs on every save and must clear a flag whose
    ///         reason has gone — a note dragged off fret 24 stayed unscored
    ///         forever. But <see cref="Ignore" /> is ALSO user-authorable (the
    ///         inspector chip and the highway toggle), and an unconditional
    ///         re-derivation silently discarded that. This is the discriminator:
    ///         the enforcer only ever clears what it set.
    ///     </para>
    /// </summary>
    public bool IgnoreDerived { get; set; }
    public bool PassOtherNotes { get; set; }
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;

    public FractionalPosition EndPosition => Position + Sustain;
    public int Fret { get; set; }
    public HarmonicType Harmonic { get; set; } = HarmonicType.None;
    public HopoType Hopo { get; set; } = HopoType.None;
    public bool LinkNext { get; set; }
    public MuteType Mute { get; set; } = MuteType.None;
    public int? SlideTo { get; set; }
    public int StringIndex { get; set; }
    public FractionalPosition Sustain { get; set; } = FractionalPosition.Zero;

    /// <summary>
    /// Optional AUTHORED notation duration in quarter-note units (1/1 = quarter,
    /// 1/2 = eighth, 1/3 = eighth triplet, 3/2 = dotted quarter, …), captured by
    /// score importers (Guitar Pro, Songsterr) before Rocksmith-style sustain
    /// post-processing discards it. Null = no authored rhythm — notation derives
    /// the duration from sustain/onset gaps. Never affects gameplay.
    /// </summary>
    public Fraction? NotationDuration { get; set; }

    /// <summary>
    /// Optional BEFORE-BEAT grace-note hint: the fret of a small ornamental
    /// note (on the SAME string) played just ahead of this note, captured from
    /// score imports (Guitar Pro <c>GraceNotes</c> beats). Notation-only — the
    /// sheet renders it as a grace glyph; gameplay ignores it. Null = no grace.
    /// Serialized as the backward-tolerant rscp attribute <c>gf</c>.
    /// </summary>
    public int? GraceFret { get; set; }

    /// <summary>
    /// Import-transient: the <see cref="GraceFret"/> ornament SLIDES into this
    /// note (GP "sl." grace). Consumed during import by
    /// <c>GpPostProcessor.MaterializeGraceSlides</c>, which turns it into a real
    /// short note that slides into the target (matching EOF / Log Charter) and
    /// clears both this flag and <see cref="GraceFret"/> — so it is never
    /// persisted true and never reaches the sheet as a bare hint.
    /// </summary>
    public bool GraceSlide { get; set; }

    /// <summary>
    /// Left-hand finger that frets this note (RS XML <c>leftHand</c>): -1 = not
    /// stated, 0 = thumb, 1-4 = index to pinky. Chord notes take theirs from
    /// the chord template, so this is a single-note field.
    /// </summary>
    public int LeftHandFinger { get; set; } = -1;

    /// <summary>Pick direction of this note (RS XML <c>pickDirection</c>).
    /// <see cref="PickDirectionType.None" /> = not stated.</summary>
    public PickDirectionType PickDirection { get; set; } = PickDirectionType.None;

    public bool Tremolo { get; set; }
    public bool UnpitchedSlide { get; set; }
    public bool Vibrato { get; set; }
    public int VibratoStrength { get; set; }

    public ObservableCollection<BendValue> BendValues { get; set; } = [];

    public double GetTimeMs(IList<BeatMarker> beats) => Position.GetPositionMs(beats);

    public GuitarNote Clone()
    {
        var clone = new GuitarNote();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(GuitarNote other)
    {
        Position = other.Position;
        Sustain = other.Sustain;
        NotationDuration = other.NotationDuration;
        GraceFret = other.GraceFret;
        GraceSlide = other.GraceSlide;
        LeftHandFinger = other.LeftHandFinger;
        PickDirection = other.PickDirection;
        StringIndex = other.StringIndex;
        Fret = other.Fret;
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
        Accent = other.Accent;
        Ignore = other.Ignore;
        IgnoreDerived = other.IgnoreDerived;
        PassOtherNotes = other.PassOtherNotes;

        BendValues.Clear();
        foreach (var b in other.BendValues) BendValues.Add(b.Clone());
    }
}
