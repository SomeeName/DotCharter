namespace DotCharter.Tools.RigDataset.Beds;

/// <summary>
///     Turns a run seed and a bed index into a <see cref="BedPlan" />, with no audio
///     and no engine. Pure, so the whole musical half of the bed generator is in the
///     default gate.
///     <para>
///         <b>Every bed is ours.</b> Nothing here reads a sample, an IR, a loop or a
///         MIDI file: the notes are drawn from a key and a progression, and the sound
///         is the app's own DSP. That is not an aesthetic choice — MUSDB18-HQ is
///         "educational purposes only" and MoisesDB is CC BY-NC-SA, so a bed taken
///         from either poisons a corpus that is meant to train a model that SHIPS.
///     </para>
///     <para>
///         <b>Rig params are filtered through an <see cref="IParamRanges" />.</b>
///         A layer's chain is declared as <c>(module, param, 0…1)</c> and a param the
///         catalogue does not have is DROPPED rather than pushed — the engine would
///         reject it silently and the sidecar would then claim a knob that was never
///         set. The ranges are a PARAMETER and not a static lookup, so this whole
///         file stays pure and testable with no native DLL.
///     </para>
/// </summary>
public static class BedPlanner
{
    /// <summary>Default bed count. Thirty is enough to keep a bed from being
    /// memorised at a 40 % separate fraction over 100k clips (~40 bed windows per
    /// bed, all at different offsets, tempos and levels), and small enough that the
    /// whole folder is a few hundred megabytes.</summary>
    public const int DefaultCount = 30;

    /// <summary>Tempos, cycled by bed index so the set is covered evenly however
    /// many beds are asked for.</summary>
    private static readonly double[] Tempos = [72, 84, 92, 100, 110, 120, 132, 146];

    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly int[] MajorScale = [0, 2, 4, 5, 7, 9, 11];
    private static readonly int[] MinorScale = [0, 2, 3, 5, 7, 8, 10];

    // Degrees into the scale, one per bar. Ordinary, singable four-bar loops: the
    // bed has to sound like something a guitar would be played over, because the
    // separator's behaviour on a plausible mix is the thing being modelled.
    private static readonly int[][] MajorProgressions =
        [[0, 4, 5, 3], [0, 3, 4, 0], [0, 5, 3, 4], [0, 3, 0, 4]];

    private static readonly int[][] MinorProgressions =
        [[0, 5, 2, 6], [0, 3, 4, 0], [0, 6, 5, 4], [0, 2, 5, 4]];

    /// <summary>Scale steps of a triad, plus the optional seventh.</summary>
    private static readonly int[] TriadSteps = [0, 2, 4];

    /// <summary>Builds the plan for one bed. Deterministic in
    /// <paramref name="runSeed" /> and <paramref name="index" /> alone.</summary>
    /// <param name="runSeed">The run's seed.</param>
    /// <param name="index">The bed's index.</param>
    /// <param name="ranges">The param catalogue the layer rigs are filtered against.</param>
    /// <param name="sampleRate">48 000.</param>
    public static BedPlan Plan(int runSeed, int index, IParamRanges ranges, int sampleRate = 48_000)
    {
        var seed = RenderCommand.SeedFor(runSeed, index, attempt: 0);
        var rng = new Random(seed);

        var tempo = Tempos[index % Tempos.Length];
        var minor = index % 2 == 1;
        var root = (index * 5) % 12; // circle of fourths: all twelve in twelve beds
        var scale = minor ? MinorScale : MajorScale;
        var progression = (minor ? MinorProgressions : MajorProgressions)[rng.Next(4)];

        const int beatsPerBar = 4;
        var seconds = 60 + rng.Next(61); // 60…120 s
        var totalBeats = seconds * tempo / 60.0;
        var bars = (int)Math.Ceiling(totalBeats / beatsPerBar);

        var layers = new List<BedLayerPlan>(3)
        {
            new(BedVoices.Drums, BedVoices.Drums, DrumNotes(rng, bars, beatsPerBar), DrumRig(rng, ranges),
                LevelDb(rng, -3f)),
            new(BedVoices.Bass, BedVoices.Bass, BassNotes(rng, bars, beatsPerBar, root, scale, progression),
                BassRig(rng, ranges), LevelDb(rng, -5f)),
            new(BedVoices.Keys, BedVoices.Keys, KeyNotes(rng, bars, beatsPerBar, root, scale, progression),
                KeysRig(rng, ranges), LevelDb(rng, -10f))
        };

        return new BedPlan(index, seed, seconds, sampleRate, tempo, 48 + root, NoteNames[root],
            minor ? "minor" : "major", progression, beatsPerBar, layers);
    }

    private static float LevelDb(Random rng, float centre)
        => centre + (float)(rng.NextDouble() * 4d - 2d);

    // ------------------------------------------------------------------ notes

