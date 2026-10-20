// RsToneConverter.GameCurveChorus: the chorus gear mapped through their soundbanks' Wwise Flanger curves (RsGameChorusCurves) instead of a reading of the knob labels.
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigTargetCatalog;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

public sealed partial class RsToneConverter
{
    /// <summary>The <c>chorus</c> module's fixed base delay (voice 0, <c>dcdsp_chorus.h</c> kBaseMs).</summary>
    private const float ChorusBaseDelayMs = 12f;

    /// <summary>
    ///     A chorus gear through its bank (<see cref="RsFlangerChorus" />): Rate through the
    ///     bank's rate curve (the "Hz" label is not what the game applies), Depth as a
    ///     share of the bank's delay (peak deviation, <see cref="RsFlangerChorus.PeakDeviationMs" />),
    ///     Mix as the bank's wet/dry ratio in a crossfade, one voice per Flanger object
    ///     and Stereo as the phase spread. Spreading Depth 1..100 % over the
    ///     module's whole 1..15 ms and passing Mix through 1:1 would make a Rocksmith
    ///     chorus several times deeper and wetter than the game plays it (seen on
    ///     Polkadot Stingray "Killer Record", tone "Chorus").
    /// </summary>
    private static DcRigModule BuildFlangerChorus(RsGear gear, RsFlangerChorus bank, RsGearCatalog catalog, ConversionReport report)
    {
        var p = new ParamMap(Chorus, null, gear.GearKey);
        var delayMs = bank.DelayKnobMs is { } delayKnob ? delayKnob.Evaluate(KnobOrDefault(gear, catalog, "Delay")) : bank.DelayMs;

        p.Set("rateHz", bank.RateHz.Evaluate(KnobOrDefault(gear, catalog, "Rate")));
        p.Set("depthMs", RsFlangerChorus.PeakDeviationMs(bank.DepthPercent.Evaluate(KnobOrDefault(gear, catalog, "Depth")), delayMs));
        p.Set("mix", bank.CrossfadeWet(bank.MixPercent.Evaluate(KnobOrDefault(gear, catalog, "Mix"))));
        p.Set("voices", bank.Voices);
        if (bank.StereoDegrees is { } stereo)
            p.Set("spread", stereo.Evaluate(KnobOrDefault(gear, catalog, "Stereo")) / 180f);
        if (gear.TryGetKnob("LoFilter", out var loFilter))
        {
            catalog.TryGetKnob(gear.GearKey, "LoFilter", out var knob);
            p.Set("wetHpfHz", NaturalScaled(loFilter, knob, 20f, 400f));
        }

        if (MathF.Abs(delayMs - ChorusBaseDelayMs) > 1f)
        {
            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                $"the game's {delayMs:0.#} ms chorus delay rides the chorus module's fixed {ChorusBaseDelayMs:0} ms base; depth, rate and mix follow the game");
        }

        if (bank.GearKey == "Pedal_Chorus")
        {
            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                "Depth's S-curve taper is unpublished; it is read as linear, exact at both ends and the middle");
        }

        return new DcRigModule(Chorus, p.Build());
    }
}
