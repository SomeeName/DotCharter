using System.Globalization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using static DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity.RsToneFidelityTones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity;

/// <summary>The outcome of one target: pass/fail plus the measured numbers.</summary>
public sealed record TargetResult(string Id, bool Pass, string Measured);

/// <summary>
///     A calibration target. There is NO Rocksmith reference render to compare
///     against (the game's audio is neither shippable nor scriptable), so every
///     target is RELATIONAL and taken from the gear's documented character — a
///     ribbon is darker than a condenser, a high-gain head distorts more than a
///     clean one at the same knob, a Rangemaster boosts treble — or from the
///     repo's own loudness contract. No target invents an absolute number.
/// </summary>
public sealed record FidelityTarget(string Id, string Claim, string Source, Func<Func<string, DcRigPreset, ToneRender>, TargetResult> Evaluate);

/// <summary>The target set the harness checks; see <see cref="FidelityTarget" />.</summary>
public static class RsToneFidelityTargets
{
    private const string Amp = "Amp_MarshallJCM800";
    private const string GuitarCabStem = "Cab_GB412CMKIII";

    /// <summary>The ±6 dB band <c>DspTonePresetLibraryTests</c> already holds every
    /// factory preset's ESTIMATE to; here it is held against the RENDER.</summary>
    public const double LoudnessToleranceDb = 6.0;