    private static IReadOnlyList<BedNote> BassNotes(Random rng, int bars, int beatsPerBar, int root,
        int[] scale, int[] progression)
    {
        var pattern = rng.Next(4);
        var notes = new List<BedNote>(bars * 8);
        for (var bar = 0; bar < bars; bar++)
        {
            var degree = progression[bar % progression.Length];
            var rootMidi = 28 + root + scale[degree % scale.Length] + 12 * (degree / scale.Length);
            var fifth = rootMidi + 7;
            var b0 = bar * beatsPerBar;
            switch (pattern)
            {
                case 0: // whole notes
                    notes.Add(new BedNote(b0, beatsPerBar, rootMidi, 0.9f));
                    break;
                case 1: // driving eighths
                    for (var i = 0; i < beatsPerBar * 2; i++)
                        notes.Add(new BedNote(b0 + i * 0.5, 0.45, rootMidi, i % 2 == 0 ? 0.95f : 0.7f));
                    break;
                case 2: // root–fifth quarters
                    for (var i = 0; i < beatsPerBar; i++)
                        notes.Add(new BedNote(b0 + i, 0.9, i % 2 == 0 ? rootMidi : fifth, 0.85f));
                    break;
                default: // syncopated
                    notes.Add(new BedNote(b0 + 0.0, 0.7, rootMidi, 0.95f));
                    notes.Add(new BedNote(b0 + 0.75, 0.7, rootMidi, 0.7f));
                    notes.Add(new BedNote(b0 + 1.5, 0.45, fifth, 0.75f));
                    notes.Add(new BedNote(b0 + 2.0, 0.9, rootMidi, 0.9f));
                    notes.Add(new BedNote(b0 + 3.0, 0.45, rootMidi - 12 + 10, 0.6f));
                    notes.Add(new BedNote(b0 + 3.5, 0.45, fifth, 0.65f));
                    break;
            }
        }

        return notes;
    }

    private static IReadOnlyList<BedNote> KeyNotes(Random rng, int bars, int beatsPerBar, int root,
        int[] scale, int[] progression)
    {
        var seventh = rng.NextDouble() < 0.45;
        var notes = new List<BedNote>(bars * 4);
        for (var bar = 0; bar < bars; bar++)
        {
            var degree = progression[bar % progression.Length];
            var b0 = bar * beatsPerBar;
            foreach (var step in TriadSteps)
            {
                var d = degree + step;
                notes.Add(new BedNote(b0, beatsPerBar - 0.1,
                    52 + root + scale[d % scale.Length] + 12 * (d / scale.Length), 0.55f));
            }

            if (!seventh) continue;
            var d7 = degree + 6;
            notes.Add(new BedNote(b0, beatsPerBar - 0.1,
                52 + root + scale[d7 % scale.Length] + 12 * (d7 / scale.Length), 0.35f));
        }

        return notes;
    }

    /// <summary>Kick 36, snare 38, closed hat 42, open hat 46 — General-MIDI keys, so
    /// the sidecar reads like a drum part and not like a table of magic ints.</summary>
    private static IReadOnlyList<BedNote> DrumNotes(Random rng, int bars, int beatsPerBar)
    {
        var pattern = rng.Next(4);
        var notes = new List<BedNote>(bars * 16);
        for (var bar = 0; bar < bars; bar++)
        {
            var b0 = bar * beatsPerBar;
            switch (pattern)
            {
                case 0: // straight
                    notes.Add(new BedNote(b0 + 0, 0.25, 36, 1.0f));
                    notes.Add(new BedNote(b0 + 2, 0.25, 36, 0.9f));
                    break;
                case 1: // eighth-note kick
                    notes.Add(new BedNote(b0 + 0, 0.25, 36, 1.0f));
                    notes.Add(new BedNote(b0 + 1.5, 0.25, 36, 0.8f));
                    notes.Add(new BedNote(b0 + 2, 0.25, 36, 0.9f));
                    notes.Add(new BedNote(b0 + 3.5, 0.25, 36, 0.75f));
                    break;
                case 2: // half time
                    notes.Add(new BedNote(b0 + 0, 0.25, 36, 1.0f));
                    break;
                default: // four on the floor
                    for (var i = 0; i < beatsPerBar; i++)
                        notes.Add(new BedNote(b0 + i, 0.25, 36, i == 0 ? 1.0f : 0.85f));
                    break;
            }

            var snareBeats = pattern == 2 ? new[] { 2.0 } : [1.0, 3.0];
            foreach (var s in snareBeats) notes.Add(new BedNote(b0 + s, 0.25, 38, 0.95f));

            var hatStep = pattern == 2 ? 1.0 : 0.5;
            for (var t = 0.0; t < beatsPerBar; t += hatStep)
            {
                var open = Math.Abs(t - (beatsPerBar - hatStep)) < 1e-9 && rng.NextDouble() < 0.5;
                notes.Add(new BedNote(b0 + t, open ? 0.4 : 0.12, open ? 46 : 42,
                    Math.Abs(t % 1.0) < 1e-9 ? 0.55f : 0.38f));
            }
        }

        return notes;
    }

