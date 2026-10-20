namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     The reference DI every candidate rig is rendered over: a deterministic,
///     synthesised electric-guitar direct take, generated from code.
///     <para>
///         <b>Why synthesised, and why that is a licence decision rather than a
///         laziness one.</b> A match compares a guitar stem off a record against a
///         rig rendered over SOME dry take — we never have the record's own DI. That
///         take has to ship with the app and with the eval, so it has to be
///         redistributable. The licensed electric-DI corpora are not: IDMT-SMT-Guitar
///         is CC BY-NC-ND (no commercial use, and the ND clause forbids
///         redistributing our renders of it), EGDB's licence is unstated, MUSDB and
///         MoisesDB are NC. EGFxSet and GuitarSet are CC BY 4.0 and would be usable
///         with attribution — but EGFxSet is isolated single notes (no chords, no
///         performance) and GuitarSet is an ACOUSTIC guitar through a hex pickup,
///         which is the wrong instrument for a tone the player will hear through an
///         amp. Generating the take makes the licence question disappear, commits
///         ZERO bytes of audio, and gives something none of those corpora do:
///         bit-identical content on every machine, so two eval runs are comparable.
///     </para>
///     <para>
///         <b>What it is not.</b> It is a Karplus-Strong pluck, not a guitar. It has
///         no fret noise, no pick scrape, no player. The one property the metric
///         actually needs is that it excites the range a guitar excites with a
///         plausible harmonic series and a plausible envelope, so that a drive has
///         something to clip and a cab has something to filter — and for that it is
///         sufficient. Where a real DI matters (measuring how much the reference-DI
///         choice moves a score), point the eval at one with
///         <c>DOTCHARTER_AUDIO_EVAL_DI</c>; the two IDMT clips already on the owner's
///         box are fine for that IN-HOUSE use, which is what CC BY-NC-ND allows.
///     </para>
///     <para>
///         Deterministic by construction: a fixed-seed LCG for the excitation, no
///         floating-point accumulation across notes that could depend on order, and
///         no clock, environment or culture anywhere. The same call always returns the
///         same samples.
///     </para>
/// </summary>
public static class ReferenceDi
{
    /// <summary>The generator's identity, for a report header and for invalidating a
    /// pre-rendered library. <b>Bump it for any change to the phrase or the
    /// synthesis</b> — every stored render was made over this exact audio.</summary>
    public const string Id = "refdi-v1";

    /// <summary>Generation rate; also <see cref="RigRenderer.RenderRate" />.</summary>
    public const int SampleRate = 48_000;

    /// <summary>
    ///     The phrase: low open-position power chords, a single-note line over the
    ///     same key, and a full chord to end on. Chosen so that one 12-second take
    ///     covers the low fundamentals a cab's rolloff acts on, the midrange a drive
    ///     compresses, and the sustained tail a reverb or delay needs.
    /// </summary>
    public static MonoAudio Generate() => Generate(0);

    /// <summary>
    ///     <paramref name="variant" /> 0 is <see cref="Generate()" />'s fixed phrase;
    ///     any other value is a seeded improvisation over the same scale and the same
    ///     synthesis — a DIFFERENT performance of the same instrument.
    ///     <para>
    ///         That distinction is what makes content invariance testable at all: the
    ///         only way to show the metric hears the RIG and not the NOTES is to put
    ///         two different performances through one rig and check they land closer
    ///         together than one performance through two rigs does. It is also what a
    ///         retrieval library wants — rendering every candidate over a medley of
    ///         variants instead of one phrase stops the library learning the phrase.
    ///     </para>
    /// </summary>
    public static MonoAudio Generate(int variant)
    {
        var notes = variant == 0 ? Phrase() : Improvisation(variant);
        var seconds = 0.0;
        foreach (var n in notes) seconds = Math.Max(seconds, n.Start + n.Duration);
        var length = (int)Math.Ceiling((seconds + 1.0) * SampleRate);
        var buffer = new double[length];

        var seed = 0x5F3759DFu;
        foreach (var note in notes)
        {
            // Each note advances the shared LCG, so the whole take is one
            // reproducible stream and note N's excitation never depends on a clock.
            Pluck(buffer, note, ref seed);
        }

        var samples = new float[length];
        double peak = 0;
        foreach (var v in buffer) peak = Math.Max(peak, Math.Abs(v));
        // A real DI sits well under full scale; −6 dBFS leaves a drive room to be
        // driven without the take itself being what clips.
        var gain = peak > 1e-9 ? Math.Pow(10.0, -6.0 / 20.0) / peak : 0;
        for (var i = 0; i < length; i++) samples[i] = (float)(buffer[i] * gain);

        return new MonoAudio(samples, SampleRate);
    }

