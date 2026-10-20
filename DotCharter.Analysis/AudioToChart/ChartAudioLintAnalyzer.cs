using JuceSharp.Dsp;

namespace DotCharter.Analysis.AudioToChart;

/// <summary>One charted sound to lint, projected to sounding pitches.
/// <paramref name="IsChord"/> switches the sub-octave veto OFF (chords carry
/// real octaves); <paramref name="IsPitched"/> false (mutes, unpitched
/// slides) skips the sound entirely — there is no pitch to verify.</summary>
public sealed record LintExpectedSound(
    double TimeMs, IReadOnlyList<int> SoundingMidis, bool IsChord, bool IsPitched = true);

/// <summary>One unverified charted pitch: what the chart says, and — when a
/// neighboring semitone DID confirm — what the audio says instead.</summary>
public sealed record ChartAudioFinding(
    double TimeMs,
    int ChartedMidi,
    float Confidence,
    int? ObservedMidi,
    float ObservedConfidence);

public sealed record ChartAudioLintResult(
    int SoundsChecked, int SoundsVerified, IReadOnlyList<ChartAudioFinding> Findings);

public sealed class ChartAudioLintOptions
{
    /// <summary>How far (semitones, each direction) the "audio says" probe
    /// looks for the pitch that actually sounded when the charted one failed.</summary>
    public int ObservedSearchSemitones { get; init; } = 4;

    public static ChartAudioLintOptions Default { get; } = new();
}

/// <summary>
///     v0.29 audio-verified chart lint: offline quarter-tone CQT over a whole
///     clip (<see cref="JuceConstantQ.AnalyzeWhole" />) + the Binary-Match-Mask
///     verifier (<see cref="JuceChartVerifier" />), asked per charted pitch:
///     did this note's energy actually appear at its chart time? Unverified
///     pitches probe nearby semitones for the "audio says B4" hint.
///     <para>
///     ADVISORY like everything in this project: deterministic review
///     candidates, never auto-committed, and detached from live scoring (the
///     live path is Services' <c>LiveCqtVerifier</c>; the CQT constants below
///     mirror its canonical values — the A0 floor, quarter-tone bins to E7, hop 256,
///     the kernel cap — so lint and live agree on what is audible).
///     </para>
/// </summary>
public static class ChartAudioLintAnalyzer
{
    // The SHIPPING configuration, read from the single source of truth in JuceSharp.Dsp
    // (Services' LiveCqtVerifier reads the same constants — it is not referenceable from
    // here, which is why these must not be hand-copied: copies drift).
    //
    // NOTE: two of them were TUNED against the STREAMING statistics and are inherited here
    // for consistency, not because they were measured on this path. `AnalyzeWhole` computes
    // WHOLE-CLIP percentiles (95th per-bin floor, 5th noise floor) rather than the rolling
    // ones live play uses, so the same NoiseMargin is a materially looser gate offline, and
    // the veto's `sub > Floor(bs)` precondition reads a clip-length-dependent floor.
    private const int MinMidi = LiveCqtConfig.MinMidi;
    private const int BinCount = LiveCqtConfig.BinCount;
    private const int Hop = LiveCqtConfig.Hop;
    private const int MaxKernelSamples = LiveCqtConfig.MaxKernelSamples;
    private const float NoiseMargin = LiveCqtConfig.NoiseMargin;
    private const double SubOctaveVeto = LiveCqtConfig.SubOctaveVeto;
    private const int SubOctaveBins = LiveCqtConfig.SubOctaveBins;

    // Quarter-tone bins, so two bins per semitone (JuceConstantQ's default
    // binsPerOctave = 24, Step = 2). Derived, never re-typed: the bins are what
    // decides the range, and BinForMidi is (midi - MinMidi) * Step.
    private const int BinsPerSemitone = 2;

    /// <summary>Lowest sounding MIDI the CQT can address (E1).</summary>
    public const int MinVerifiableMidi = MinMidi;

    /// <summary>
    ///     Highest sounding MIDI the CQT can address — <see cref="MinVerifiableMidi" />
    ///     plus the semitones <c>BinCount</c> quarter-tone bins span (E7).
    ///     <para>
    ///     A pitch outside <c>[MinVerifiableMidi, MaxVerifiableMidi]</c> has no
    ///     bin at all, so <c>JuceChartVerifier.Verify</c> returns a flat no-hit
    ///     for it and <see cref="ProbeObserved" /> — which only looks NEARBY —
    ///     finds nothing to name either. Reporting that as a finding says "the
    ///     audio is silent here" about a pitch this analyzer never listened to:
    ///     a 5-string bass's low B (23) and a 4-string bass dropped to D (26)
    ///     each produced one bogus finding PER NOTE on a chart that was right.
    ///     Unknowable is not wrong, so those pitches are skipped exactly like
    ///     mutes and harmonics.
    ///     </para>
    /// </summary>
    public const int MaxVerifiableMidi = MinMidi + (BinCount - 1) / BinsPerSemitone;

