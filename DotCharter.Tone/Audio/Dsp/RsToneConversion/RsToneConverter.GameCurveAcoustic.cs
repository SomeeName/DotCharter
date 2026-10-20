// RsToneConverter.GameCurveAcoustic: Pedal_AcousticEmulator's Mid-range / Mid Frequency band through the game's own bank.
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigTargetCatalog;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

public sealed partial class RsToneConverter
{
    // pedal_acousticemulator.bnk (read from the player's install, RsAcousticCurves): the
    // chain is Guitar Distortion -> Matrix Reverb -> Guitar Distortion -> Compressor, and
    // the second Guitar Distortion (distortion type 0: none) is a six-band EQ. Mid-range
    // drives its pre-EQ band 0's GAIN (param 1) and Mid Frequency its FREQUENCY (param 2,
    // log10-Hz scaled); the band is taken at Q 1.

    // The svf's resonance map is Q = 0.707 * 28^res (dcdsp_svf.h): Q 1 is res = log2(1/0.707) / log2(28).
    private static readonly float SvfResonanceForQ1 = (float)(Math.Log2(1.0 / 0.70710678) / Math.Log2(28.0));

    /// <summary>
    ///     The Acoustic Emulator's mid band: the
    ///     game's cut, deepest at the bottom of Mid-range, at Mid Frequency, Q 1. The
    ///     `eq` bell stops at −18 dB, so the band rides a NOTCH `svf` blended with the dry
    ///     signal: <c>mix = 1 − 10^(dB/20)</c> leaves the centre exactly that deep. The
    ///     skirt of a blended notch is not a peaking EQ's, which the report says.
    ///     Body and Tone stay on `acousticsim`.
    /// </summary>
    private static DcRigModule BuildAcousticEmulatorMid(RsGear gear, RsGearCatalog catalog, ConversionReport report, RsAcousticCurves curves)
    {
        var depthDb = curves.MidGainDb.Evaluate(KnobOrDefault(gear, catalog, "Mid"));
        var p = new ParamMap(Svf, null, gear.GearKey);
        p.Set("mode", 3f);   // notch
        p.Set("cutoffHz", curves.MidFreqHz.Evaluate(KnobOrDefault(gear, catalog, "MidShift")));
        p.Set("resonance", SvfResonanceForQ1);
        p.Set("drive", 0f);
        p.Set("source", 0f); // manual: no LFO / envelope sweep
        p.Set("mix", 1f - MathF.Pow(10f, depthDb / 20f));
        report.Add(ConversionNoteKind.Approximated, gear.GearKey,
            "acoustic emulator: the game's mid cut is a notch blended with dry — exact depth at the mid frequency, approximate skirt");
        return new DcRigModule(Svf, p.Build());
    }
}
