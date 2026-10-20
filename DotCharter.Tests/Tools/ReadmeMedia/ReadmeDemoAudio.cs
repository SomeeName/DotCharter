namespace DotCharter.Tests.Tools.ReadmeMedia;

/// <summary>Which voice of the little band plays a <see cref="ReadmeDemoNote" />.</summary>
internal enum ReadmeDemoInstrument
{
    Guitar,
    Seven,
    Bass,
    Kick,
    Snare,
    Hat
}

/// <summary>One sounding note of the demo song: when, how long, which pitch, and how the pitch moves.</summary>
/// <param name="Instrument">The voice.</param>
/// <param name="StartMs">Start, in milliseconds from the top of the song.</param>
/// <param name="DurationMs">Length in milliseconds.</param>
/// <param name="Midi">MIDI note number (ignored by the drums).</param>
/// <param name="Gain">0-1 loudness.</param>
/// <param name="Pitch">Piecewise-linear pitch offset in semitones against time since the start, or null.</param>
internal sealed record ReadmeDemoNote(
    ReadmeDemoInstrument Instrument, double StartMs, double DurationMs, int Midi, double Gain,
    (double Ms, double Semitones)[]? Pitch = null);

/// <summary>
///     Renders a demo song to audio with a tiny additive synth - plucked strings with a soft
///     clip for the guitars, a sine bass and synthesised drums - so the README song is
///     ours down to the last sample. Deterministic (the noise is seeded); no recording is read.
/// </summary>
internal static class ReadmeDemoAudio
{
    public const int SampleRate = 44100;

    /// <summary>Renders <paramref name="notes" /> to 16-bit stereo samples, <paramref name="tailMs" /> past the last note.</summary>
    public static short[] Render(IReadOnlyList<ReadmeDemoNote> notes, double tailMs = 2500)
    {
        var end = notes.Max(n => n.StartMs + n.DurationMs) + tailMs;
        var frames = (int)(end / 1000.0 * SampleRate);
        var left = new float[frames];
        var right = new float[frames];
        var random = new Random(912);
        foreach (var note in notes)
        {
            var start = (int)(note.StartMs / 1000.0 * SampleRate);
            switch (note.Instrument)
            {
                case ReadmeDemoInstrument.Kick: Kick(left, right, start, (float)note.Gain); break;
                case ReadmeDemoInstrument.Snare: Snare(left, right, start, (float)note.Gain, random); break;
                case ReadmeDemoInstrument.Hat: Hat(left, right, start, (float)note.Gain, random); break;
                default: Pitched(left, right, start, note); break;
            }
        }

        var peak = 0f;
        for (var i = 0; i < frames; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        var scale = peak > 0 ? 0.85f / peak : 1f;
        var pcm = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            pcm[i * 2] = (short)(left[i] * scale * short.MaxValue);
            pcm[(i * 2) + 1] = (short)(right[i] * scale * short.MaxValue);
        }

        return pcm;
    }

    private static void Pitched(float[] left, float[] right, int start, ReadmeDemoNote note)
    {
        var isBass = note.Instrument == ReadmeDemoInstrument.Bass;
        var harmonics = isBass ? 3 : 7;
        var seconds = Math.Min(note.DurationMs / 1000.0 + 0.25, isBass ? 2.0 : 4.0);
        var length = (int)(seconds * SampleRate);
        var panLeft = note.Instrument switch
        {
            ReadmeDemoInstrument.Guitar => 0.65f,
            ReadmeDemoInstrument.Seven => 0.35f,
            _ => 0.5f
        };
        var decay = isBass ? 1.6 : note.Instrument == ReadmeDemoInstrument.Seven ? 2.4 : 2.0;
        var phase = 0.0;
        for (var i = 0; i < length && start + i < left.Length; i++)
        {
            var t = i / (double)SampleRate;
            var semis = PitchAt(note.Pitch, t * 1000.0);
            var frequency = 440.0 * Math.Pow(2, ((note.Midi + semis) - 69) / 12.0);
            phase += 2 * Math.PI * frequency / SampleRate;
            var value = 0.0;
            for (var h = 1; h <= harmonics; h++)
                value += Math.Sin(phase * h) / (h * (isBass ? 1.8 : 1.0)) * Math.Exp(-t * decay * (0.6 + (h * 0.35)));
            var gate = t < note.DurationMs / 1000.0 ? 1.0 : Math.Exp(-(t - (note.DurationMs / 1000.0)) * 14);
            var attack = Math.Min(1.0, t * 400);
            var sample = (float)(Math.Tanh(value * (isBass ? 1.0 : 1.9)) * gate * attack * note.Gain * 0.22);
            left[start + i] += sample * panLeft * 2;
            right[start + i] += sample * (1 - panLeft) * 2;
        }
    }

    private static double PitchAt((double Ms, double Semitones)[]? curve, double ms)
    {
        if (curve == null || curve.Length == 0) return 0;
        if (ms <= curve[0].Ms) return curve[0].Semitones;
        for (var i = 1; i < curve.Length; i++)
        {
            if (ms > curve[i].Ms) continue;
            var span = curve[i].Ms - curve[i - 1].Ms;
            var f = span <= 0 ? 1 : (ms - curve[i - 1].Ms) / span;
            return curve[i - 1].Semitones + ((curve[i].Semitones - curve[i - 1].Semitones) * f);
        }

        return curve[^1].Semitones;
    }

    private static void Kick(float[] left, float[] right, int start, float gain)
    {
        var length = (int)(0.28 * SampleRate);
        var phase = 0.0;
        for (var i = 0; i < length && start + i < left.Length; i++)
        {
            var t = i / (double)SampleRate;
            phase += 2 * Math.PI * (45 + (110 * Math.Exp(-t * 30))) / SampleRate;
            var sample = (float)(Math.Sin(phase) * Math.Exp(-t * 12) * gain * 0.7);
            left[start + i] += sample;
            right[start + i] += sample;
        }
    }

    private static void Snare(float[] left, float[] right, int start, float gain, Random random)
    {
        var length = (int)(0.22 * SampleRate);
        for (var i = 0; i < length && start + i < left.Length; i++)
        {
            var t = i / (double)SampleRate;
            var noise = (random.NextDouble() * 2) - 1;
            var tone = Math.Sin(2 * Math.PI * 190 * t);
            var sample = (float)(((noise * 0.8) + (tone * 0.4)) * Math.Exp(-t * 20) * gain * 0.4);
            left[start + i] += sample;
            right[start + i] += sample;
        }
    }

    private static void Hat(float[] left, float[] right, int start, float gain, Random random)
    {
        var length = (int)(0.06 * SampleRate);
        var previous = 0.0;
        for (var i = 0; i < length && start + i < left.Length; i++)
        {
            var t = i / (double)SampleRate;
            var noise = (random.NextDouble() * 2) - 1;
            var high = noise - previous;
            previous = noise;
            var sample = (float)(high * Math.Exp(-t * 70) * gain * 0.12);
            left[start + i] += sample;
            right[start + i] += sample;
        }
    }
}
