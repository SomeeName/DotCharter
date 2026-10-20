// RsToneConverter.GameCurveGear: the gear mapped through the game's own knob curves (RsGameCurves, read from the player's install) — Rack_StudioEQ, Pedal_Compression, Pedal_LoFiFilter, Pedal_BassEmulator — and the second node a gear may add after its first (BuildCompanion).
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigTargetCatalog;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

public sealed partial class RsToneConverter
{
    /// <summary>
    ///     The node a gear adds right after its first one, or null. One gear, two
    ///     nodes, where one of our modules has half of what the gear does:
    ///     <list type="bullet">
    ///         <item><c>eq</c>: a four-band parametric's second bell (<see cref="BuildHiMidBand" />);</item>
    ///         <item><c>svf</c> from <c>Pedal_LoFiFilter</c>: the high-cut half (the first node is the low cut);</item>
    ///         <item><c>harmonizer</c> from <c>Pedal_BassEmulator</c>: the octave voice's filter.</item>
    ///     </list>
    /// </summary>
    private static DcRigModule? BuildCompanion(RsGear gear, DcRigModule first, RsGearCatalog catalog, ConversionReport report)
        => first.ModuleTypeId switch
        {
            Eq => BuildHiMidBand(gear, catalog, report),
            Svf when gear.GearKey == RsGameCurves.LoFiFilterKey && RsGameCurves.Current.LoFi is { } lofi => BuildLoFiHighCut(gear, catalog, lofi),
            Harmonizer when gear.GearKey == RsGameCurves.BassEmulatorKey && RsGameCurves.Current.BassEmulator is { } be
                => BuildBassEmulatorFilter(gear, catalog, be),
            AcousticSim when gear.GearKey == RsGameCurves.AcousticEmulatorKey && RsGameCurves.Current.Acoustic is { } ac
                => BuildAcousticEmulatorMid(gear, catalog, report, ac),
            _ => null
        };

    /// <summary>
    ///     The second bell of a four-band parametric (<c>Rack_StudioEQ</c>: Bass,
    ///     Low-Mid, High-Mid, Treble): an <c>eq</c> whose mid band is the HiMid and
    ///     whose shelves stay at 0 dB, which the core skips outright. Null unless
    ///     the gear has BOTH a low/plain mid and a HiMid gain — with no other mid,
    ///     <see cref="BuildFilter" /> puts the HiMid on the first node's bell.
    /// </summary>
    private static DcRigModule? BuildHiMidBand(RsGear gear, RsGearCatalog catalog, ConversionReport report)
    {
        if (!HasAny(gear, "HiMid") || !HasAny(gear, "LoMid", "Mid", "Middle")) return null;
        if (gear.GearKey == RsGameCurves.StudioEqKey && RsGameCurves.Current.StudioEq is { } eq) return BuildStudioEqHiMid(gear, catalog, eq);

        var p = new ParamMap(Eq, null, gear.GearKey);
        SetBandGain(gear, catalog, p, "midGainDb", "HiMid");
        SetBandFreq(gear, catalog, p, report, "midFreq", "HiMidFreq");
        SetBandQ(gear, catalog, p, "midQ", "HiMidQ");
        p.Set("lowGainDb", 0f);
        p.Set("highGainDb", 0f);
        return new DcRigModule(Eq, p.Build());
    }

    /// <summary>The knob's value in the tone, else its catalogue default: a tone the
    /// game writes carries every knob, a hand-made one may not, and the game's
    /// default is what the game would play.</summary>
    private static float KnobOrDefault(RsGear gear, RsGearCatalog catalog, string pot)
    {
        if (gear.TryGetKnob(pot, out var value)) return value;
        return catalog.TryGetKnob(gear.GearKey, pot, out var knob) ? knob.DefaultValue : 0f;
    }

    // ------------------------------------------------------- Rack_StudioEQ

