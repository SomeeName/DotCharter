using System.Globalization;
using DotCharter.Analysis.Tone;

namespace DotCharter.Tests.Analysis.Tone;

/// <summary>
///     <see cref="ToneAudioAnalyzer" /> — the measurement half of "generate a tone
///     from the song's own audio".
///     <para>
///         Every fixture is SYNTHESISED in this file, so each expectation is
///         arguable from first principles rather than from what one recording
///         happened to produce. A sine's crest factor is 3.01 dB because
///         <c>1/(1/√2)</c> is 3.01 dB, not because a run said so.
///     </para>
///     <para>
///         The load-bearing claim of the whole feature is a RELATIONSHIP — a
///         clipped signal must read as more driven than the clean one it was made
///         from — so that is asserted as an ordering between two profiles, never
///         against a magic constant that a tuning pass could quietly satisfy.
///     </para>
///     <para>
///         <b>Determinism</b> is also covered from the other side by
///         <see cref="AnalysisDeterminismTests" />, which re-runs this pipeline
///         after every other one in the project — the shared-state case a
///         same-buffer-twice test in one class cannot see.
///     </para>
/// </summary>
public sealed class ToneAudioAnalyzerTests(ITestOutputHelper output)
{
    private const int Rate = AnalysisTestSignals.SampleRate;

    // ------------------------------------------------------------------ levels

    /// <summary>
    ///     A sine's peak is √2 × its RMS — 3.0103 dB — and nothing about framing,
    ///     windowing or the transform may move a purely time-domain measurement.
    /// </summary>
    [Fact]
    public void APureSine_HasTheCrestFactorOfASine()
    {
        var profile = ToneAudioAnalyzer.Analyze(Sine(220.0, 2.0, 0.8f), Rate);

        Assert.Equal(0.8 / Math.Sqrt(2), profile.Rms, 3);
        Assert.Equal(20 * Math.Log10(0.8), profile.PeakDb, 2);
        Assert.Equal(3.0103, profile.CrestFactorDb, 2);
    }

    /// <summary>
    ///     Digital silence has no peak, and <c>log10(0)</c> is negative infinity —
    ///     which would then reach the mapper, the UI and any file the tone is
    ///     written to. The floor is a real number.
    /// </summary>
    [Fact]
    public void DigitalSilence_ReportsMinusOneHundredTwentyDbRatherThanNegativeInfinity()
    {
        var profile = ToneAudioAnalyzer.Analyze(new float[Rate], Rate);

        Assert.Equal(-120.0, profile.PeakDb);
        Assert.True(profile.IsSilent, "an all-zero span must be silent");
    }

