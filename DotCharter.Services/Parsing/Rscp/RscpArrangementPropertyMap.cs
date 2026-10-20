namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     The single attribute-name ↔ <see cref="ArrangementProperties" /> flag
///     table, shared by <see cref="RscpWriter" /> and
///     <see cref="RscpArrangementParser" />.
///     <para>
///         It exists as ONE table on purpose. Thirty flags written by one
///         hand-maintained list and read by another is precisely how a field
///         goes quietly missing in one direction — which is the bug this node
///         was added to fix in the first place.
///     </para>
/// </summary>
internal static class RscpArrangementPropertyMap
{
    /// <summary>Attribute name → accessor pair, in RS-XML declaration order.</summary>
    public static readonly (string Name,
        Func<ArrangementProperties, int> Get,
        Action<ArrangementProperties, int> Set)[] Flags =
        [
            ("represent", p => p.Represent, (p, v) => p.Represent = v),
            ("bonusArr", p => p.BonusArr, (p, v) => p.BonusArr = v),
            ("standardTuning", p => p.StandardTuning, (p, v) => p.StandardTuning = v),
            ("nonStandardChords", p => p.NonStandardChords, (p, v) => p.NonStandardChords = v),
            ("barreChords", p => p.BarreChords, (p, v) => p.BarreChords = v),
            ("powerChords", p => p.PowerChords, (p, v) => p.PowerChords = v),
            ("dropDPower", p => p.DropDPower, (p, v) => p.DropDPower = v),
            ("openChords", p => p.OpenChords, (p, v) => p.OpenChords = v),
            ("fingerPicking", p => p.FingerPicking, (p, v) => p.FingerPicking = v),
            ("pickDirection", p => p.PickDirection, (p, v) => p.PickDirection = v),
            ("doubleStops", p => p.DoubleStops, (p, v) => p.DoubleStops = v),
            ("palmMutes", p => p.PalmMutes, (p, v) => p.PalmMutes = v),
            ("harmonics", p => p.Harmonics, (p, v) => p.Harmonics = v),
            ("pinchHarmonics", p => p.PinchHarmonics, (p, v) => p.PinchHarmonics = v),
            ("hopo", p => p.Hopo, (p, v) => p.Hopo = v),
            ("tremolo", p => p.Tremolo, (p, v) => p.Tremolo = v),
            ("slides", p => p.Slides, (p, v) => p.Slides = v),
            ("unpitchedSlides", p => p.UnpitchedSlides, (p, v) => p.UnpitchedSlides = v),
            ("bends", p => p.Bends, (p, v) => p.Bends = v),
            ("tapping", p => p.Tapping, (p, v) => p.Tapping = v),
            ("vibrato", p => p.Vibrato, (p, v) => p.Vibrato = v),
            ("fretHandMutes", p => p.FretHandMutes, (p, v) => p.FretHandMutes = v),
            ("slapPop", p => p.SlapPop, (p, v) => p.SlapPop = v),
            ("twoFingerPicking", p => p.TwoFingerPicking, (p, v) => p.TwoFingerPicking = v),
            ("fifthsAndOctaves", p => p.FifthsAndOctaves, (p, v) => p.FifthsAndOctaves = v),
            ("syncopation", p => p.Syncopation, (p, v) => p.Syncopation = v),
            ("bassPick", p => p.BassPick, (p, v) => p.BassPick = v),
            ("sustain", p => p.Sustain, (p, v) => p.Sustain = v),
            ("pathLead", p => p.PathLead, (p, v) => p.PathLead = v),
            ("pathRhythm", p => p.PathRhythm, (p, v) => p.PathRhythm = v),
            ("pathBass", p => p.PathBass, (p, v) => p.PathBass = v)
        ];
}