    /// <summary>
    ///     <c>Rack_StudioEQ</c> (418 of the library's 580 tones): Bass shelf, Low-Mid
    ///     bell and Treble shelf on one <c>eq</c>; the High-Mid bell is the companion
    ///     (<see cref="BuildHiMidBand" />). Frequencies and Q go through the GAME'S
    ///     curves (<see cref="RsGameCurves" />): the knob labels are not the corners
    ///     (Bass Freq. 120, the default, is a 39 Hz shelf; Q Wide/Narrow is 0.8 / 2.5).
    ///     Gains are passed through in dB — the game's gain curves are S-curves of
    ///     unpublished shape, and their ends are the labelled ±12 dB.
    /// </summary>
    private static DcRigModule BuildStudioEq(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, RsStudioEqCurves eq)
    {
        var p = new ParamMap(Eq, ov, gear.GearKey);
        SetBandGain(gear, catalog, p, "lowGainDb", "Bass");
        p.Set("lowFreq", eq.BassFreqHz.Evaluate(KnobOrDefault(gear, catalog, "BassFreq")));
        SetBandGain(gear, catalog, p, "midGainDb", "LoMid");
        p.Set("midFreq", eq.LoMidFreqHz.Evaluate(KnobOrDefault(gear, catalog, "LoMidFreq")));
        p.Set("midQ", eq.Q.Evaluate(KnobOrDefault(gear, catalog, "LoMidQ")));
        SetBandGain(gear, catalog, p, "highGainDb", "Treble");
        p.Set("highFreq", eq.TrebleFreqHz.Evaluate(KnobOrDefault(gear, catalog, "TrebleFreq")));
        p.ApplyStatic();
        return new DcRigModule(Eq, p.Build());
    }

    /// <summary>Rack_StudioEQ's High-Mid bell through the game's curves; shelves at
    /// 0 dB, which the core skips.</summary>
    private static DcRigModule BuildStudioEqHiMid(RsGear gear, RsGearCatalog catalog, RsStudioEqCurves eq)
    {
        var p = new ParamMap(Eq, null, gear.GearKey);
        SetBandGain(gear, catalog, p, "midGainDb", "HiMid");
        p.Set("midFreq", eq.HiMidFreqHz.Evaluate(KnobOrDefault(gear, catalog, "HiMidFreq")));
        p.Set("midQ", eq.Q.Evaluate(KnobOrDefault(gear, catalog, "HiMidQ")));
        p.Set("lowGainDb", 0f);
        p.Set("highGainDb", 0f);
        return new DcRigModule(Eq, p.Build());
    }

    // --------------------------------------------------- Pedal_Compression

    /// <summary>
    ///     <c>Pedal_Compression</c> (164 tones) is the Audiokinetic Wwise Compressor in
    ///     the game's bank — a plain feed-forward threshold / ratio / attack / release
    ///     compressor, which is what the <c>compressor</c> module is. The OPTICAL
    ///     compressor would be wrong here: its light-cell ballistics have no attack or
    ///     release, so both knobs would fold. Attack and Release land 1:1 (the bank
    ///     maps both linearly onto their labels), and the Compression knob drives the
    ///     three params the bank hangs on it — threshold, ratio and output gain (the
    ///     pedal has no level knob). The ratio's top end (20.09:1) is held at the
    ///     module's 20:1. Knee and detector stay the module's: the Wwise Compressor
    ///     exposes neither.
    /// </summary>
    private static DcRigModule BuildWwiseCompressor(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, RsCompressionCurves curves)
    {
        var p = new ParamMap(Compressor, ov, gear.GearKey);
        var comp = KnobOrDefault(gear, catalog, "Comp");
        p.Set("thresholdDb", curves.ThresholdDb.Evaluate(comp));
        p.Set("ratio", curves.Ratio.Evaluate(comp));
        p.Set("makeupDb", curves.OutputGainDb.Evaluate(comp));
        p.Set("attackMs", curves.AttackMs.Evaluate(KnobOrDefault(gear, catalog, "Attack")));
        p.Set("releaseMs", curves.ReleaseMs.Evaluate(KnobOrDefault(gear, catalog, "Release")));
        p.ApplyStatic();
        return new DcRigModule(Compressor, p.Build());
    }

    // ---------------------------------------------------- Bass_Pedal_MBComp

    /// <summary><c>Bass_Pedal_MBComp</c> with an install: one broadband
    /// <c>compressor</c> fitted to the pedal's own chain (<see cref="RsMbCompModel" />).</summary>
    private static DcRigModule BuildMbComp(RsGear gear, RsGearCatalog catalog, ConversionReport report, RsMbCompModel model)
    {
        var notes = new List<string>();
        var p = model.CompressorParams(KnobOrDefault(gear, catalog, "Compress"), KnobOrDefault(gear, catalog, "Rate"), notes);
        foreach (var note in notes) report.Add(ConversionNoteKind.Approximated, gear.GearKey, note);
        return new DcRigModule(Compressor, p);
    }