    /// <summary>
    ///     <b>The fixed 20-second medley the retrieval library is rendered over.</b>
    ///     Four five-second windows taken from four DIFFERENT performances of the same
    ///     instrument (<see cref="Generate(int)" /> variants 0, 3, 7 and 11).
    ///     <para>
    ///         A library rendered over ONE phrase learns the phrase: every entry shares
    ///         the same notes, so the part of each entry's feature vector that comes
    ///         from content is identical and cancels — which looks like excellent
    ///         separation right up until a real target, with different notes, arrives
    ///         and that shared term does not cancel. Four performances leave a
    ///         library whose spread reflects the RIGS.
    ///     </para>
    ///     <para>
    ///         Twenty seconds because the modulation block needs room: a 600 ms delay
    ///         repeat is only measurable across a window many times its length, and
    ///         five seconds of one performance is already the minimum for that.
    ///         Deterministic, cached, and safe to share — the array is never mutated.
    ///     </para>
    /// </summary>
    public static MonoAudio Medley()
    {
        var cached = _medley;
        if (cached is not null) return cached;

        int[] variants = [0, 3, 7, 11];
        const double windowSeconds = 5.0;
        var window = (int)(windowSeconds * SampleRate);
        var samples = new float[window * variants.Length];

        for (var i = 0; i < variants.Length; i++)
        {
            var take = Generate(variants[i]).Samples;
            // Skip the first half second: every generated take starts on a downbeat,
            // and four identical attacks at 0, 5, 10 and 15 s would be a rhythm the
            // modulation block reads as a 0.2 Hz tremolo.
            var from = (int)(0.5 * SampleRate);
            for (var n = 0; n < window; n++)
            {
                var source = from + n;
                var value = source < take.Length ? take[source] : 0f;
                // 20 ms crossfade at each seam, so a splice is not an impulse.
                var fade = (int)(0.02 * SampleRate);
                if (n < fade) value *= n / (float)fade;
                else if (n > window - fade) value *= (window - n) / (float)fade;
                samples[i * window + n] = value;
            }
        }

        return _medley = new MonoAudio(samples, SampleRate);
    }

    private static MonoAudio? _medley;

    private readonly record struct Note(double Frequency, double Start, double Duration, double Velocity);

    /// <summary>
    ///     A different performance: single notes and two-note shapes drawn from the
    ///     same E minor pentatonic, with seeded rhythm and dynamics. Same synthesis,
    ///     same register, same instrument — only the notes change.
    /// </summary>
    private static List<Note> Improvisation(int variant)
    {
        double[] scale =
        [
            82.41, 98.0, 110.0, 123.47, 146.83, 164.81, 196.0, 220.0, 246.94, 293.66, 329.63, 392.0
        ];

        var rng = 0x9E3779B9u + (uint)variant * 2654435761u;
        var notes = new List<Note>();
        var t = 0.0;
        while (t < 11.5)
        {
            rng = rng * 1664525u + 1013904223u;
            var degree = (int)((rng >> 16) % (uint)scale.Length);
            rng = rng * 1664525u + 1013904223u;
            var duration = 0.18 + (rng >> 16) % 1000 / 1000.0 * 0.55;
            rng = rng * 1664525u + 1013904223u;
            var velocity = 0.45 + (rng >> 16) % 1000 / 1000.0 * 0.55;

            notes.Add(new Note(scale[degree], t, duration + 0.15, velocity));

            // Every third note gets a fifth above it, so the take has dyads as well
            // as single notes and a drive has something to intermodulate.
            if (degree + 3 < scale.Length && (rng >> 24) % 3 == 0)
                notes.Add(new Note(scale[degree] * 1.4983, t + 0.005, duration + 0.15, velocity * 0.85));

            t += duration;
        }

        return notes;
    }