    public static IReadOnlyList<FidelityTarget> All { get; } =
    [
        new("mic-air-order",
            "Same amp and cab, mic on the cap: the top octave (8-16 kHz) ranks Condenser > 57 > Ribbon.",
            "Mic class character: small/large condensers are specified flat to 15-20 kHz; the SM57's published response rolls off above ~10 kHz; ribbon mics roll off from a few kHz (figure-8 ribbon physics).",
            render =>
            {
                var c = Air(render, "Condenser", "Cone");
                var s = Air(render, "57", "Cone");
                var r = Air(render, "Ribbon", "Cone");
                return Result("mic-air-order", c > s && s > r, $"8-16 kHz vs midband: Condenser {c:F1}, 57 {s:F1}, Ribbon {r:F1} dB");
            }),
        new("mic-57-presence",
            "The 57 has more presence (4-6.3 kHz over 1-2 kHz) than the ribbon.",
            "Shure SM57 spec sheet: a presence rise centred around 5-6 kHz; ribbons have none.",
            render =>
            {
                var s = Presence(render, "57");
                var r = Presence(render, "Ribbon");
                return Result("mic-57-presence", s > r, $"presence (4-6.3 kHz minus 1-2 kHz): 57 {s:F1}, Ribbon {r:F1} dB");
            }),
        new("mic-edge-darker",
            "For every mic, moving it from the cone (cap) to the edge loses top end (4-16 kHz).",
            "Close-miking practice: on-axis at the dust cap is the brightest spot; toward the cone edge the high frequencies fall.",
            render =>
            {
                var parts = new List<string>();
                var ok = true;
                foreach (var mic in new[] { "57", "Condenser", "Ribbon" })
                {
                    var cone = Top(render, mic, "Cone");
                    var edge = Top(render, mic, "Edge");
                    ok &= edge < cone;
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{mic} cone {cone:F1} / edge {edge:F1}"));
                }

                return Result("mic-edge-darker", ok, $"4-16 kHz vs midband: {string.Join(", ", parts)} dB");
            }),
        new("mic-tube-u67",
            "Same amp and cab, mic on the cap: the Tube condenser carries less 63-125 Hz than the small Condenser, and more 8-10 kHz than the 57.",
            "Neumann U 67 operating manual, cardioid response: -2.2 dB at 100 Hz, a broad +1.5 dB rise at 9-10 kHz; Shure SM57 user guide: the response falls away above its 5-6 kHz presence peak.",
            render =>
            {
                double Band(string mic, double lo, double hi) => ToneMetrics.BandMean(Mic(render, mic, "Cone").Measurement.BandDb, lo, hi);
                var tubeLow = Band("Tube", 63, 125);
                var condLow = Band("Condenser", 63, 125);
                var tubeTop = Band("Tube", 8000, 10000);
                var s57Top = Band("57", 8000, 10000);
                return Result("mic-tube-u67", tubeLow < condLow && tubeTop > s57Top,
                    $"63-125 Hz vs midband: Tube {tubeLow:F1}, Condenser {condLow:F1} dB; 8-10 kHz: Tube {tubeTop:F1}, 57 {s57Top:F1} dB");
            }),
        new("mic-offaxis-polar",
            "Off Axis (the capsule turned 45 degrees on the cap) costs the 57 and the Tube top end (4-16 kHz against the midband), but leaves a figure-8 Ribbon's balance where it was: it loses level evenly.",
            "Shure SM57 user guide polar patterns at 45 deg: -1.2 dB at 1 kHz, -2.4 dB at 8 kHz; Neumann U 67 cardioid polar pattern at 45 deg: -1.7 dB at 1 kHz, -8.9 dB at 16 kHz; Royer R-121: a figure-8 whose 200 Hz / 1 kHz / 10 kHz curves coincide.",
            render =>
            {
                var parts = new List<string>();
                var ok = true;
                foreach (var mic in new[] { "57", "Tube" })
                {
                    var cone = Top(render, mic, "Cone");
                    var off = Top(render, mic, "OffAxis");
                    ok &= off < cone;
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{mic} cone {cone:F1} / off-axis {off:F1}"));
                }

                var ribbonCone = Top(render, "Ribbon", "Cone");
                var ribbonOff = Top(render, "Ribbon", "OffAxis");
                ok &= Math.Abs(ribbonOff - ribbonCone) < 0.5;
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"Ribbon cone {ribbonCone:F1} / off-axis {ribbonOff:F1}"));
                return Result("mic-offaxis-polar", ok, $"4-16 kHz vs midband: {string.Join(", ", parts)} dB");
            }),
        new("bass-cab-low-end",
            "A bass cab (8x10, 1x15) carries more of the low E (80-100 Hz bands over the midband) than a guitar 4x12, through the same bass head.",
            "Driver data: bass drivers resonate around 30-45 Hz where guitar 12\" drivers sit near 75-100 Hz (published Thiele-Small Fs), so a bass box extends lower.",
            render =>
            {
                double Low(string cab) => ToneMetrics.BandMean(
                    render("bass:" + cab, Convert("Bass_Amp_EdenWT550", cab)).Measurement.BandDb, 80, 100);
                var fridge = Low("Bass_Cab_AT810BC_57_Cone");
                var fifteen = Low("Bass_Cab_AT1150BC_57_Cone");
                var guitar = Low(GuitarCabStem + "_57_Cone");
                return Result("bass-cab-low-end", fridge > guitar && fifteen > guitar,
                    $"80-100 Hz vs midband: 8x10 {fridge:F1}, 1x15 {fifteen:F1}, guitar 4x12 {guitar:F1} dB");
            }),
        // ---- the cab IRs of #74, one claim per datasheet fact; every new
        //      model (12-25) appears in at least one (RsToneFidelityHarnessTests.
        //      CabTargets_CoverEveryNewModel).
        new("bass-cab-15-reaches-lowest",
            "Through the same bass head the 15-inch bass boxes (2x15 Bass, 4x15 Bass, 1x15 + 1x10 Bass) carry more 40-50 Hz than the 10-inch ones (2x10 Bass, 3x10 Bass, 6x10 Bass).",
            "Jensen BP15250 vs BP10150 datasheets: Fs 47 vs 55 Hz, Qts 0.51 vs 0.33 - a 65 Hz Qtc 0.707 closed box against a 68 Hz vented (QB3) one, the 10-inch falling at 24 dB/oct below it.",
            render =>
            {
                double Low(string cab) => ToneMetrics.BandMean(render("bass:" + cab, Convert("Bass_Amp_EdenWT550", cab)).Measurement.BandDb, 40, 50);
                var fifteens = new[] { "Bass_Cab_TW215BC_57_Cone", "Bass_Cab_GB415BC_57_Cone", "Bass_Cab_CA1510BC_57_Cone" }.Select(Low).ToArray();
                var tens = new[] { "Bass_Cab_CH210BC_57_Cone", "Bass_Cab_CH310BC_57_Cone", "Bass_Cab_EdenD610XST_57_Cone" }.Select(Low).ToArray();
                return Result("bass-cab-15-reaches-lowest", fifteens.Min() > tens.Max(),
                    $"40-50 Hz vs midband: 15-inch {Join(fifteens)}, 10-inch {Join(tens)} dB");
            }),
        new("bass-cab-below-guitar-ten",
            "Through the same bass head every new bass box (2x10 Bass, 3x10 Bass, 6x10 Bass, 1x12 Bass, 2x12 Bass, 2x15 Bass, 4x15 Bass, 1x15 + 1x10 Bass) carries more 50-63 Hz than the guitar 10-inch cabinets (4x10 Tweed Open, 1x10 Combo).",
            "Jensen datasheets: the bass drivers resonate at 47-55 Hz in boxes tuned to 64-68 Hz; the P10R guitar speaker at 97 Hz in an open back.",
            render =>
            {
                double Low(string cab) => ToneMetrics.BandMean(render("bass:" + cab, Convert("Bass_Amp_EdenWT550", cab)).Measurement.BandDb, 50, 63);
                var bass = new[]
                {
                    "Bass_Cab_CH210BC_57_Cone", "Bass_Cab_CH310BC_57_Cone", "Bass_Cab_EdenD610XST_57_Cone", "Bass_Cab_CS112BC_57_Cone",
                    "Bass_Cab_BT212BC_57_Cone", "Bass_Cab_TW215BC_57_Cone", "Bass_Cab_GB415BC_57_Cone", "Bass_Cab_CA1510BC_57_Cone"
                }.Select(Low).ToArray();
                var guitar = new[] { "Cab_TW410C_57_Cone", "Cab_TW110C_57_Cone" }.Select(Low).ToArray();
                return Result("bass-cab-below-guitar-ten", bass.Min() > guitar.Max(),
                    $"50-63 Hz vs midband: bass boxes {Join(bass)}, guitar 10-inch {Join(guitar)} dB");
            }),
        new("guitar-15-darker-than-10",
            "Same clean amp: the 2x15 Guitar is darker (3-6 kHz against the midband) than the 4x10 Tweed Open and the 1x10 Combo.",
            "Jensen's published curves: the P15N falls 12 dB below its midband by 4 kHz, where the P10R is still 8 dB above it.",
            render =>
            {
                double Top3k(string cab) => ToneMetrics.BandMean(render("clean:" + cab, Convert("Amp_TW22", cab)).Measurement.BandDb, 3000, 6000);
                var fifteen = Top3k("Cab_HG215C_57_Cone");
                var tens = new[] { "Cab_TW410C_57_Cone", "Cab_TW110C_57_Cone" }.Select(Top3k).ToArray();
                return Result("guitar-15-darker-than-10", fifteen < tens.Min(),
                    $"3-6 kHz vs midband: 2x15 {fifteen:F1}, 10-inch {Join(tens)} dB");
            }),
        new("pa-horn-keeps-the-top",
            "Same clean amp: the PA 15 + Horn carries more 8-12 kHz than every close-miked guitar or bass cabinet of #74, and the PA Column more 5-10 kHz than the 1x10 Combo.",
            "Jensen V-11 compression driver (crossed at its recommended 2.5 kHz) holds +20 dB above its own midrange to 20 kHz; the MOD 5-30 5-inch full-range rises 7-11 dB at 6-8 kHz; a 10-inch P10R falls 7 dB by 8 kHz.",
            render =>
            {
                double Band(string cab, double lo, double hi) => ToneMetrics.BandMean(render("clean:" + cab, Convert("Amp_TW22", cab)).Measurement.BandDb, lo, hi);
                var pa = Band("Cab_PA1152C", 8000, 12000);
                var others = new[] { "Cab_TW410C_57_Cone", "Cab_TW110C_57_Cone", "Cab_HG215C_57_Cone", "Bass_Cab_CH210BC_57_Cone", "Bass_Cab_TW215BC_57_Cone" }
                    .Select(c => Band(c, 8000, 12000)).ToArray();
                var column = Band("Cab_PA999C", 5000, 10000);
                var combo = Band("Cab_TW110C_57_Cone", 5000, 10000);
                return Result("pa-horn-keeps-the-top", pa > others.Max() && column > combo,
                    $"8-12 kHz: PA 15 + Horn {pa:F1}, close-miked {Join(others)} dB; 5-10 kHz: PA Column {column:F1}, 1x10 {combo:F1} dB");
            }),
        new("hifi-tower-full-range",
            "Same clean amp: the Hi-Fi Tower reaches lower (40-63 Hz) than the PA Column and keeps more 8-12 kHz than the 1x10 Combo.",
            "KEF R7 Meta: +/-3 dB 48 Hz-28 kHz (bass reflex); Jensen MOD 5-30: Fs 125 Hz; P10R: -7 dB at 8 kHz.",
            render =>
            {
                double Band(string cab, double lo, double hi) => ToneMetrics.BandMean(render("clean:" + cab, Convert("Amp_TW22", cab)).Measurement.BandDb, lo, hi);
                var hifiLow = Band("Cab_Audiophile", 40, 63);
                var columnLow = Band("Cab_PA999C", 40, 63);
                var hifiTop = Band("Cab_Audiophile", 8000, 12000);
                var comboTop = Band("Cab_TW110C_57_Cone", 8000, 12000);
                return Result("hifi-tower-full-range", hifiLow > columnLow && hifiTop > comboTop,
                    $"40-63 Hz: Hi-Fi {hifiLow:F1}, PA Column {columnLow:F1} dB; 8-12 kHz: Hi-Fi {hifiTop:F1}, 1x10 {comboTop:F1} dB");
            }),
        new("gain-knob-monotonic",
            "On one amp, raising the RS Gain knob from 1 to 75 never lowers the distortion, 1 -> 100 at least doubles it, and the top step keeps at least 90 % of it.",
            "The knob's definition (preamp gain). An amp's chain may also move a filter on the same knob, which may shave the top step a little.",
            render =>
            {
                var thd = new[] { 1, 25, 50, 75, 100 }
                    .Select(g => render($"gain:{g}", Convert(Amp, GuitarCabStem + "_57_Cone", Pots(("Gain", g)))).Drive.ThdPercent)
                    .ToArray();
                var ok = thd.Take(4).Zip(thd.Skip(1).Take(3)).All(p => p.Second >= p.First - 0.5)
                         && thd[^1] >= 2 * thd[0] && thd[^1] >= 0.9 * thd[^2];
                return Result("gain-knob-monotonic", ok, $"THD % at Gain 1/25/50/75/100: {Join(thd)}");
            }),
        new("gain-knob-drives-every-guitar-amp",
            "On every guitar amp whose first knob drives its chain's distortion, that knob at 100 distorts more than at its minimum.",
            "The knob's definition, checked against each amp's chain as the player's install defines it (read at run time): only amps whose first knob (Gain, PreAmp, Volume, ...) reaches a drive stage (RsAmpStage.GuitarDistortion / FutzDistortion) are held to it. Replaced a real-world family order (#88), which some chains do not follow.",
            render =>
            {
                var weak = new List<string>();
                var amps = RsAmpChains.Models.Where(m => m.Key.StartsWith("Amp_", StringComparison.Ordinal) &&
                    (m.Knobs[0].Stages & (RsAmpStage.GuitarDistortion | RsAmpStage.FutzDistortion)) != 0).ToList();
                foreach (var m in amps)
                {
                    var knob = m.Knobs[0];
                    var pot = knob.Key[(m.Key.Length + 1)..];
                    double Thd(float v) => render($"drive:{m.Key}:{v}", Convert(m.Key, GuitarCabStem + "_57_Cone", Pots((pot, v)))).Drive.ThdPercent;
                    var lo = Thd(knob.Min);
                    var hi = Thd(knob.Max);
                    if (!(hi > lo)) weak.Add(string.Create(CultureInfo.InvariantCulture, $"{m.Key} {lo:F1} -> {hi:F1}"));
                }

                return Result("gain-knob-drives-every-guitar-amp", weak.Count == 0,
                    $"{amps.Count - weak.Count} of {amps.Count} amps distort more at the top of their first knob{(weak.Count == 0 ? "" : "; not: " + string.Join(", ", weak))}");
            }),
        new("reverb-decay-follows-knob",
            "A hall reverb's measured tail grows with its Decay knob, and a dry rig has the shortest tail.",
            "The knob's definition (decay time).",
            render =>
            {
                double Tail(string name, float? decay) => render(name, decay is null
                    ? Convert(Amp, GuitarCabStem + "_57_Cone")
                    : Convert(Amp, GuitarCabStem + "_57_Cone",
                        racks: [("Rack_StudioVerb", Pots(("Time", decay.Value), ("Mix", 60)))])).Measurement.TailMs;
                var dry = Tail("tail:dry", null);
                var shortTail = Tail("tail:verb10", 10);
                var longTail = Tail("tail:verb90", 90);
                return Result("reverb-decay-follows-knob", dry < shortTail && shortTail < longTail,
                    $"tail to -30 dB: dry {dry:F0} ms, Decay 10 {shortTail:F0} ms, Decay 90 {longTail:F0} ms");
            }),
        new("treble-booster-tilts-up",
            "Pedal_RangeBooster in front of a clean amp raises the treble (2-5 kHz) relative to the bass (100-250 Hz).",
            "Dallas Rangemaster: a treble booster whose small input capacitor thins the lows and lifts the highs.",
            render =>
            {
                double HighOverLow(ToneRender t) => ToneMetrics.BandMean(t.Measurement.BandDb, 2000, 5000)
                                                    - ToneMetrics.BandMean(t.Measurement.BandDb, 100, 250);
                var without = HighOverLow(render("boost:off", Convert("Amp_TW22", "Cab_TW112C_57_Cone")));
                var with = HighOverLow(render("boost:on", Convert("Amp_TW22", "Cab_TW112C_57_Cone",
                    prePedals: [("Pedal_RangeBooster", Pots(("Boost", 50)))])));
                return Result("treble-booster-tilts-up", with > without + 1.0,
                    $"(2-5 kHz) - (100-250 Hz): without {without:F1}, with {with:F1} dB");
            }),
        new("bitcruncher-filter-knob-up-is-brighter",
            "Pedal_BitCruncher's Filter knob runs the crusher's Lo-Fi filter BACKWARDS: at Filter 1 the 8-16 kHz band is at least 3 dB under Filter 100.",
            "The pedal's knob curve in the player's install, read at run time (#157): turning the Filter knob up takes the Lo-Fi Filter out.",
            render =>
            {
                double Air(int filter) => ToneMetrics.BandMean(render($"crunch:filter{filter}", CrushedBass(filter)).Measurement.BandAbsDb, 8000, 16000);
                var low = Air(1);
                var high = Air(100);
                return Result("bitcruncher-filter-knob-up-is-brighter", low < high - 3,
                    $"8-16 kHz band (absolute): Filter 1 {low:F1}, Filter 100 {high:F1} dB");
            }),
        new("studio-eq-high-mid",
            "Rack_StudioEQ's High-Mid at +12 dB, 2 kHz, raises the 2 kHz band by most of those 12 dB (it is labelled in dB).",
            "The knob's own unit.",
            render =>
            {
                double At2k(float gain) => ToneMetrics.BandAt(render($"eq:{gain}", Convert(Amp, GuitarCabStem + "_57_Cone",
                    racks: [("Rack_StudioEQ", Pots(("HiMid", gain), ("HiMidFreq", 2), ("LoMid", 0)))])).Measurement.BandAbsDb, 2000);
                var flat = At2k(0);
                var boosted = At2k(12);
                return Result("studio-eq-high-mid", boosted - flat > 8,
                    $"2 kHz band (absolute, FFT units): HiMid 0 dB {flat:F1}, HiMid +12 dB {boosted:F1} (rise {boosted - flat:F1} dB)");
            }),

        // ---- issue #68 / #69 / #77: targets on a gear's knob curves as the player's
        // install defines them (read at run time; the targets skip without one). Each
        // claim is about OUR render, and no number of the install's is quoted here.
        new("studio-eq-bass-freq-curve",
            "Rack_StudioEQ Bass +12 dB with the Bass Freq. knob at 260 is a shelf at ~100 Hz: 63 Hz rises by > 6 dB, 250 Hz by < 3 dB.",
            "The knob's curve in the player's install, read at run time, which sweeps the corner in log frequency: a shelf whose corner were the knob's raw value (260 Hz) would lift 250 Hz by ~6 dB.",
            render =>
            {
                var (low, mid) = (Rise(render, "bassfreq", 63, ("Bass", 12f), ("BassFreq", 260f)),
                                  Rise(render, "bassfreq", 250, ("Bass", 12f), ("BassFreq", 260f)));
                return Result("studio-eq-bass-freq-curve", low > 6 && mid < 3,
                    $"rise over flat: 63 Hz {low:F1} dB, 250 Hz {mid:F1} dB");
            }),
        new("studio-eq-treble-reaches-20k",
            "Rack_StudioEQ Treble +12 dB at Treble Freq. 20 kHz leaves 10 kHz nearly flat (< 2 dB); at 2 kHz it lifts 4-8 kHz by > 8 dB.",
            "The knob's own unit (kHz, 2..20 kHz in the gear catalogue), through its curve in the player's install; a corner clamped to 12 kHz would lift 10 kHz by ~4 dB.",
            render =>
            {
                var top = Rise(render, "treble20k", 10000, ("Treble", 12f), ("TrebleFreq", 20f));
                var low = RiseMean(render, "treble2k", 4000, 8000, ("Treble", 12f), ("TrebleFreq", 2f));
                return Result("studio-eq-treble-reaches-20k", top < 2 && low > 8,
                    $"rise over flat: Freq 20 kHz -> 10 kHz band {top:F1} dB; Freq 2 kHz -> 4-8 kHz {low:F1} dB");
            }),
        new("studio-eq-q-switch",
            "Rack_StudioEQ High-Mid +12 dB at 1.7 kHz (a 1.25 kHz bell): one octave above, Wide lifts 3-7.5 dB and Narrow 0.6-2.5 dB.",
            "The switch's two positions in the player's install, read at run time; the ranges are RBJ bells one octave out, wide enough to tell a broad bell from a narrow one and to fail the slider's extremes (Q 0.3 / 8 would give +9.6 / +0.1 dB).",
            render =>
            {
                var wide = Rise(render, "q-wide", 2500, ("HiMid", 12f), ("HiMidFreq", 1.7f), ("HiMidQ", 0f));
                var narrow = Rise(render, "q-narrow", 2500, ("HiMid", 12f), ("HiMidFreq", 1.7f), ("HiMidQ", 1f));
                return Result("studio-eq-q-switch", wide is >= 3 and <= 7.5 && narrow is >= 0.6 and <= 2.5,
                    $"2.5 kHz band rise over flat: Wide {wide:F1} dB, Narrow {narrow:F1} dB");
            }),
        new("compression-attack-follows-knob",
            "Pedal_Compression (loop slot) at Comp 100: a 100 ms attack lets the first 5-40 ms of each pluck through > 3 dB louder (against the note 150-400 ms in) than a 1 ms attack.",
            "The knob's own unit (ms, 1..100 in the gear catalogue) on a compressor, through its curve in the player's install: a slow attack lets the transient through.",
            render =>
            {
                double Onset(float attack) => OnsetOverBody(render($"comp:attack{attack}",
                    CompressedClean(("Comp", 100f), ("Attack", attack), ("Release", 200f))).Output);
                var fast = Onset(1);
                var slow = Onset(100);
                return Result("compression-attack-follows-knob", slow > fast + 3,
                    $"onset (5-40 ms) over body (150-400 ms): Attack 1 ms {fast:F1} dB, Attack 100 ms {slow:F1} dB");
            }),
        new("compression-release-follows-knob",
            "Pedal_Compression (loop slot) at Comp 100: with a 10 ms release the low E's tail (0.6-1.4 s) stays > 3 dB louder against its start (50-300 ms) than with a 1000 ms release.",
            "The knob's own unit (ms, 10..1000 in the gear catalogue), through its curve in the player's install — a slow release lags the decaying note and holds the gain down.",
            render =>
            {
                double Sustain(float release) => SustainDb(render($"comp:release{release}",
                    CompressedClean(("Comp", 100f), ("Attack", 30f), ("Release", release))).Output);
                var fast = Sustain(10);
                var slow = Sustain(1000);
                return Result("compression-release-follows-knob", fast > slow + 3,
                    $"tail (0.6-1.4 s) over start (50-300 ms): Release 10 ms {fast:F1} dB, Release 1000 ms {slow:F1} dB");
            }),
        new("compression-knob-squashes",
            "Pedal_Compression (loop slot): raising the Compression knob 1 -> 50 -> 100 (default attack/release) holds the low E's tail up against its start, monotonically.",
            "The knob's definition (more compression), through its curve in the player's install, read at run time.",
            render =>
            {
                var s = new[] { 1f, 50f, 100f }
                    .Select(c => SustainDb(render($"comp:comp{c}", CompressedClean(("Comp", c), ("Attack", 30f), ("Release", 200f))).Output))
                    .ToArray();
                return Result("compression-knob-squashes", s[0] < s[1] && s[1] < s[2],
                    $"tail over start at Comp 1/50/100: {Join(s)} dB");
            }),
        new("lofi-lo-cut-follows-knob",
            "Pedal_LoFiFilter with Hi Cut at 1: turning Lo Cut 1 -> 100 drops 100-250 Hz against 1-2 kHz by > 10 dB.",
            "The knob's definition (a low cut), through its curve in the player's install, read at run time.",
            render =>
            {
                double Shape(float loCut) => BandSpan(render($"lofi:locut{loCut}", LoFi(1, loCut)), 100, 250, 1000, 2000);
                var open = Shape(1);
                var cut = Shape(100);
                return Result("lofi-lo-cut-follows-knob", open - cut > 10,
                    $"(100-250 Hz) - (1-2 kHz): Lo Cut 1 {open:F1} dB, Lo Cut 100 {cut:F1} dB");
            }),
        new("lofi-hi-cut-follows-knob",
            "Pedal_LoFiFilter with Lo Cut at 1: turning Hi Cut 1 -> 100 drops 4-8 kHz against 500 Hz-1 kHz by > 10 dB.",
            "The knob's definition (a high cut: up is MORE cut), through its curve in the player's install, read at run time.",
            render =>
            {
                double Shape(float hiCut) => BandSpan(render($"lofi:hicut{hiCut}", LoFi(hiCut, 1)), 4000, 8000, 500, 1000);
                var open = Shape(1);
                var cut = Shape(100);
                return Result("lofi-hi-cut-follows-knob", open - cut > 10,
                    $"(4-8 kHz) - (500 Hz-1 kHz): Hi Cut 1 {open:F1} dB, Hi Cut 100 {cut:F1} dB");
            }),
        new("bass-emulator-octave-down",
            "Pedal_BassEmulator in front of a bass rig moves the E4 note an octave down: over that note, 164.8 Hz gains > 20 dB on 329.6 Hz.",
            "The pedal's definition (a bass an octave below the guitar), through its block in the player's install, read at run time.",
            render =>
            {
                // The E4 note alone (1.6-2.9 s of the DI): over the whole render the
                // low E and the chord's E3 already fill 160 Hz.
                double Octave(ToneRender r) => ToneDb(r.Output, Ms(1600), Ms(2900), 164.81) - ToneDb(r.Output, Ms(1600), Ms(2900), 329.63);
                var without = Octave(render("bassemu:off", Convert("Bass_Amp_EdenWT550", "Bass_Cab_AT810BC_57_Cone")));
                var with = Octave(render("bassemu:on", Convert("Bass_Amp_EdenWT550", "Bass_Cab_AT810BC_57_Cone",
                    prePedals: [("Pedal_BassEmulator", null)])));
                return Result("bass-emulator-octave-down", with - without > 20,
                    $"E4 note, (164.8 Hz) - (329.6 Hz): without {without:F1} dB, with {with:F1} dB");
            }),

        // ---- #88: per-amp targets on the amps' own chains ----
        // The chains are read from the player's install into the native rsamp table
        // at run time (the targets skip without one), and no number of theirs is
        // quoted here: each claim is a direction and a generous floor on OUR render.
        // Each is a DIFFERENCE of two renders with one knob moved, so the DI, the cab
        // and the rest of the chain cancel.
        new("or50h-punch-is-its-band",
            "Orange OR50H's Punch 1 -> 100 raises the 10 kHz band by more than 12 dB.",
            "The knob's definition (a treble 'punch' boost), through the amp's chain in the player's install. Folded until #88.",
            render =>
            {
                double At10k(float punch) => ToneMetrics.BandAt(render($"or50h-punch:{punch}", Convert("Amp_OrangeOR50H", GuitarCabStem + "_57_Cone",
                    Pots(("Gain", 1), ("Punch", punch)))).Measurement.BandAbsDb, 10000);
                var rise = At10k(100) - At10k(1);
                return Result("or50h-punch-is-its-band", rise > 12.0, $"10 kHz band rise: {rise:F1} dB");
            }),
        new("hg500-mid-frequency-moves-the-cut",
            "HG-500's Mid-Frequency, with Mid at its full cut, moves the cut from the bottom of its range to the top: (160-250 Hz) - (2.5-3.15 kHz) rises by more than 8 dB.",
            "The knob's definition (the mid control's centre), through the amp's chain in the player's install. Folded until #88.",
            render =>
            {
                double Tilt(float mf) => Shape(render($"hg500-mf:{mf}", Convert("Amp_HG500", GuitarCabStem + "_57_Cone",
                    Pots(("Gain", 1), ("Mid", 1), ("MidFreq", mf)))), 160, 250, 2500, 3150);
                var low = Tilt(1);
                var high = Tilt(100);
                return Result("hg500-mid-frequency-moves-the-cut", high > low + 8.0,
                    $"(160-250 Hz) - (2.5-3.15 kHz): Mid-Frequency 1 {low:F1}, 100 {high:F1} dB");
            }),
        new("clh300b-enhance-is-a-smile",
            "CLH-300B's Enhance 1 -> 100 lifts the 8 kHz band over the 500 Hz band by more than 10 dB.",
            "The knob's definition (a 'smile' enhance: treble up, low mids down), through the amp's chain in the player's install. Folded until #88.",
            render =>
            {
                double Smile(float e) => Shape(render($"ch300b-enh:{e}", Convert("Bass_Amp_CH300B", BassCab,
                    Pots(("Gain", 1), ("Enhance", e)))), 6300, 10000, 400, 630);
                var rise = Smile(100) - Smile(1);
                return Result("clh300b-enhance-is-a-smile", rise > 10.0,
                    $"(6.3-10 kHz) - (400-630 Hz) change: {rise:F1} dB");
            }),
        new("bright-switch-lifts-treble",
            "EN-50's Bright switch at a moderate gain lifts 8-16 kHz over 0.8-1.25 kHz by more than 3 dB.",
            "The switch's definition (a bright switch), through the amp's chain in the player's install (#88; until then an AB763 bright cap stood in).",
            render =>
            {
                double Tilt(int bright) => Shape(render($"bright:{bright}", Convert("Amp_EN50", "Cab_EN4120C_57_Cone",
                    Pots(("Gain", 30), ("Bass", 50), ("Mid", 50), ("Treble", 50), ("Bright", bright)))), 8000, 16000, 800, 1250);
                var off = Tilt(0);
                var on = Tilt(1);
                return Result("bright-switch-lifts-treble", on > off + 3.0,
                    $"(8-16 kHz) - (0.8-1.25 kHz): Bright off {off:F1}, on {on:F1} dB");
            }),
        new("dsl-resonance-lifts-lows",
            "Marshall DSL100H's Resonance at 100 against 1 lifts 63-125 Hz over 1 kHz by more than 3 dB.",
            "The knob's definition (a power-amp resonance control: more low end), through the amp's chain in the player's install (#88; until then the rig's poweramp resonance shelf stood in).",
            render =>
            {
                double Low(float res) => Shape(render($"res:{res}", Convert("Amp_MarshallDSL100H", GuitarCabStem + "_57_Cone",
                    Pots(("Gain", 20), ("Res", res)))), 63, 125, 800, 1250);
                var lo = Low(1);
                var hi = Low(100);
                return Result("dsl-resonance-lifts-lows", hi > lo + 3.0,
                    $"(63-125 Hz) - (0.8-1.25 kHz): Resonance 1 {lo:F1}, Resonance 100 {hi:F1} dB");
            }),
        new("hw300b-high-shift",
            "HW-300B's High-Shift raises the 8 kHz band by more than 4 dB.",
            "The switch's definition (a treble shift), through the amp's chain in the player's install, run on the native rsamp module.",
            render =>
            {
                double At8k(int on) => ToneMetrics.BandAt(render($"hw300b-hi:{on}", Convert(Hw300bKey, BassCab,
                    Pots(("Gain", 1), ("HiShift", on), ("LoShift", 0)))).Measurement.BandAbsDb, 8000);
                var rise = At8k(1) - At8k(0);
                return Result("hw300b-high-shift", rise > 4.0, $"8 kHz band rise: {rise:F1} dB");
            }),
        new("hw300b-low-shift",
            "HW-300B's Low-Shift cuts the 500 Hz band by more than 4 dB.",
            "The switch's definition (a low shift: deep lows up, low mids down), through the amp's chain in the player's install. The reference DI is a guitar and carries too little deep bass to measure the boost there; JuceSharp's GearRsAmpTests pins the chain maths on synthetic tables.",
            render =>
            {
                double At500(int on) => ToneMetrics.BandAt(render($"hw300b-lo:{on}", Convert(Hw300bKey, BassCab,
                    Pots(("Gain", 1), ("LoShift", on), ("HiShift", 0)))).Measurement.BandAbsDb, 500);
                var cut = At500(1) - At500(0);
                return Result("hw300b-low-shift", cut < -4.0, $"500 Hz band change: {cut:F1} dB");
            }),
        new("hw300b-mid-selector",
            "HW-300B's Mid-Frequency selector moves its Mid boost: at Mid 100, position 1 (its lowest centre) against position 5 (its highest) raises the 200-315 Hz bands over 2.5-3.15 kHz by more than 10 dB.",
            "The selector's definition (stepped mid centres, low to high), through the amp's chain in the player's install, on the native rsamp module.",
            render =>
            {
                double Tilt(int pos) => Shape(render($"hw300b-mid:{pos}", Convert(Hw300bKey, BassCab,
                    Pots(("Gain", 1), ("Mid", 100), ("MidFreq", pos), ("LoShift", 0), ("HiShift", 0)))), 200, 315, 2500, 3150);
                var at220 = Tilt(0);
                var at3k = Tilt(4);
                return Result("hw300b-mid-selector", at220 > at3k + 10.0,
                    $"(200-315 Hz) - (2.5-3.15 kHz): selector position 1 {at220:F1}, position 5 {at3k:F1} dB");
            }),
        new("semi-parametric-mid-moves",
            "BH-880B's Mid at +12 dB follows its Mid Frequency knob: at 2 kHz against 200 Hz it raises the 1.6-2.5 kHz bands over 160-250 Hz by more than 10 dB.",
            "The knob's own unit (Hz) and the BH-880B's catalogue range (Mid Frequency 200-2000 Hz).",
            render =>
            {
                double Tilt(float hz) => Shape(render($"pmid:{hz}", Convert("Bass_Amp_BT880B", BassCab,
                    Pots(("Gain", 1), ("Mid", 12), ("MidFreq", hz), ("Lo", 0), ("Hi", 0)))), 1600, 2500, 160, 250);
                var low = Tilt(200);
                var high = Tilt(2000);
                return Result("semi-parametric-mid-moves", high > low + 10.0,
                    $"(1.6-2.5 kHz) - (160-250 Hz): Mid Frequency 200 Hz {low:F1}, 2 kHz {high:F1} dB");
            })
    ];

    /// <summary>The level (dB, arbitrary reference) of one frequency over
    /// [<paramref name="from" />, <paramref name="to" />): a Hann-windowed
    /// single-bin DFT, so a pitch that is not a whole number of cycles in the
    /// window does not leak into its neighbours.</summary>
    private static double ToneDb(float[] y, int from, int to, double hz)
    {
        var n = to - from;
        var w = 2 * Math.PI * hz / ReferenceDi.SampleRate;
        double re = 0, im = 0;
        for (var i = 0; i < n; i++)
        {
            var win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            re += win * y[from + i] * Math.Cos(w * i);
            im -= win * y[from + i] * Math.Sin(w * i);
        }

        return 10 * Math.Log10(re * re + im * im + 1e-30);
    }

    // ---------------------------------------------------------------- helpers

    private static DcRigPreset StudioEq(params (string Pot, float Value)[] pots)
        => Convert(Amp, GuitarCabStem + "_57_Cone", racks: [("Rack_StudioEQ", Pots(pots))]);

    /// <summary>Absolute band level with the EQ set minus the same render with the
    /// EQ flat (every gain 0): the EQ is a RACK, after the cab, so the difference is
    /// the EQ's own response.</summary>
    private static double Rise(Func<string, DcRigPreset, ToneRender> render, string name, double hz, params (string Pot, float Value)[] pots)
    {
        var flat = render("eq:flat", StudioEq(("Bass", 0f), ("LoMid", 0f), ("HiMid", 0f), ("Treble", 0f))).Measurement.BandAbsDb;
        var set = render("eq:" + name, StudioEq(pots)).Measurement.BandAbsDb;
        return ToneMetrics.BandAt(set, hz) - ToneMetrics.BandAt(flat, hz);
    }

    private static double RiseMean(Func<string, DcRigPreset, ToneRender> render, string name, double loHz, double hiHz, params (string Pot, float Value)[] pots)
    {
        var flat = render("eq:flat", StudioEq(("Bass", 0f), ("LoMid", 0f), ("HiMid", 0f), ("Treble", 0f))).Measurement.BandAbsDb;
        var set = render("eq:" + name, StudioEq(pots)).Measurement.BandAbsDb;
        return ToneMetrics.BandMean(set, loHz, hiHz) - ToneMetrics.BandMean(flat, loHz, hiHz);
    }

    /// <summary>Pedal_Compression in a LOOP slot (after a clean Blackface, Amp_TW22,
    /// and its cab): nothing nonlinear follows it, so what is measured is the
    /// compressor's own ballistics. In front of the amp its +12 dB make-up at
    /// Comp 100 drives the amp into a saturation that flattens them.</summary>
    private static DcRigPreset CompressedClean(params (string Pot, float Value)[] pots)
        => Convert("Amp_TW22", "Cab_TW112C_57_Cone", postPedals: [("Pedal_Compression", Pots(pots))]);

    private static DcRigPreset LoFi(float hiCut, float loCut)
        => Convert("Amp_TW22", "Cab_TW112C_57_Cone", prePedals: [("Pedal_LoFiFilter", Pots(("FilterType", hiCut), ("Mix", loCut)))]);

    private static double BandSpan(ToneRender r, double aLo, double aHi, double bLo, double bHi)
        => ToneMetrics.BandMean(r.Measurement.BandAbsDb, aLo, aHi) - ToneMetrics.BandMean(r.Measurement.BandAbsDb, bLo, bHi);

    /// <summary>The DI's two single notes start at 0 s (E2) and 1.5 s (E4): the mean
    /// over both of RMS(5-40 ms) - RMS(150-400 ms) after each onset.</summary>
    private static double OnsetOverBody(float[] y)
    {
        double One(int onset) => SegmentRmsDb(y, onset + Ms(5), onset + Ms(40)) - SegmentRmsDb(y, onset + Ms(150), onset + Ms(400));
        return (One(0) + One(Ms(1500))) / 2;
    }

    /// <summary>The low E's decay held against its start: RMS(0.6-1.4 s) - RMS(50-300 ms).</summary>
    private static double SustainDb(float[] y) => SegmentRmsDb(y, Ms(600), Ms(1400)) - SegmentRmsDb(y, Ms(50), Ms(300));

    private static int Ms(double ms) => (int)(ms * ReferenceDi.SampleRate / 1000.0);

    private static double SegmentRmsDb(float[] y, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to; i++) sum += (double)y[i] * y[i];
        return 10 * Math.Log10(sum / Math.Max(1, to - from) + 1e-30);
    }

    private const string Hw300bKey = "Bass_Amp_HT300B";
    private const string BassCab = "Bass_Cab_AT810BC_57_Cone";

    /// <summary>(lo..hi) minus (refLo..refHi), in dB, off one render's shape.</summary>
    private static double Shape(ToneRender r, double lo, double hi, double refLo, double refHi)
        => ToneMetrics.BandMean(r.Measurement.BandAbsDb, lo, hi) - ToneMetrics.BandMean(r.Measurement.BandAbsDb, refLo, refHi);

    /// <summary>A bass head and cab, then a Bit Cruncher (a crushed, mostly wet
    /// setting) at Filter <paramref name="filter" />.</summary>
    private static DcRigPreset CrushedBass(int filter) => Convert("Bass_Amp_CH350B", "Bass_Cab_BT212BC_Tube_Cone",
        postPedals: [("Pedal_BitCruncher", Pots(("FilterType", 4), ("Release", 14), ("Mix", 63), ("Attack", filter), ("Sens", 1)))]);

    private static double Air(Func<string, DcRigPreset, ToneRender> render, string mic, string pos)
        => ToneMetrics.BandMean(Mic(render, mic, pos).Measurement.BandDb, 8000, 16000);

    private static double Top(Func<string, DcRigPreset, ToneRender> render, string mic, string pos)
        => ToneMetrics.BandMean(Mic(render, mic, pos).Measurement.BandDb, 4000, 16000);

    private static double Presence(Func<string, DcRigPreset, ToneRender> render, string mic)
    {
        var bands = Mic(render, mic, "Cone").Measurement.BandDb;
        return ToneMetrics.BandMean(bands, 4000, 6300) - ToneMetrics.BandMean(bands, 1000, 2000);
    }

    private static ToneRender Mic(Func<string, DcRigPreset, ToneRender> render, string mic, string pos)
        => render($"mic:{mic}/{pos}", Convert(Amp, $"{GuitarCabStem}_{mic}_{pos}"));

    private static TargetResult Result(string id, bool pass, FormattableString measured)
        => new(id, pass, FormattableString.Invariant(measured));

    private static string Join(double[] v) => string.Join("/", v.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)));
}