    // ---------------------------------------------------- Pedal_LoFiFilter

    /// <summary>
    ///     <c>Pedal_LoFiFilter</c> (13 tones): a low cut and a
    ///     high cut, as two manual <c>svf</c> nodes — this one the low cut (high-pass),
    ///     the companion the high cut. Corners from the game's own curves: Lo Cut
    ///     (key <c>Mix</c>) 50 → 1000 Hz, Hi Cut (key <c>FilterType</c>) 18 kHz → 1 kHz,
    ///     both "up = more cut" (defaults: a ~695 Hz low cut, a ~15 kHz high cut).
    ///     The game's plug-in is a custom one whose other parameters (and so its
    ///     slopes) are not decodable, so each half is a 2-pole Butterworth
    ///     (resonance 0 = Q 0.707) — reported. The whole Hi Cut range lands: the svf
    ///     designs its corner under 0.9 x Nyquist (19.8 kHz at 44.1 kHz); a design
    ///     that clamps at a quarter of the rate would pin every knob value under
    ///     ~48, the default 30 included, at 12 kHz.
    /// </summary>
    private static DcRigModule BuildLoFiFilter(RsGear gear, RsGearCatalog catalog, ConversionReport report, RsLoFiCurves curves)
    {
        report.Add(ConversionNoteKind.Approximated, gear.GearKey,
            "lo-fi filter: both corners follow the game's own knob curves, but its plug-in's slopes are not decodable (two 2-pole filters)");
        return ManualSvf(gear, SvfHighPass, curves.LoCutHz.Evaluate(KnobOrDefault(gear, catalog, "Mix")));
    }

    private static DcRigModule BuildLoFiHighCut(RsGear gear, RsGearCatalog catalog, RsLoFiCurves curves)
        => ManualSvf(gear, SvfLowPass, curves.HiCutHz.Evaluate(KnobOrDefault(gear, catalog, "FilterType")));

    /// <summary><c>svf</c> mode indices (FilterGear.cpp's labels).</summary>
    private const float SvfLowPass = 0f, SvfHighPass = 2f;

    private static DcRigModule ManualSvf(RsGear gear, float mode, float cutoffHz)
    {
        var p = new ParamMap(Svf, null, gear.GearKey);
        p.Set("mode", mode);
        p.Set("source", 0f);   // Manual: a fixed corner, no sweep
        p.Set("cutoffHz", cutoffHz);
        p.Set("resonance", 0f); // Q = 0.707 * 28^0: Butterworth
        p.Set("drive", 0f);
        p.Set("mix", 1f);
        return new DcRigModule(Svf, p.Build());
    }

    // -------------------------------------------------- Pedal_BassEmulator

    /// <summary>
    ///     <c>Pedal_BassEmulator</c> (no library tone) is the
    ///     Audiokinetic Wwise Harmonizer in the game's bank: ONE voice an octave down
    ///     with the dry signal off (-96 dB). That is the <c>harmonizer</c> module at
    ///     -12 semitones, fully wet. Body and Tone drive the voice's PEAKING filter
    ///     (gain -4..+6 dB at 100..1800 Hz, Q 1), the companion <c>eq</c> bell.
    /// </summary>
    private static DcRigModule BuildBassEmulator(RsGear gear, RsGearOverrides ov)
    {
        var p = new ParamMap(Harmonizer, ov, gear.GearKey);
        p.Set("semitones", RsGameCurves.BassEmulatorSemitones);
        p.Set("mix", 1f);
        p.ApplyStatic();
        return new DcRigModule(Harmonizer, p.Build());
    }

    private static DcRigModule BuildBassEmulatorFilter(RsGear gear, RsGearCatalog catalog, RsBassEmulatorCurves curves)
    {
        var p = new ParamMap(Eq, null, gear.GearKey);
        p.Set("midGainDb", curves.BodyDb.Evaluate(KnobOrDefault(gear, catalog, "Body")));
        p.Set("midFreq", curves.ToneHz.Evaluate(KnobOrDefault(gear, catalog, "Tone")));
        p.Set("midQ", RsGameCurves.BassEmulatorFilterQ);
        p.Set("lowGainDb", 0f);
        p.Set("highGainDb", 0f);
        return new DcRigModule(Eq, p.Build());
    }
}