    private static List<Note> Phrase()
    {
        var notes = new List<Note>();

        // Two bars of E5 / G5 / A5 power chords: root + fifth + octave, strummed
        // with a 7 ms spread so the chord is not three simultaneous impulses.
        double[] roots = [82.41, 82.41, 97.999, 110.0, 82.41, 82.41, 110.0, 97.999];
        for (var i = 0; i < roots.Length; i++)
        {
            var start = i * 0.6;
            var velocity = i % 4 == 0 ? 1.0 : 0.72;
            notes.Add(new Note(roots[i], start, 0.62, velocity));
            notes.Add(new Note(roots[i] * 1.4983, start + 0.004, 0.62, velocity * 0.9));
            notes.Add(new Note(roots[i] * 2.0, start + 0.008, 0.62, velocity * 0.8));
        }

        // A single-note line in the same key, quieter and more separated: this is
        // what a harmonic-to-noise measurement reads, and a chord alone makes every
        // rig look more distorted than it is.
        double[] line = [164.81, 196.0, 246.94, 293.66, 246.94, 196.0, 164.81, 146.83];
        for (var i = 0; i < line.Length; i++)
            notes.Add(new Note(line[i], 4.8 + i * 0.32, 0.45, i % 2 == 0 ? 0.85 : 0.6));

        // A sustained open chord to end on: the tail a reverb, a delay or a
        // compressor's release has to act on.
        double[] chord = [82.41, 123.47, 164.81, 207.65, 246.94, 329.63];
        for (var i = 0; i < chord.Length; i++)
            notes.Add(new Note(chord[i], 7.6 + i * 0.012, 3.2, 0.9));

        // …and a hard single hit after it, for transient response.
        notes.Add(new Note(110.0, 11.2, 0.8, 1.0));

        return notes;
    }

    /// <summary>
    ///     Karplus-Strong with a pick-position comb and a fixed damping filter. The
    ///     excitation is a velocity-scaled noise burst shaped by a short attack; the
    ///     loop filter is a two-tap average whose feedback sets the decay.
    /// </summary>
    private static void Pluck(double[] buffer, Note note, ref uint seed)
    {
        var start = (int)(note.Start * SampleRate);
        var frames = (int)(note.Duration * SampleRate);
        if (start >= buffer.Length) return;
        frames = Math.Min(frames, buffer.Length - start);

        var delay = Math.Max(2, (int)Math.Round(SampleRate / note.Frequency));
        var line = new double[delay];

        // Pick position: a comb at 1/5 of the string kills the fifth harmonic and is
        // what stops the burst sounding like a filtered click.
        var comb = Math.Max(1, delay / 5);
        var burst = new double[delay];
        for (var i = 0; i < delay; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            burst[i] = (seed >> 8) / (double)(1 << 23) - 1.0;
        }

        for (var i = 0; i < delay; i++)
        {
            var combed = burst[i] - (i >= comb ? burst[i - comb] : 0);
            // A 3 ms attack ramp: a raw step is a DC transient no pickup produces.
            var ramp = Math.Min(1.0, i / (0.003 * SampleRate));
            line[i] = combed * note.Velocity * 0.5 * ramp;
        }

        // Higher notes decay faster, as a real string does.
        var feedback = 0.996 - 0.00004 * (note.Frequency / 82.41);
        var index = 0;
        var previous = 0.0;
        for (var n = 0; n < frames; n++)
        {
            var current = line[index];
            var filtered = 0.5 * (current + previous) * feedback;
            previous = current;
            line[index] = filtered;
            index = index + 1 >= delay ? 0 : index + 1;

            // A release ramp over the last 40 ms, so a note that is cut short does
            // not end in a click the transient block would read as pick attack.
            var remaining = frames - n;
            var release = remaining < 0.04 * SampleRate ? remaining / (0.04 * SampleRate) : 1.0;
            buffer[start + n] += current * release;
        }
    }
}