    // ------------------------------------------------------------------- rigs

    private static BedRigModule? Module(string typeId, Random rng, IParamRanges ranges,
        params (string Param, double Unit)[] knobs)
    {
        var p = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (param, unit) in knobs)
        {
            // The jitter is drawn WHETHER OR NOT the knob exists, so a catalogue that
            // gains or loses one param does not reshuffle every later draw in the bed.
            var jittered = Math.Clamp(unit + (rng.NextDouble() * 0.1d - 0.05d), 0d, 1d);
            var value = ranges.FromUnit(typeId, param, jittered);
            if (value is not null) p[param] = value.Value;
        }

        return p.Count == 0 ? null : new BedRigModule(typeId, p);
    }

    private static IReadOnlyList<BedRigModule> Chain(Random rng, IParamRanges ranges,
        (string TypeId, (string Param, double Unit)[] Knobs)[] spec)
        => spec.Select(s => Module(s.TypeId, rng, ranges, s.Knobs)).OfType<BedRigModule>().ToArray();

    // Units are 0…1 into each knob's CATALOGUE range, never engine values: this file
    // is not allowed to know that `bassamp.bass` is ±12 dB and `compressor.ratio`
    // is 1…20, because that would be the third copy of the range table.
    private static readonly (string TypeId, (string Param, double Unit)[] Knobs)[] BassChain =
    [
        ("compressor",
        [
            ("thresholdDb", 0.55), ("ratio", 0.16), ("attackMs", 0.10), ("releaseMs", 0.15),
            ("makeupDb", 0.25), ("kneeDb", 0.30)
        ]),
        ("bassamp",
        [
            ("model", 0.33), ("gain", 0.45), ("bass", 0.65), ("lowMid", 0.50), ("hiMid", 0.45),
            ("treble", 0.55), ("drive", 0.30), ("compress", 0.40), ("master", 0.60)
        ])
    ];

    private static readonly (string TypeId, (string Param, double Unit)[] Knobs)[] DrumChain =
    [
        ("transient", [("attackDb", 0.66), ("sustainDb", 0.45), ("sensitivity", 0.50), ("mix", 0.90)]),
        ("multiband",
        [
            ("lowXoverHz", 0.30), ("highXoverHz", 0.40),
            ("lowThresholdDb", 0.60), ("lowRatio", 0.16), ("lowGainDb", 0.55),
            ("midThresholdDb", 0.60), ("midRatio", 0.12), ("midGainDb", 0.50),
            ("highThresholdDb", 0.60), ("highRatio", 0.12), ("highGainDb", 0.50),
            ("attackMs", 0.08), ("releaseMs", 0.20)
        ]),
        ("reverb", [("roomSize", 0.25), ("damping", 0.60), ("width", 0.70), ("mix", 0.10)])
    ];

    private static readonly (string TypeId, (string Param, double Unit)[] Knobs)[] KeysChain =
    [
        ("chorus",
        [
            ("rateHz", 0.20), ("depthMs", 0.35), ("mix", 0.45), ("voices", 0.50),
            ("feedback", 0.20), ("spread", 0.70), ("lfoShape", 0.0), ("wetHpfHz", 0.30)
        ]),
        ("hallreverb",
        [
            ("space", 0.40), ("decayS", 0.15), ("predelayMs", 0.15), ("dampingKHz", 0.40),
            ("lowCutHz", 0.30), ("erLevel", 0.50), ("diffusion", 0.70), ("width", 0.80), ("mix", 0.35)
        ]),
        ("tilt", [("tilt", 0.45), ("pivotHz", 0.30), ("level", 0.50)])
    ];

    /// <summary>
    ///     Every <c>(module, param)</c> the bed rigs ask for.
    ///     <para>
    ///         <b>This is what makes a dropped knob visible.</b> A param id that the
    ///         engine catalogue does not have is silently skipped by
    ///         <see cref="Module" /> — which is the right runtime behaviour and a
    ///         terrible silent failure, because the bed then renders through a module
    ///         at its DEFAULTS and nothing says so. The gate lints this list against
    ///         the real catalogue, so a renamed param is a red test rather than a
    ///         quietly duller bed.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<(string Module, string Param)> DeclaredKnobs { get; } =
        new[] { BassChain, DrumChain, KeysChain }
            .SelectMany(c => c)
            .SelectMany(m => m.Knobs.Select(k => (m.TypeId, k.Param)))
            .ToArray();

    private static IReadOnlyList<BedRigModule> BassRig(Random rng, IParamRanges ranges)
        => Chain(rng, ranges, BassChain);

    private static IReadOnlyList<BedRigModule> DrumRig(Random rng, IParamRanges ranges)
        => Chain(rng, ranges, DrumChain);

    private static IReadOnlyList<BedRigModule> KeysRig(Random rng, IParamRanges ranges)
        => Chain(rng, ranges, KeysChain);
}