    /// <summary>
    ///     The crash that reaches a user. <c>NaN</c> loses every comparison, so a
    ///     single one here survives all of the mapper's range checks and lands in
    ///     a tone the app presents as measured.
    /// </summary>
    [Theory]
    [MemberData(nameof(EverySignal))]
    public void EveryFieldIsFinite_ForEverySignal(string name)
    {
        var profile = ToneAudioAnalyzer.Analyze(Signal(name), Rate);

        foreach (var (field, value) in Fields(profile))
        {
            Assert.True(double.IsFinite(value),
                $"{name}.{field} was {value.ToString("R", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>Silence carries no signal, so no tone may be inferred from it —
    /// and every field, <see cref="ToneAudioProfile.Drive" /> included, still has
    /// to be a number.</summary>
    [Fact]
    public void Silence_IsSilentAndCarriesNoDrive()
    {
        var profile = ToneAudioAnalyzer.Analyze(new float[Rate], Rate);

        Assert.True(profile.IsSilent);
        Assert.Equal(0.0, profile.Drive);
        foreach (var (field, value) in Fields(profile))
            Assert.True(double.IsFinite(value), $"silence.{field} was {value}");
    }

    /// <summary>A span quieter than <see cref="ToneAudioProfile.SilenceRms" /> is
    /// noise floor, not a performance. Guessing a tone from it produces a
    /// confident, wrong answer.</summary>
    [Fact]
    public void AVeryQuietSpan_IsTreatedAsSilence()
    {
        var profile = ToneAudioAnalyzer.Analyze(Sine(220.0, 1.0, 1e-5f), Rate);

        Assert.True(profile.IsSilent);
    }

    // ---------------------------------------------------------------- spectrum

    /// <summary>
    ///     Flatness is 0 for a pure tone and 1 for white noise; a single sine and
    ///     a noise burst are the two ends of that definition, so they bracket
    ///     every real signal.
    /// </summary>
    [Fact]
    public void FlatnessRunsFromAPureToneToWhiteNoise()
    {
        var sine = ToneAudioAnalyzer.Analyze(Sine(220.0, 2.0, 0.8f), Rate);
        var noise = ToneAudioAnalyzer.Analyze(WhiteNoise(2.0, 0.5f), Rate);

        Assert.True(sine.SpectralFlatness < 0.02,
            $"a pure sine is not a flat spectrum, got {sine.SpectralFlatness:R}");
        Assert.True(noise.SpectralFlatness > 0.5,
            $"white noise is a flat spectrum, got {noise.SpectralFlatness:R}");
    }

    /// <summary>
    ///     White noise has no fundamental, so nothing sits on a harmonic series;
    ///     a sine is nothing BUT its own fundamental.
    /// </summary>
    [Fact]
    public void HarmonicRatio_SeparatesAPitchedNoteFromNoise()
    {
        var sine = ToneAudioAnalyzer.Analyze(Sine(220.0, 2.0, 0.8f), Rate);
        var noise = ToneAudioAnalyzer.Analyze(WhiteNoise(2.0, 0.5f), Rate);

        Assert.True(sine.HarmonicRatio > 0.8,
            $"a sine's energy is all on its own fundamental, got {sine.HarmonicRatio:R}");
        Assert.True(noise.HarmonicRatio < 0.25,
            $"noise has no harmonic series, got {noise.HarmonicRatio:R}");
    }

    /// <summary>
    ///     "Bright" is the whole point of the centroid — if the ordering does not
    ///     hold, every treble and presence setting downstream is arbitrary.
    /// </summary>
    [Fact]
    public void ADarkToneHasALowerCentroidThanABrightOne()
    {
        var dark = ToneAudioAnalyzer.Analyze(Sine(200.0, 1.5, 0.8f), Rate);
        var bright = ToneAudioAnalyzer.Analyze(Sine(3000.0, 1.5, 0.8f), Rate);

        Assert.True(dark.SpectralCentroidHz < bright.SpectralCentroidHz,
            $"200 Hz read as {dark.SpectralCentroidHz:R} Hz, 3 kHz as {bright.SpectralCentroidHz:R} Hz");

        // …and each lands near its own tone, not merely in the right order.
        Assert.Equal(200.0, dark.SpectralCentroidHz, 60.0);
        Assert.Equal(3000.0, bright.SpectralCentroidHz, 200.0);
    }

    /// <summary>The three bands partition the spectrum, so a caller may read them
    /// as shares. Anything else and a mapper that spends the "remaining" energy
    /// is spending energy that does not exist.</summary>
    [Theory]
    [MemberData(nameof(EverySignal))]
    public void TheThreeBandsSumToOne(string name)
    {
        var profile = ToneAudioAnalyzer.Analyze(Signal(name), Rate);

        if (profile.IsSilent) return; // nothing to apportion

        Assert.Equal(1.0, profile.LowEnergy + profile.MidEnergy + profile.HighEnergy, 9);
    }

    /// <summary>A tone of a known frequency has to land in the band the profile's
    /// own doc comment claims for it: &lt;250 Hz, 250 Hz–2 kHz, &gt;2 kHz.</summary>
    [Theory]
    [InlineData(120.0, "low")]
    [InlineData(900.0, "mid")]
    [InlineData(5000.0, "high")]
    public void AToneLandsInItsOwnBand(double frequencyHz, string band)
    {
        var profile = ToneAudioAnalyzer.Analyze(Sine(frequencyHz, 1.5, 0.8f), Rate);

        var share = band switch
        {
            "low" => profile.LowEnergy,
            "mid" => profile.MidEnergy,
            _ => profile.HighEnergy
        };

        Assert.True(share > 0.9,
            $"{frequencyHz} Hz put only {share:R} of its energy in the {band} band " +
            $"(low {profile.LowEnergy:R} / mid {profile.MidEnergy:R} / high {profile.HighEnergy:R})");
    }

    // ------------------------------------------------------------------- drive

    /// <summary>
    ///     <b>The claim the feature rests on.</b> Hard-clipping a played note
    ///     squashes the crest, fills the gaps between the partials and therefore
    ///     has to read as more driven. Asserted as an ORDERING between two
    ///     profiles measured from the SAME source signal — a threshold on the
    ///     clipped one alone would pass just as happily if the analyzer returned
    ///     a constant.
    /// </summary>
    [Fact]
    public void AClippedNote_ReadsAsMoreDrivenThanTheCleanNoteItWasMadeFrom()
    {
        var clean = Pluck(110.0, 2.0);
        var clipped = HardClip(clean, 0.06f);

        var cleanProfile = ToneAudioAnalyzer.Analyze(clean, Rate);
        var clippedProfile = ToneAudioAnalyzer.Analyze(clipped, Rate);

        output.WriteLine($"clean   {Describe(cleanProfile)}");
        output.WriteLine($"clipped {Describe(clippedProfile)}");

        Assert.True(clippedProfile.CrestFactorDb < cleanProfile.CrestFactorDb,
            $"clipping is what removes the peaks: {cleanProfile.CrestFactorDb:R} dB " +
            $"→ {clippedProfile.CrestFactorDb:R} dB");
        Assert.True(clippedProfile.SpectralFlatness > cleanProfile.SpectralFlatness,
            $"distortion fills the gaps between harmonics: {cleanProfile.SpectralFlatness:R} " +
            $"→ {clippedProfile.SpectralFlatness:R}");
        Assert.True(clippedProfile.Drive > cleanProfile.Drive + 0.1,
            $"Drive must separate them: {cleanProfile.Drive:R} → {clippedProfile.Drive:R}");
    }

    /// <summary>
    ///     The other half of the ordering: a clean, dynamic performance must NOT
    ///     read as driven, or every span in the song gets a distortion pedal.
    /// </summary>
    [Fact]
    public void ACleanDynamicPerformance_ReadsAsUndriven()
    {
        var profile = ToneAudioAnalyzer.Analyze(Pluck(110.0, 2.0), Rate);

        Assert.True(profile.CrestFactorDb > 10.0,
            $"a decaying note keeps a wide crest, got {profile.CrestFactorDb:R} dB");
        Assert.True(profile.Drive < 0.5, $"got Drive {profile.Drive:R}");
    }

    /// <summary>
    ///     Clipping raises the level that survives between attacks — that is what
    ///     "compressed" means to a player, and it is the pair a drive pedal
    ///     models.
    /// </summary>
    [Fact]
    public void ClippingRaisesSustain()
    {
        var clean = Pluck(110.0, 2.0);
        var clipped = HardClip(clean, 0.06f);

        var cleanProfile = ToneAudioAnalyzer.Analyze(clean, Rate);
        var clippedProfile = ToneAudioAnalyzer.Analyze(clipped, Rate);

        Assert.True(clippedProfile.Sustain > cleanProfile.Sustain,
            $"{cleanProfile.Sustain:R} → {clippedProfile.Sustain:R}");
    }

    /// <summary>A note that never decays holds its envelope, and one that decays
    /// to nothing does not — the two ends of the Sustain definition.</summary>
    [Fact]
    public void Sustain_SeparatesASustainedNoteFromADecayingOne()
    {
        var held = ToneAudioAnalyzer.Analyze(Sine(220.0, 2.0, 0.8f), Rate);
        var decaying = ToneAudioAnalyzer.Analyze(Pluck(110.0, 2.0), Rate);

        Assert.True(held.Sustain > 0.9, $"a held sine barely moves, got {held.Sustain:R}");
        Assert.True(decaying.Sustain < held.Sustain,
            $"a decaying pluck must sustain less: {decaying.Sustain:R} vs {held.Sustain:R}");
    }

    // ------------------------------------------------------------- robustness

    /// <summary>Same span in, identical struct out — the project's determinism
    /// contract, from inside this class. The cross-pipeline half is in
    /// <see cref="AnalysisDeterminismTests" />.</summary>
    [Fact]
    public void AnalyzingTheSameSpanTwice_ProducesTheIdenticalProfile()
    {
        var buffer = Mixed();

        var first = ToneAudioAnalyzer.Analyze(buffer, Rate);
        var second = ToneAudioAnalyzer.Analyze(buffer, Rate);

        Assert.Equal(first, second);
        foreach (var ((field, a), (_, b)) in Fields(first).Zip(Fields(second)))
            Assert.True(a.Equals(b), $"{field} drifted: {a:R} vs {b:R}");
    }

    /// <summary>
    ///     A charter can drop two tone changes a few milliseconds apart. The span
    ///     between them is shorter than one analysis frame and must come back with
    ///     an answer rather than an exception out of the FFT.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(ToneAudioAnalyzer.FrameSize - 1)]
    public void ASpanShorterThanOneFrame_IsMeasuredRatherThanRefused(int length)
    {
        var buffer = new float[length];
        for (var i = 0; i < length; i++) buffer[i] = 0.5f * MathF.Sin(2f * MathF.PI * 220f * i / Rate);

        var profile = ToneAudioAnalyzer.Analyze(buffer, Rate);

        foreach (var (field, value) in Fields(profile))
            Assert.True(double.IsFinite(value), $"length {length}: {field} was {value}");
    }

    /// <summary>
    ///     A decoder glitch is neutralised where it ENTERS. One <c>NaN</c> is
    ///     smeared across every FFT bin, and every guard downstream is a
    ///     comparison it wins by losing.
    /// </summary>
    [Fact]
    public void ASingleNonFiniteSample_DoesNotPoisonTheProfile()
    {
        var buffer = Pluck(110.0, 2.0);
        var clean = ToneAudioAnalyzer.Analyze(buffer, Rate);

        buffer[Rate / 2] = float.NaN;
        buffer[Rate / 2 + 1] = float.PositiveInfinity;
        var poisoned = ToneAudioAnalyzer.Analyze(buffer, Rate);

        foreach (var (field, value) in Fields(poisoned))
            Assert.True(double.IsFinite(value), $"{field} was {value}");

        // Two samples out of 88,200 must not move the answer meaningfully.
        Assert.Equal(clean.Drive, poisoned.Drive, 2);
    }

    [Fact]
    public void ANonPositiveSampleRate_IsRejectedAtTheEntryPoint() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ToneAudioAnalyzer.Analyze(new float[1024], 0));

    // ------------------------------------------------------------------- slice

    [Fact]
    public void Slice_TakesTheSamplesBetweenTwoTimesAndClampsToTheBuffer()
    {
        var buffer = new float[Rate];

        Assert.Equal(Rate / 2, ToneAudioAnalyzer.Slice(buffer, Rate, 0.25, 0.75).Length);
        Assert.Equal(Rate / 2, ToneAudioAnalyzer.Slice(buffer, Rate, 0.5, 9.0).Length);
        Assert.True(ToneAudioAnalyzer.Slice(buffer, Rate, 0.75, 0.25).IsEmpty);
        Assert.True(ToneAudioAnalyzer.Slice(buffer, Rate, 5.0, 6.0).IsEmpty);
        Assert.True(ToneAudioAnalyzer.Slice(buffer, Rate, double.NaN, 1.0).IsEmpty);
    }

    // ------------------------------------------------------------------ report

    /// <summary>
    ///     Not an assertion — the calibration table the mapping stage reads. Every
    ///     number the mapper's thresholds are argued from comes from here, so it
    ///     is printed rather than hidden inside a passing test.
    /// </summary>
    [Fact]
    public void MeasuredProfiles_AreReportedForCalibration()
    {
        foreach (var name in Names)
            output.WriteLine($"{name,-16} {Describe(ToneAudioAnalyzer.Analyze(Signal(name), Rate))}");
    }

    private static string Describe(ToneAudioProfile p) =>
        string.Create(CultureInfo.InvariantCulture,
            $"rms {p.Rms:F4} peak {p.PeakDb,7:F2} crest {p.CrestFactorDb,6:F2} " +
            $"centroid {p.SpectralCentroidHz,8:F1} flat {p.SpectralFlatness:F4} " +
            $"harm {p.HarmonicRatio:F3} lmh {p.LowEnergy:F3}/{p.MidEnergy:F3}/{p.HighEnergy:F3} " +
            $"sus {p.Sustain:F3} DRIVE {p.Drive:F3}");

    private static IEnumerable<(string Field, double Value)> Fields(ToneAudioProfile p)
    {
        yield return (nameof(p.Rms), p.Rms);
        yield return (nameof(p.PeakDb), p.PeakDb);
        yield return (nameof(p.CrestFactorDb), p.CrestFactorDb);
        yield return (nameof(p.SpectralCentroidHz), p.SpectralCentroidHz);
        yield return (nameof(p.SpectralFlatness), p.SpectralFlatness);
        yield return (nameof(p.HarmonicRatio), p.HarmonicRatio);
        yield return (nameof(p.LowEnergy), p.LowEnergy);
        yield return (nameof(p.MidEnergy), p.MidEnergy);
        yield return (nameof(p.HighEnergy), p.HighEnergy);
        yield return (nameof(p.Sustain), p.Sustain);
        yield return (nameof(p.Drive), p.Drive);
    }

    // ----------------------------------------------------------------- signals
    //
    // Everything below is synthesised in-process from a fixed formula or a fixed
    // seed. Nothing reads a file, a clock or System.Random — two runs of this
    // class see bit-identical samples.

    private static readonly string[] Names =
        ["silence", "sine220", "sine3k", "noise", "pluck", "clipped", "mixed"];

    public static TheoryData<string> EverySignal()
    {
        var data = new TheoryData<string>();
        foreach (var name in Names) data.Add(name);
        return data;
    }

    private static float[] Signal(string name) => name switch
    {
        "silence" => new float[Rate],
        "sine220" => Sine(220.0, 2.0, 0.8f),
        "sine3k" => Sine(3000.0, 2.0, 0.8f),
        "noise" => WhiteNoise(2.0, 0.5f),
        "pluck" => Pluck(110.0, 2.0),
        "clipped" => HardClip(Pluck(110.0, 2.0), 0.06f),
        "mixed" => Mixed(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fixture")
    };

    private static float[] Sine(double frequencyHz, double seconds, float amplitude)
    {
        var buffer = new float[(int)(seconds * Rate)];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * i / Rate));
        return buffer;
    }

    /// <summary>A decaying, harmonically rich note — the shape a clean guitar
    /// actually has, and the only fixture whose crest is wide enough for the
    /// clean/driven ordering to mean anything.</summary>
    private static float[] Pluck(double frequencyHz, double seconds)
    {
        var buffer = new float[(int)(seconds * Rate)];
        AnalysisTestSignals.AddPluck(buffer, 0.0, frequencyHz, seconds, 0.5f);
        return buffer;
    }

    /// <summary>Three notes in one span — what a real tone-change region holds,
    /// and the case that would read as "flat" if the frames' spectra were
    /// averaged before flatness was taken.</summary>
    private static float[] Mixed()
    {
        var buffer = new float[3 * Rate];
        AnalysisTestSignals.AddPluck(buffer, 0.10, 110.00, 0.9);
        AnalysisTestSignals.AddPluck(buffer, 1.05, 146.83, 0.9);
        AnalysisTestSignals.AddPluck(buffer, 2.00, 196.00, 0.9);
        return buffer;
    }

    /// <summary>Hard clip, then restore the original peak so the two profiles are
    /// compared at the same level. Scaling cannot change the crest factor — it is
    /// a ratio — so the collapse the test asserts is the clipping alone.</summary>
    private static float[] HardClip(float[] source, float threshold)
    {
        var clipped = new float[source.Length];
        float peak = 0;
        for (var i = 0; i < source.Length; i++) peak = MathF.Max(peak, MathF.Abs(source[i]));

        for (var i = 0; i < source.Length; i++)
            clipped[i] = Math.Clamp(source[i], -threshold, threshold);

        var gain = peak > 0 ? peak / threshold : 1f;
        for (var i = 0; i < clipped.Length; i++) clipped[i] *= gain;
        return clipped;
    }

    /// <summary>White noise from a fixed-seed xorshift written out here on
    /// purpose: <see cref="Random" />'s algorithm is an implementation detail and
    /// has changed between runtimes, which would make this fixture — and the
    /// determinism claim resting on it — quietly framework-dependent.</summary>
    private static float[] WhiteNoise(double seconds, float amplitude)
    {
        var buffer = new float[(int)(seconds * Rate)];
        var state = 0x9E3779B9;
        for (var i = 0; i < buffer.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[i] = amplitude * (state / (float)uint.MaxValue * 2f - 1f);
        }

        return buffer;
    }
}
