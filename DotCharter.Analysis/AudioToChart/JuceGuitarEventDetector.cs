using JuceSharp.Dsp;

namespace DotCharter.Analysis.AudioToChart;

/// <summary>
///     Offline adapter over the realtime <see cref="JuceGuitarAnalyzer"/>
///     pipeline. The analyzer itself is pure managed and chunk-boundary
///     independent, so reusing it offline keeps detection behaviour identical
///     to live play without touching JuceSharp.Dsp.
/// </summary>
public sealed class JuceGuitarEventDetector : IGuitarEventDetector
{
    /// <summary>
    ///     How early the analyzer stamps a LEGATO event, added back before the event
    /// leaves this adapter. Measured on the hand-corrected <c>TestData/EOF</c>
    ///     corpus: its charts sit on the audio's attacks (an event-triggered spectral
    ///     flux average peaks 2–3 ms before the charted time), picked ONSET events
    ///     land a median 6 ms early, and legato events — which on a full mix are
    ///     85 % of what the analyzer reports — a median 42 ms early, on the mix and on
    ///     a separated guitar stem alike. Sweeping the shift over 0–50 ms put the best
    ///     onset F1 at 40 ms. Onsets are left alone: shifting them measured no better.
    ///     <para>
    ///     Offline only. Live play reads <see cref="JuceGuitarAnalyzer" /> directly and
    ///     judges with its own windows; this constant never reaches it.
    ///     </para>
    /// </summary>
    public const double LegatoLeadSeconds = 0.040;

    // Rms level at which an attack counts as "full strength". Soft playing
    // (~0.02 rms) still yields ~0.3 so quiet passages are kept, just marked
    // less confident.
    private const float StrengthKneeRms = 0.05f;

    private readonly JuceGuitarAnalyzer _analyzer;

    public JuceGuitarEventDetector(int sampleRate)
    {
        _analyzer = new JuceGuitarAnalyzer(sampleRate);
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    public IReadOnlyList<GuitarAudioEvent> Push(ReadOnlySpan<float> samples)
    {
        var events = _analyzer.Push(samples);
        if (events.Count == 0) return Array.Empty<GuitarAudioEvent>();

        var mapped = new List<GuitarAudioEvent>(events.Count);
        foreach (var ev in events)
        {
            if (ev.Pitches.Length == 0) continue; // percussive: nothing chartable

            var pitches = new GuitarPitchCandidate[ev.Pitches.Length];
            for (var i = 0; i < ev.Pitches.Length; i++)
            {
                var p = ev.Pitches[i];
                pitches[i] = new GuitarPitchCandidate(p.Midi, p.FrequencyHz, p.Salience);
            }

            var strength = ev.Rms / (ev.Rms + StrengthKneeRms);
            var legato = ev.Kind == JuceGuitarEventKind.Legato;
            mapped.Add(new GuitarAudioEvent(
                ReportedSeconds(legato, ev.StreamSeconds),
                legato,
                strength,
                pitches));
        }

        return mapped;
    }

    public void Reset() => _analyzer.Reset();

    /// <summary>The time this adapter reports for an analyzer event: a legato event
    /// moved later by <see cref="LegatoLeadSeconds" />, a picked onset unchanged.</summary>
    internal static double ReportedSeconds(bool legato, double streamSeconds) =>
        legato ? streamSeconds + LegatoLeadSeconds : streamSeconds;
}
