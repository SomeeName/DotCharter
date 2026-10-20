namespace DotCharter.Core.Domain.Project;

/// <summary>
/// Sidecar of technique/path flags that mirror rscharter's
/// <c>ArrangementProperties</c> (Rocksmith RS-XML <c>arrangementProperties</c>
/// element). Each flag is serialized as a 0/1 integer attribute.
///
/// <para>
/// <c>represent</c>, <c>bonusArr</c> and the three <c>path*</c> flags are
/// mirrors of <see cref="Arrangement.Subtype"/> and <see cref="Arrangement.ArrangementType"/>
/// — they are kept here verbatim so RS-XML round-trips bit-for-bit, but the
/// <see cref="Arrangement"/> properties remain the source of truth in the
/// editor.
/// </para>
/// <para>
/// <b>What an export ships is NOT this object.</b> Every exporter resolves it
/// through <see cref="DotCharter.Core.Utilities.ArrangementPropertiesDeriver.Derive"/>
/// first, which recomputes the technique flags from the chart's content unless
/// <see cref="ManualOverride"/> pins them. What is stored here is the charter's
/// pinned set (or whatever an import carried in) — see the deriver for the
/// authored / derived / undecidable split.
/// </para>
/// </summary>
public class ArrangementProperties
{
    /// <summary>
    ///     When true the charter has PINNED the derivable technique flags on this
    ///     object and no export may recompute them. Default false = derive.
    ///     <para>
    ///     This is the provenance discriminator for the derive/author collision, the
    ///     same role <c>GuitarNote.IgnoreDerived</c> plays for <c>Ignore</c>, and the
    ///     same shape as DLC Builder's <c>ArrangementProperties: ArrPropFlags option</c>
    ///     (<c>None</c> = pass through, <c>Some</c> = a full overwrite seeded from the
    ///     current values). It does NOT cover <c>standardTuning</c>, the <c>path*</c>
    ///     flags, <c>represent</c>/<c>bonusArr</c> or <c>bassPick</c> — each of those
    ///     mirrors a first-class field, so pinning it could only produce a
    ///     disagreement with the field it summarizes.
    ///     </para>
    ///     Serialized as the additive rscp attribute <c>manual="T"</c> on
    ///     <c>dcArrangementProperties</c>, absent when false.
    /// </summary>
    public bool ManualOverride { get; set; }

    public int Represent { get; set; } = 1;
    public int BonusArr { get; set; }
    public int StandardTuning { get; set; }
    public int NonStandardChords { get; set; }
    public int BarreChords { get; set; }
    public int PowerChords { get; set; }
    public int DropDPower { get; set; }
    public int OpenChords { get; set; }
    public int FingerPicking { get; set; }
    public int PickDirection { get; set; }
    public int DoubleStops { get; set; }
    public int PalmMutes { get; set; }
    public int Harmonics { get; set; }
    public int PinchHarmonics { get; set; }
    public int Hopo { get; set; }
    public int Tremolo { get; set; }
    public int Slides { get; set; }
    public int UnpitchedSlides { get; set; }
    public int Bends { get; set; }
    public int Tapping { get; set; }
    public int Vibrato { get; set; }
    public int FretHandMutes { get; set; }
    public int SlapPop { get; set; }
    public int TwoFingerPicking { get; set; }
    public int FifthsAndOctaves { get; set; }
    public int Syncopation { get; set; }
    public int BassPick { get; set; }
    public int Sustain { get; set; }
    public int PathLead { get; set; }
    public int PathRhythm { get; set; }
    public int PathBass { get; set; }

    public ArrangementProperties Clone() => (ArrangementProperties)MemberwiseClone();
}
