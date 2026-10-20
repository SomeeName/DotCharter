using JuceSharp.Dsp;

namespace DotCharter.Analysis.AudioToChart;

/// <summary>
/// Live Play's constant-Q verifier used as the draft's PITCH stage: a
///     whole-clip quarter-tone CQT (<see cref="JuceConstantQ.AnalyzeWhole" />, the
///     shipping <see cref="LiveCqtConfig" /> — the same transform and Binary-Match-Mask
///     verifier <see cref="ChartAudioLintAnalyzer" /> runs) asked, at each detected
///     event, which pitches of the arrangement's playable range are sounding.
///     <para>
///     <b>Why.</b> The detector's own pitches come from an onset-synchronous
///     polyphonic estimate built for a clean DI guitar; on a full mix, at the onsets
///     it finds, only 10.9 % of the charted pitches were among the drafted ones. Asking
///     the verifier per candidate pitch — confined to what the arrangement can play —
///     roughly doubled note F1 on the corpus, on the mix and on separated stems; the
///     numbers are in <c>docs/agents/testing/autochart-eval.md</c>.
///     </para>
///     <para>
///     Built once per CLIP, because the transform depends only on the audio; each
///     arrangement then asks it with its own range. Not thread-safe (neither is the
///     verifier). Deterministic: the transform and every ranking tie-break are fixed.
///     </para>
/// </summary>
internal sealed class CqtPitchEvidence
{
    /// <summary>A confirmed pitch is dropped as a PARTIAL when the pitch an octave or
    /// an octave-and-a-fifth below (its 2nd or 3rd harmonic's fundamental) is confirmed
    /// at least this loud relative to it.</summary>
    internal const float PartialRatio = 0.5f;

    private static readonly int[] PartialSemitones = [12, 19];

    private readonly JuceChartVerifier _verifier;
    private readonly double _clipStartSeconds;

    private CqtPitchEvidence(JuceChartVerifier verifier, double clipStartSeconds)
    {
        _verifier = verifier;
        _clipStartSeconds = clipStartSeconds;
    }

    /// <summary>The CQT of a whole mono clip at <paramref name="sampleRate" />. Event times are
    /// asked on the clip's own axis, or — for an excerpt that starts
    /// <paramref name="clipStartSeconds" /> into a song — on the song's.</summary>
    internal static CqtPitchEvidence FromWholeClip(ReadOnlySpan<float> mono, int sampleRate, double clipStartSeconds = 0)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var cqt = new JuceConstantQ(sampleRate,
            hop: LiveCqtConfig.Hop, fMinMidi: LiveCqtConfig.MinMidi, binCount: LiveCqtConfig.BinCount,
            maxKernelSamples: LiveCqtConfig.MaxKernelSamples, subOctaveBins: LiveCqtConfig.SubOctaveBins);
        cqt.AnalyzeWhole(mono);
        return new CqtPitchEvidence(new JuceChartVerifier(cqt)
        {
            NoiseMargin = LiveCqtConfig.NoiseMargin,
            SubOctaveVeto = LiveCqtConfig.SubOctaveVeto
        }, clipStartSeconds);
    }

    /// <summary>
    ///     Up to <paramref name="maxPitches" /> pitches in <c>[lowMidi, highMidi]</c> the
    ///     verifier confirms at <paramref name="timeSeconds" />, loudest first (ties: the
    ///     lower pitch), partials of a louder confirmed pitch removed. Salience is the
    ///     level relative to the loudest, so the strongest is 1. Empty when nothing is
    ///     confirmed — the caller keeps the detector's own pitches then.
    /// </summary>
    internal IReadOnlyList<GuitarPitchCandidate> PitchesAt(double timeSeconds, int lowMidi, int highMidi, int maxPitches)
    {
        if (maxPitches <= 0 || !double.IsFinite(timeSeconds)) return [];
        var hits = new List<(int Midi, float Level)>();
        for (var midi = Math.Max(lowMidi, ChartAudioLintAnalyzer.MinVerifiableMidi);
             midi <= Math.Min(highMidi, ChartAudioLintAnalyzer.MaxVerifiableMidi);
             midi++)
        {
            var result = _verifier.Verify(midi, timeSeconds - _clipStartSeconds, allowHarmonicPeak: true,
                allowSubOctaveVeto: true);
            if (result.Hit && float.IsFinite(result.Level)) hits.Add((midi, result.Level));
        }

        var kept = new List<(int Midi, float Level)>(hits.Count);
        foreach (var hit in hits)
        {
            if (!IsPartial(hit, hits)) kept.Add(hit);
        }

        if (kept.Count == 0) return [];
        kept.Sort(static (a, b) => b.Level != a.Level ? b.Level.CompareTo(a.Level) : a.Midi.CompareTo(b.Midi));
        var top = kept[0].Level;
        var count = Math.Min(maxPitches, kept.Count);
        var pitches = new GuitarPitchCandidate[count];
        for (var i = 0; i < count; i++)
        {
            var (midi, level) = kept[i];
            pitches[i] = new GuitarPitchCandidate(midi, (float)(440.0 * Math.Pow(2.0, (midi - 69) / 12.0)),
                top > 0f ? level / top : 0f);
        }

        return pitches;
    }

    private static bool IsPartial((int Midi, float Level) hit, List<(int Midi, float Level)> hits)
    {
        foreach (var below in PartialSemitones)
        {
            foreach (var other in hits)
            {
                if (other.Midi == hit.Midi - below && other.Level >= PartialRatio * hit.Level)
                    return true;
            }
        }

        return false;
    }
}
