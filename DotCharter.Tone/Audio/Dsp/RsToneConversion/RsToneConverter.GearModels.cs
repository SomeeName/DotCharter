// RsToneConverter's dedicated-model builders: the 0.9 gear-wave targets (one circuit model per RS pedal or head).
using System.Globalization;
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigTargetCatalog;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

public sealed partial class RsToneConverter
{
    // ================================================================
    // 0.9 gear wave targets (native catalogue 57 → 100)
    //
    // Every builder below exists because the module it emits is a STRICTLY
    // better target than what this converter reached for before it — either a
    // faithful model of the very circuit the RS pedal is (rat / ds1 / hm2 /
    // bassod / bassamp / tubepre / octfuzz / bbddelay / limiter / multiband /
    // optocomp), a module with params the old target simply did not have
    // (hallreverb's `diffusion`, multitap's second tap, svf's filter mode +
    // follower times), or a module for gear that was DROPPED entirely
    // (svf, acousticsim). Routing is data — `targetModule` in
    // rs_gear_overrides.json — so a key moves without touching this file.
    //
    // Knob names here are the RS knob KEY SUFFIX, not the display name: they
    // disagree often (Rack_StudioChamber's "Diffusion" knob has the key suffix
    // `Depth`, Pedal_RingMod's "Rate" is `Attack` and its "Mix" is
    // `Sensitivity`). Read the key, never the label.
    // ================================================================