    /// <summary>True when <paramref name="midi" /> has a CQT bin — i.e. this
    /// analyzer can say anything at all about it.</summary>
    public static bool IsVerifiable(int midi) => midi is >= MinVerifiableMidi and <= MaxVerifiableMidi;

    public static ChartAudioLintResult Analyze(ReadOnlySpan<float> monoPcm, int sampleRate,
        IReadOnlyList<LintExpectedSound> sounds, ChartAudioLintOptions? options = null)
    {
        options ??= ChartAudioLintOptions.Default;
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var cqt = new JuceConstantQ(sampleRate,
            hop: Hop, fMinMidi: MinMidi, binCount: BinCount, maxKernelSamples: MaxKernelSamples,
            subOctaveBins: SubOctaveBins);
        cqt.AnalyzeWhole(monoPcm);
        var verifier = new JuceChartVerifier(cqt)
        {
            NoiseMargin = NoiseMargin,
            SubOctaveVeto = SubOctaveVeto
        };

        var findings = new List<ChartAudioFinding>();
        var checkedCount = 0;
        var verifiedCount = 0;
        // An ORDER-PRESERVING dedup, deliberately not a HashSet. `Findings` is
        // OUTPUT — a lint report a charter reads top to bottom — and
        // `HashSet<T>`'s enumeration order is explicitly unspecified. The
        // determinism contract here is byte-identical output, not "stable on the
        // runtime build we happen to ship"; a chord's findings come out in the
        // chart's own string order. A sound has at most a handful of pitches, so
        // the linear Contains is free.
        var distinct = new List<int>(8);

        foreach (var sound in sounds)
        {
            if (!sound.IsPitched || sound.SoundingMidis.Count == 0) continue;

            distinct.Clear();
            foreach (var midi in sound.SoundingMidis)
            {
                if (IsVerifiable(midi) && !distinct.Contains(midi))
                    distinct.Add(midi);
            }
            // Every pitch of this sound is outside the CQT's range: it was never
            // listened to, so it is neither checked nor reported. Counting it as
            // CHECKED would be as dishonest as filing the finding — the summary
            // line is "N of M verified".
            if (distinct.Count == 0) continue;
            checkedCount++;

            var t = sound.TimeMs / 1000.0;
            // Chords carry real octaves (power/barre chords), so the
            // sub-octave veto applies to SINGLE notes only — the same rule
            // the live verifier ships.
            var allowVeto = !sound.IsChord && distinct.Count == 1;

            var allVerified = true;
            foreach (var midi in distinct)
            {
                var result = verifier.Verify(midi, t, allowHarmonicPeak: true, allowSubOctaveVeto: allowVeto);
                if (result.Hit) continue;

                allVerified = false;
                var (observedMidi, observedConfidence) = ProbeObserved(verifier, midi, t, options);
                findings.Add(new ChartAudioFinding(
                    sound.TimeMs, midi, result.Confidence, observedMidi, observedConfidence));
            }

            if (allVerified) verifiedCount++;
        }

        return new ChartAudioLintResult(checkedCount, verifiedCount, findings);
    }

    /// <summary>The strongest CONFIRMING semitone near a failed charted pitch
    /// — "charted C5, audio says B4". Null when nothing nearby confirms
    /// either (silence, or something too far away).</summary>
    private static (int? Midi, float Confidence) ProbeObserved(
        JuceChartVerifier verifier, int chartedMidi, double tSeconds, ChartAudioLintOptions options)
    {
        int? best = null;
        var bestConfidence = 0f;
        for (var offset = -options.ObservedSearchSemitones; offset <= options.ObservedSearchSemitones; offset++)
        {
            if (offset == 0) continue;
            var candidate = chartedMidi + offset;
            if (!IsVerifiable(candidate)) continue;
            // No veto on the probe: it ASKS what is sounding, it does not judge.
            var result = verifier.Verify(candidate, tSeconds, allowHarmonicPeak: true, allowSubOctaveVeto: false);
            if (!result.Hit || result.Confidence <= bestConfidence) continue;
            best = candidate;
            bestConfidence = result.Confidence;
        }

        return (best, bestConfidence);
    }
}