    /// <summary>ProCo-Rodent-style distortion. <b>`filter` is inverted on
    /// purpose</b> — the real pedal's filter pot is darker clockwise, while
    /// every RS "Tone" is brighter clockwise, so the mapping must flip it or
    /// every converted RAT comes out at the wrong end of its own tone control.</summary>
    private static DcRigModule BuildRat(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        var p = new ParamMap(Rat, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Gain" or "Drive" or "Distortion" or "Dist":
                    p.Set("distortion", Norm01(val, knob));
                    break;
                case "Tone" or "Filter" or "Treble":
                    p.Set("filter", 1f - Norm01(val, knob));
                    break;
                case "Level" or "Output" or "Volume" or "Vol":
                    p.Set("volume", Norm01(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(Rat, p.Build());
    }

    /// <summary>The orange-box distortion (`ds1`): op-amp drive into a passive
    /// tilt tone stack — the same three-knob shape as the RS pedal.</summary>
    private static DcRigModule BuildDs1(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        var p = new ParamMap(Ds1, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Gain" or "Drive" or "Distortion" or "Dist":
                    p.Set("dist", Norm01(val, knob));
                    break;
                case "Tone" or "Filter" or "Contour":
                    p.Set("tone", Norm01(val, knob));
                    break;
                case "Level" or "Output" or "Volume" or "Vol":
                    p.Set("level", Norm01(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(Ds1, p.Build());
    }

    /// <summary>The chainsaw (`hm2`). Its two colour controls are BOOST-only
    /// resonant bells (0…+12 dB each), not a bass/treble pair, so a single RS
    /// Tone knob is mapped as the TILT between them — full clockwise is all
    /// 1 kHz bite, full anticlockwise all 80 Hz chug, and the middle is the
    /// "both knobs at noon" sound the pedal is famous for.</summary>
    private static DcRigModule BuildHm2(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        var p = new ParamMap(Hm2, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Gain" or "Drive" or "Distortion" or "Dist":
                    p.Set("dist", Norm01(val, knob));
                    break;
                case "Tone" or "Contour":
                {
                    var t = Norm01(val, knob);
                    p.Set("highDb", t * 12f);
                    p.Set("lowDb", (1f - t) * 12f);
                    break;
                }
                case "Bass" or "Low":
                    p.Set("lowDb", Norm01(val, knob) * 12f);
                    break;
                case "Treble" or "High":
                    p.Set("highDb", Norm01(val, knob) * 12f);
                    break;
                case "Level" or "Output" or "Volume" or "Vol":
                    p.Set("level", Norm01(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(Hm2, p.Build());
    }

    /// <summary>The bass overdrive / bass DI (`bassod`): a driven leg blended
    /// against a clean leg, with real ±12 dB bass and treble shelves. That
    /// BLEND is the whole point of the circuit and neither generic drive module
    /// has one, so an RS Blend knob would have nothing to land on.</summary>
    private static DcRigModule BuildBassOd(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        if (gear.GearKey == RsGameCurves.BassOverdriveKey && RsGameCurves.Current.BassOverdrive is { } game)
            return BuildGameBassOverdrive(gear, catalog, ov, report, game);
        var p = new ParamMap(BassOd, ov, gear.GearKey);
        var folded = false;
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Gain" or "Drive" or "Distortion" or "Dist" or "Fuzz":
                    p.Set("drive", Norm01(val, knob));
                    break;
                case "Blend" or "Mix":
                    p.Set("blend", Norm01(val, knob));
                    break;
                case "Bass" or "Low" or "Lo":
                    p.Set("bass", Shelf(val, knob));
                    break;
                case "Tone" or "Treble" or "High" or "Hi":
                    p.Set("treble", Shelf(val, knob));
                    break;
                case "Level" or "Output" or "Volume" or "Vol" or "Master":
                    p.Set("level", Norm01(val, knob));
                    break;
                case "Filter" or "Pres" or "Presence" or "Mid" or "Middle":
                    folded = true;
                    break;
            }
        }

        if (folded)
        {
            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                "bass drive: filter / presence / mid knob has no bassod param — folded");
        }

        p.ApplyStatic();
        return new DcRigModule(BassOd, p.Build());
    }

    /// <summary><c>dcdsp_bassod.h</c>: the output gain is <c>level</c> x 1.4.</summary>
    private const float BassOdLevelGain = 1.4f;

    /// <summary><c>dcdsp_bassod.h</c>: <c>drive</c> 0..1 is 0..+32 dB into its tanh.</summary>
    private const float BassOdDriveDbSpan = 32f;

    /// <summary>
    ///     <c>Bass_Pedal_BassOverdrive</c> through the game's own bank: a Guitar
    ///     Distortion (Overdrive, ceiling 0.5) whose wet/dry is the Blend and whose
    ///     output gain rides Blend too, in the game's level frame. The game's mix is
    ///     <c>w · o · 0.5 · s / G + (1 - w) · x</c> in rig units (s the shaper's
    ///     normalised output, G the frame); <c>bassod</c> plays
    ///     <c>1.4 · level · (blend · s + (1 - blend) · x)</c>, so the two match when
    ///     <c>1.4 · level</c> is their sum of coefficients and <c>blend</c> the driven
    ///     share of it. At the Hysteria tone's Blend 70 that is a 7 % driven share at
    ///     level 0.2, where the label read gave 70 % at 0.6: a fuzz with the clean
    ///     bass 27 dB under it, against the game's clean-led blend. Drive: the game's
    ///     drive % (0.25 dB per %, dcdsp_rsamp.h's inferred law) plus the frame plus
    ///     the 6 dB between the two shapers' ceilings, over bassod's 32 dB span. Tone
    ///     is the post-EQ's treble cut (0 dB at the top); the Filter knob's low-end
    ///     pre/de-emphasis around the drive has no bassod param and is reported.
    /// </summary>
    private static DcRigModule BuildGameBassOverdrive(
        RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report, RsBassOverdriveCurves curves)
    {
        var p = new ParamMap(BassOd, ov, gear.GearKey);
        var blendKnob = KnobOrDefault(gear, catalog, "Blend");
        var wet = curves.WetPct.Evaluate(blendKnob) / 100f;
        var outGain = MathF.Pow(10f, curves.OutputDb.Evaluate(blendKnob) / 20f);
        var frame = MathF.Pow(10f, RsGameCurves.GameLevelDb / 20f);
        var driven = wet * outGain * RsGameCurves.BassOverdriveShaperCeiling / frame;
        var clean = 1f - wet;
        p.Set("blend", driven / (driven + clean));
        p.Set("level", Math.Clamp((driven + clean) / BassOdLevelGain, 0f, 1f));

        var driveDb = curves.DrivePct.Evaluate(KnobOrDefault(gear, catalog, "Gain")) * 0.25f
                      + RsGameCurves.GameLevelDb + 20f * MathF.Log10(1f / RsGameCurves.BassOverdriveShaperCeiling);
        p.Set("drive", Math.Clamp(driveDb / BassOdDriveDbSpan, 0f, 1f));
        p.Set("treble", Math.Max(-12f, curves.TrebleDb.Evaluate(KnobOrDefault(gear, catalog, "Tone"))));
        p.Set("bass", 0f);
        report.Add(ConversionNoteKind.Approximated, gear.GearKey,
            "bass overdrive: the game's blend and levels; its Filter knob (a low-end emphasis around the drive) has no bassod param — folded");
        p.ApplyStatic();
        return new DcRigModule(BassOd, p.Build());
    }

    /// <summary>A pedal limiter (`limiter`). The RS knob is literally "Limit",
    /// and a limiter is not a 4:1 studio compressor: more Limit means a LOWER
    /// ceiling, and a higher Rate ("Speed") means a SHORTER release, so both
    /// knobs map descending.</summary>
    private static DcRigModule BuildLimiter(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        var p = new ParamMap(Limiter, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Limit" or "Threshold" or "Thresh" or "Comp":
                    p.Set("ceilingDb", NaturalScaled(val, knob, 0f, -20f));
                    break;
                case "Rate" or "Speed" or "Release":
                    p.Set("releaseMs", NaturalScaled(val, knob, 500f, 5f));
                    break;
                case "Level" or "Gain" or "Makeup" or "Output":
                    p.Set("gainDb", NaturalScaled(val, knob, 0f, 12f));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(Limiter, p.Build());
    }

    /// <summary>A multiband bass compressor (`multiband`). The one RS pedal
    /// that says "multiband" on the tin drove a single-band studio compressor
    /// before this, which discarded its Transition Band knob entirely — that
    /// knob IS a crossover and now sets one. For <c>Bass_Pedal_MBComp</c> this is the
    /// generic path only: with an install it is the game's own chain fitted onto a
    /// <c>compressor</c> (<see cref="BuildMbComp" />), and without one the report
    /// says the labels were read instead.</summary>
    private static DcRigModule BuildMultiband(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        if (gear.GearKey == RsGameCurves.MbCompKey)
        {
            report.Add(ConversionNoteKind.Fallback, gear.GearKey,
                "MB Comp: the game's own model is read from a Rocksmith 2014 install; without one it is converted by its knob labels");
        }

        var p = new ParamMap(Multiband, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Compress" or "Compression" or "Comp" or "Amount":
                {
                    var ratio = NaturalScaled(val, knob, 1f, 20f);
                    p.Set("lowRatio", ratio);
                    p.Set("midRatio", ratio);
                    p.Set("highRatio", ratio);
                    break;
                }
                case "Rate" or "Recovery" or "Release" or "Speed":
                    p.Set("releaseMs", NaturalScaled(val, knob, 1000f, 5f));
                    break;
                case "Filter" or "Transition" or "Crossover":
                    // Hz knob: passes through and clamps to the module's 60..500.
                    p.Set("lowXoverHz", Direct(val, knob));
                    break;
                case "Attack":
                    p.Set("attackMs", Direct(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(Multiband, p.Build());
    }

    /// <summary>The squashy always-on pedal compressor (`optocomp`). Its
    /// ballistics are the light cell's own — that is what makes an opto comp
    /// one — so an RS Attack/Release pair has nowhere to go and the report says
    /// so rather than pretending the values survived.</summary>
    private static DcRigModule BuildOptoComp(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        var p = new ParamMap(OptoComp, ov, gear.GearKey);
        var folded = false;
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Comp" or "Compression" or "Compress" or "Sustain" or "Amount" or "Smash":
                    p.Set("peakReduction", Norm01(val, knob));
                    break;
                case "Level" or "Gain" or "Makeup" or "Output":
                    p.Set("gainDb", NaturalScaled(val, knob, 0f, 24f));
                    break;
                case "Tone":
                    p.Set("emphasis", Norm01(val, knob));
                    break;
                case "Attack" or "Release":
                    folded = true;
                    break;
            }
        }

        if (folded)
        {
            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                "optical compressor: attack/release folded — the light cell's own ballistics are the effect");
        }

        p.ApplyStatic();
        return new DcRigModule(OptoComp, p.Build());
    }

    /// <summary>An envelope-swept multimode filter (`svf`). The three RS synth
    /// filters were <b>SKIPPED OUTRIGHT</b> before this (Filter category, no EQ
    /// gain band ⇒ dropped), and they are a shape-for-shape match: sensitivity,
    /// follower attack/release, a low-pass/band-pass/high-pass selector and a
    /// wet mix.</summary>
    private static DcRigModule BuildSvf(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        var p = new ParamMap(Svf, ov, gear.GearKey);
        p.Set("source", 2f); // Envelope — these pedals have no manual/LFO mode

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Sens" or "Sensitivity":
                    p.Set("envSens", Norm01(val, knob));
                    break;
                case "Attack":
                    p.Set("envAttackMs", Direct(val, knob));
                    break;
                case "Release":
                    p.Set("envReleaseMs", Direct(val, knob));
                    break;
                case "Filter" or "FilterType" or "Type":
                    // RS enum 0/1/2 = low-pass / band-pass / high-pass, the same
                    // order as the module's first three modes.
                    p.Set("mode", MathF.Round(val));
                    break;
                case "Mix" or "Res" or "Blend" or "Level":
                    p.Set("mix", Norm01(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(Svf, p.Build());
    }

    /// <summary>The acoustic emulator. `acousticsim` has existed since the
    /// 0.15.2 wave; this key matched no branch and was SKIPPED as a "non-EQ
    /// filter with no equivalent module", which was never true.</summary>
    private static DcRigModule BuildAcousticSim(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        // Mid / MidShift are the game's mid band: a notch node right after this one
        // (BuildAcousticEmulatorMid).
        var p = new ParamMap(AcousticSim, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Body":
                    p.Set("body", Norm01(val, knob));
                    break;
                case "Tone" or "Treble" or "Brightness":
                    p.Set("brightness", Norm01(val, knob));
                    break;
                case "Mix" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(AcousticSim, p.Build());
    }

    /// <summary>The per-repeat gain the top of an RS Feedback knob maps to: the
    /// catalogue ceiling of the <c>delay</c> and <c>tapedelay</c> feedback params,
    /// which take the RS percentage straight. A long, decaying tail — never 1.0,
    /// because no Rocksmith delay regenerates.</summary>
    internal const float RsMaxDelayRegen = 0.95f;

    /// <summary>Mirror of <c>kRegenExcess</c> in dcdsp_bbddelay.h. Read with
    /// <see cref="BbdRegen" />; RsDelayRegenerationTests renders the real module,
    /// so a native change that moves the curve fails there.</summary>
    private const float BbdRegenExcess = 0.7f;

    /// <summary>The loop gain <c>bbddelay</c> runs at for a <c>feedback</c> knob
    /// value: <c>f·(1 + 0.7·f⁶)</c> (dcdsp_bbddelay.h <c>applyDerived</c>). The
    /// excess is the analog pedal's self-oscillation — it crosses 1.0 at
    /// f ≈ 0.82, so the top fifth of the knob SINGS and grows.</summary>
    internal static float BbdRegen(float feedback)
    {
        var f2 = feedback * feedback;
        return feedback * (1f + (BbdRegenExcess * f2 * f2 * f2));
    }

    /// <summary>
    ///     The <c>bbddelay</c> feedback that gives a per-repeat gain of
    ///     <paramref name="regen" /> (capped at <see cref="RsMaxDelayRegen" />).
    ///     An RS Feedback percentage is a per-repeat gain, as on every other delay
    ///     target; fed to the BBD's knob as-is, anything past ~82 % landed in the
    ///     oscillating zone and the repeats grew without end — and
    ///     <c>Pedal_AnalogueDelay</c>'s own default is 92. Bisection over the
    ///     monotonic <see cref="BbdRegen" />, returning the lower bound so the
    ///     result never overshoots the target.
    /// </summary>
    internal static float BbdFeedbackForRegen(float regen)
    {
        var target = Math.Clamp(regen, 0f, RsMaxDelayRegen);
        var lo = 0f;
        var hi = 1f;
        for (var i = 0; i < 32; i++)
        {
            var mid = 0.5f * (lo + hi);
            if (BbdRegen(mid) <= target) lo = mid;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>A bucket-brigade echo (`bbddelay`): clock noise, a darkening
    /// repeat path and a modulated clock. The tape echo models a different
    /// machine — and the BBD's time range is genuinely
    /// SHORTER (20–600 ms), so a longer RS time is clamped and reported instead
    /// of silently landing somewhere it cannot go.</summary>
    private static DcRigModule BuildBbdDelay(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        var p = new ParamMap(BbdDelay, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Time" or "Delay":
                {
                    var ms = Direct(val, knob);
                    p.Set("timeMs", ms);
                    if (ms > 600f)
                    {
                        report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                            string.Create(CultureInfo.InvariantCulture,
                                $"delay time {ms:0} ms clamped to the BBD's 600 ms ceiling"));
                    }

                    break;
                }
                case "Feedback" or "Regen" or "Repeats":
                    p.Set("feedback", BbdFeedbackForRegen(Norm01(val, knob)));
                    break;
                case "Mix" or "Level" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
                case "Mod" or "Depth":
                    p.Set("modDepth", Norm01(val, knob));
                    break;
                case "Rate":
                    p.Set("modRateHz", NaturalScaled(val, knob, 0.1f, 5f));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(BbdDelay, p.Build());
    }

    /// <summary>The two-tap studio delay (`multitap`). The clean digital delay
    /// has ONE tap, so the rack's second time knob would be
    /// dropped; multitap carries the alternating hard-panned taps that shape is
    /// for. The second tap's exact time is still an approximation — the module
    /// derives its taps from one base interval and a ratio set.</summary>
    private static DcRigModule BuildMultitap(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        var p = new ParamMap(Multitap, ov, gear.GearKey);
        p.Set("taps", 2f);
        p.Set("pan", 1f); // a stereo L/R rack delay IS the hard-alternating case

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "TimeL" or "Time" or "Delay":
                    p.Set("timeMs", Direct(val, knob));
                    break;
                case "Feedback" or "Regen" or "Repeats":
                    p.Set("feedback", Norm01(val, knob));
                    break;
                case "Mix" or "Level" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
                case "Filter" or "Tone" or "Color":
                    p.Set("tone", Norm01(val, knob));
                    break;
                case "TimeR":
                    report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                        "second tap time approximated — multitap derives its taps from one base interval and a ratio set");
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(Multitap, p.Build());
    }

    /// <summary>The FDN hall/room (`hallreverb`). It is the only reverb module
    /// with a `diffusion` param, and the RS studio racks carry a Diffusion knob
    /// that must not be dropped — it is the difference between a
    /// chamber and a smear. Wet level still scales by
    /// <see cref="RsReverbMixScale"/> like every other converted reverb.</summary>
    private static DcRigModule BuildHallReverb(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        var p = new ParamMap(HallReverb, ov, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Time" or "Decay" or "Size" or "Length" or "Room":
                    // 0.3..8 s, not the module's full 20 s: an RS reverb at its
                    // maximum is a big hall, never a 20-second freeze.
                    p.Set("decayS", NaturalScaled(val, knob, 0.3f, 8f));
                    break;
                case "Depth" or "Diffusion":
                    p.Set("diffusion", Norm01(val, knob));
                    break;
                case "Tone" or "Color":
                    // Brighter ⇒ a HIGHER in-loop damping corner.
                    p.Set("dampingKHz", NaturalScaled(val, knob, 1.5f, 16f));
                    break;
                case "Damp" or "Damping":
                    p.Set("dampingKHz", NaturalScaled(val, knob, 16f, 1.5f));
                    break;
                case "Mix" or "Level" or "Blend" or "Wet":
                    p.Set("mix", ReverbMix(val, knob));
                    break;
            }
        }

        p.SetIfAbsent("mix", RsReverbMixScale * 0.3f);
        p.ApplyStatic(); // `space` (Room/Chamber/Hall/Cathedral) is pinned per key
        return new DcRigModule(HallReverb, p.Build());
    }

    /// <summary>The octave-up fuzz (`octfuzz`) — the circuit an RS "octave up"
    /// pedal actually is. The old target was the SUB-octave octaver, i.e. the
    /// wrong direction, and the converter said so in the report every time.</summary>
    private static DcRigModule BuildOctFuzz(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        var p = new ParamMap(OctFuzz, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Mix" or "Blend" or "Octave":
                    p.Set("octave", Norm01(val, knob));
                    break;
                case "Tone" or "Filter":
                    p.Set("tone", Norm01(val, knob));
                    break;
                case "Gain" or "Drive" or "Fuzz":
                    p.Set("fuzz", Norm01(val, knob));
                    break;
                case "Level" or "Output" or "Volume" or "Vol":
                    p.Set("volume", Norm01(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(OctFuzz, p.Build());
    }

    /// <summary>A ring modulator (`ringmod`). Its RS knob keys do not match
    /// their labels at all: the "Rate" knob's key suffix is <c>Attack</c> and
    /// carries a PERIOD in ms (so the carrier frequency is its reciprocal), and
    /// the "Mix" knob's key suffix is <c>Sensitivity</c>.</summary>
    private static DcRigModule BuildRingMod(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        var p = new ParamMap(RingMod, ov, gear.GearKey);
        var folded = false;
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Attack" or "Rate" or "Speed" or "Freq":
                    p.Set("freqHz", knob is { UnitType: RsKnobUnit.Ms }
                        ? (val > 0.01f ? 1000f / val : 3000f)   // period → frequency
                        : Direct(val, knob));
                    break;
                case "Sensitivity" or "Mix" or "Blend" or "Level":
                    p.Set("mix", Norm01(val, knob));
                    break;
                case "Waveform" or "Wave" or "Shape" or "Funk":
                    p.Set("waveform", MathF.Round(val));
                    break;
                case "Depth" or "Intensity":
                    folded = true;
                    break;
            }
        }

        if (folded)
        {
            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                "ring modulator: depth folded — ringmod's carrier depth is fixed, the wet mix is the control");
        }

        p.ApplyStatic();
        return new DcRigModule(RingMod, p.Build());
    }

    // ================================================================
    // Gear that maps onto a module that already exists (it would otherwise be
    // dropped or folded). The coverage guide
    // docs/agents/services/rs-tone-coverage.md lists what is still missing.
    // ================================================================

    /// <summary>
    ///     <c>Pedal_RangeBooster</c> is a Rangemaster-style treble booster, and the
    ///     <c>boost</c> module's <c>character</c> = 1 IS that circuit
    ///     (<c>dcdsp_boost.h</c>: the ~700 Hz input thinning plus the germanium-ish
    ///     squash). Its one knob, "Boost!", is the amount: RS percent onto the
    ///     module's 0..24 dB.
    /// </summary>
    private static DcRigModule BuildTrebleBooster(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        var p = new ParamMap(Boost, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            if (pot is "Boost" or "Gain" or "Level")
                p.Set("gainDb", NaturalScaled(val, knob, 0f, 24f));
        }

        p.Set("character", 1f);
        p.Set("tilt", 0f);
        p.ApplyStatic();
        return new DcRigModule(Boost, p.Build());
    }

    /// <summary>
    ///     <c>Pedal_BitCruncher</c> onto <c>bitcrusher</c>, through the game's own bank:
    ///     the pedal is a McDSP FutzBox — input +10 dB, a distortion at
    ///     Amount 66 % that saturates the signal BEFORE it is crushed, the Lo-Fi
    ///     section, output -14 dB on the crushed path only, Mix as its wet/dry. Its
    ///     knob KEYS disagree with their labels (like the ring mod's): "Bit Depth" is
    ///     <c>FilterType</c>, "Downsample" <c>Release</c>, "Filter" <c>Attack</c> (the
    ///     Lo-Fi anti-alias filter, UP = less of it) and "Rectify" <c>Sens</c> (the
    ///     distortion's rectifier, DOWN = more of it). The FutzBox stages run in the
    ///     game's level frame (<see cref="RsGameCurves.GameLevelDb" />), so the
    ///     crushed layer sits under the dry bass at the level the game gives it.
    ///     Without the distortion, the output gain, Filter and Rectify the result is
    ///     a raw 4-bit crush of the quiet rig signal, as loud as the dry.
    /// </summary>
    private static DcRigModule BuildBitCrusher(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        var p = new ParamMap(BitCrusher, ov, gear.GearKey);
        p.Set("bits", RsGameCurves.BitCruncherBits(KnobOrDefault(gear, catalog, "FilterType")));
        p.Set("downsample", RsGameCurves.BitCruncherDownsampleFactor(KnobOrDefault(gear, catalog, "Release")));
        if (RsGameCurves.Current.BitCruncher is { } game)
        {
            p.Set("mix", game.MixPct.Evaluate(KnobOrDefault(gear, catalog, "Mix")) / 100f);
            p.Set("filter", game.FilterPct.Evaluate(KnobOrDefault(gear, catalog, "Attack")));
            p.Set("rectify", game.RectifyPct.Evaluate(KnobOrDefault(gear, catalog, "Sens")));
            p.Set("drive", game.DrivePct);
            p.Set("intensity", game.IntensityPct);
            p.Set("inputDb", game.InputDb + RsGameCurves.GameLevelDb);
            p.Set("outputDb", game.OutputDb - RsGameCurves.GameLevelDb);
            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                "bit cruncher: the game's FutzBox — its distortion and Lo-Fi filter laws are inferred (unpublished)");
        }
        else
        {
            // No install to read the FutzBox from: bits, rate and Mix by their
            // labels; the module's own saturation and levels.
            catalog.TryGetKnob(gear.GearKey, "Mix", out var mixKnob);
            p.Set("mix", Norm01(KnobOrDefault(gear, catalog, "Mix"), mixKnob));
            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                "bit cruncher: generic mapping (bits, rate and mix by their labels); the game's own stages need a Rocksmith 2014 installation");
        }

        p.ApplyStatic();
        return new DcRigModule(BitCrusher, p.Build());
    }
}
