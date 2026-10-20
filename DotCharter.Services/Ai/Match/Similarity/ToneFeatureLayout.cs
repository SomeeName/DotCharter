namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     The map of <c>tonefx-v1</c>: where each block of
///     <see cref="ToneFeatures" />.<c>Vector</c> starts, how long it is, and how much
///     <see cref="ToneSimilarity" /> weights it.
///     <para>
///         <b>This is the ONE place the layout and the weights live.</b> The extractor
///         writes through these offsets and the distance reads through them, so a
///         block cannot silently move under the metric. Any change to a length, an
///         order or a scale is a NEW EXTRACTOR: bump
///         <see cref="ToneFeatureExtractor.ExtractorId" /> so stored vectors from the
///         old one are rejected instead of compared.
///     </para>
///     <para>
///         Every value in the vector is carried in a unit where "1.0" is a large
///         perceptual step — dB/40, log2 octaves/4 — so that the per-element
///         differences are commensurate BEFORE weighting. The weights then say which
///         block matters, and dividing by the block length says that a 144-element
///         block does not outvote a 5-element one by arithmetic alone.
///     </para>
/// </summary>
public static class ToneFeatureLayout
{
    /// <summary>STFT sizes the band statistics are taken at, in samples at
    /// <see cref="ToneFeatureExtractor.WorkingRate" />. Short = transients and pick
    /// attack, long = the steady spectral envelope; a tone that matches at one scale
    /// and not the other is a different tone.</summary>
    public static readonly int[] StftScales = [1024, 4096, 16384];

    /// <summary>
    ///     Log-spaced bands per scale in the band-statistics block.
    ///     <para>
    ///         <b>48 bands over 60 Hz–10 kHz is 0.154 octave each — finer than
    ///         1/3-octave, deliberately.</b> The search lane found that a filter's
    ///         BANDWIDTH (an EQ's Q) is unidentifiable at 1/3-octave resolution: a
    ///         narrow notch and a wide one move the same two bands by the same amount,
    ///         so the distance cannot rank them and the optimiser wanders. Every
    ///         doubling costs arithmetic and nothing else here — this runs offline.
    ///     </para>
    /// </summary>
    public const int LogBands = 48;

    /// <summary>
    ///     Bands the cepstral coefficients are taken over — <b>COARSER than the
    ///     statistics block, which is the opposite of the usual advice and was
    ///     measured.</b>
    ///     <para>
    ///         The two banks have opposite jobs. The statistics block must RESOLVE a
    ///         filter, so it is fine. The cepstral block must SMOOTH a spectrum into
    ///         an envelope, and a fine log bank does not: below ~400 Hz a 0.1-octave
    ///         band is narrower than the gap between a guitar's partials, so each
    ///         partial lands in its own band and the "envelope" becomes a description
    ///         of the chord. At 64 bands / 20 coefficients the cepstral block cost
    ///         0.046 between two performances of ONE rig against 0.011 between two
    ///         rigs — it was measuring the notes four times more strongly than the
    ///         tone. At 32 / 12 it is the other way round.
    ///     </para>
    /// </summary>
    public const int CepstralBands = 32;

    /// <summary>Cepstral coefficients kept, c0 excluded (the level lives in its own
    /// block). Twelve: enough for the broad tilt, the mid scoop and the presence peak
    /// a rig imposes, and too few to describe a chord.</summary>
    public const int MfccCoefficients = 12;

    /// <summary>Per-band log MEAN, all three scales.</summary>
    public const int BandMeanOffset = 0;

    /// <summary>Length of the band-mean block.</summary>
    public const int BandMeanLength = 3 * LogBands;

    /// <summary>Per-band log STD, all three scales.</summary>
    public const int BandStdOffset = BandMeanOffset + BandMeanLength;

    /// <summary>Length of the band-spread block.</summary>
    public const int BandStdLength = 3 * LogBands;

    /// <summary>MFCC mean — the spectral envelope, the most content-invariant thing
    /// in the vector.</summary>
    public const int MfccMeanOffset = BandStdOffset + BandStdLength;

    /// <summary>Length of the MFCC mean block.</summary>
    public const int MfccMeanLength = MfccCoefficients;

    /// <summary>MFCC std, then MFCC mean-absolute-delta.</summary>
    public const int MfccVariationOffset = MfccMeanOffset + MfccMeanLength;

    /// <summary>Length of the MFCC variation block.</summary>
    public const int MfccVariationLength = MfccCoefficients * 2;

    /// <summary>Centroid, spread, rolloff85, flatness, flux — the MEAN of each.</summary>
    public const int ShapeOffset = MfccVariationOffset + MfccVariationLength;

    /// <summary>Length of the spectral-shape mean block.</summary>
    public const int ShapeLength = 5;

    /// <summary>The STD of the same five.</summary>
    public const int ShapeVariationOffset = ShapeOffset + ShapeLength;

    /// <summary>Length of the spectral-shape spread block.</summary>
    public const int ShapeVariationLength = 5;

    /// <summary>Crest factor and the frame-RMS distribution (the compression proxy).</summary>
    public const int DynamicsOffset = ShapeVariationOffset + ShapeVariationLength;

    /// <summary>Length of the dynamics block.</summary>
    public const int DynamicsLength = 5;

    /// <summary>Harmonic-to-noise and 2–6 kHz flatness (the distortion proxy).</summary>
    public const int DistortionOffset = DynamicsOffset + DynamicsLength;

    /// <summary>Length of the distortion block.</summary>
    public const int DistortionLength = 4;

    /// <summary>
    ///     Envelope autocorrelation and modulation spectrum — the TIME-varying block.
    ///     <para>
    ///         <b>Added because a long-term average spectrum cannot see a delay.</b>
    ///         Every other block here averages over frames, and averaging is exactly
    ///         what destroys the evidence for a repeat, a tremolo or a chorus: a
    ///         delayed copy of a signal has the same long-term spectrum as the signal.
    ///         The search lane hit this as a confident match, far below the floor, to
    ///         a rig with the wrong delay entirely — the distance could not tell,
    ///         so the optimiser was free to put the feedback anywhere.
    ///     </para>
    /// </summary>
    public const int ModulationOffset = DistortionOffset + DistortionLength;

    /// <summary>Length of the modulation block.</summary>
    public const int ModulationLength = 12;

    /// <summary>Absolute RMS and peak, the only level-dependent values in the vector.</summary>
    public const int LevelOffset = ModulationOffset + ModulationLength;

    /// <summary>Length of the level block.</summary>
    public const int LevelLength = 2;

    /// <summary>Total length of a <c>tonefx-v1</c> vector.</summary>
    public const int Length = LevelOffset + LevelLength;

    /// <summary>One block: where it is, and what it is called in a report.</summary>
    public sealed record Block(string Name, int Offset, int Count, double Weight);

    /// <summary>
    ///     The weighted blocks, in vector order.
    ///     <para>
    ///         <b>The split between a statistic's MEAN and its SPREAD is the whole
    ///         design, and it was measured rather than guessed.</b> The first cut of
    ///         this metric weighted "the MFCCs" as one block and scored a rig change
    ///         at 1.04× a riff change — i.e. it could not tell them apart at all. The
    ///         breakdown said why: the MFCC MEAN (the envelope) barely moves between
    ///         two performances through one rig, while the MFCC std and delta move a
    ///         lot, because how much the timbre VARIES across a take is mostly a fact
    ///         about the notes. Same for the spectral bands. Splitting each statistic into
    ///         its mean and its spread, and weighting the spread down by roughly an
    ///         order of magnitude, is what makes the metric hear the rig.
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>mfccMean 1.2</b> — the spectral envelope, and the thing the
    ///             timbre literature keeps finding competitive with trained embeddings
    ///             across 2,614 human pairwise ratings (arXiv 2507.07764).
    ///         </item>
    ///         <item>
    ///             <b>modulation 0.6</b> — the only block that can hear a delay time,
    ///             a delay's feedback or a modulation rate at all. Weighted to matter
    ///             without letting a busy riff's own rhythm dominate a comparison.
    ///         </item>
    ///         <item>
    ///             <b>bandMean 0.5</b> — band-level detail the low cepstrum smooths
    ///             away: a narrow mid scoop, a cab's presence peak. Log-spaced and finer than
    ///             1/3-octave, so a filter's bandwidth is visible.
    ///         </item>
    ///         <item>
    ///             <b>dynamics 1.2</b> — measured, the single most rig-discriminative
    ///             block there is: a clean amp and a fuzz differ nineteen times more
    ///             here than two performances of one rig do, because distortion
    ///             flattens the frame-RMS distribution and the notes barely touch it.
    ///         </item>
    ///         <item>
    ///             <b>distortion 0.6</b> — the same axis measured spectrally, and
    ///             weaker than it looks: a fixed 2-6 kHz band reads PITCH as drive, so
    ///             it is worth less than the dynamics block that measures the same
    ///             thing in the time domain.
    ///         </item>
    ///         <item>
    ///             <b>…Std / …Variation 0.1</b> — not zero. A tremolo, a phaser and a
    ///             compressor breathing exist only in the variation, and a metric
    ///             blind to it cannot hear modulation at all; it is simply not allowed
    ///             to outvote the envelope.
    ///         </item>
    ///         <item>
    ///             <b>level 0.15</b> — deliberately small and deliberately NOT zero. A
    ///             tone 6 dB hotter is a slightly different answer, but level is the
    ///             easiest thing in a chain to fix afterwards.
    ///         </item>
    ///     </list>
    /// </summary>
    public static readonly Block[] Blocks =
    [
        new("bandMean", BandMeanOffset, BandMeanLength, 0.5),
        new("bandStd", BandStdOffset, BandStdLength, 0.05),
        new("mfccMean", MfccMeanOffset, MfccMeanLength, 0.5),
        new("mfccVar", MfccVariationOffset, MfccVariationLength, 0.1),
        new("shape", ShapeOffset, ShapeLength, 0.5),
        new("shapeVar", ShapeVariationOffset, ShapeVariationLength, 0.05),
        new("dynamics", DynamicsOffset, DynamicsLength, 1.2),
        new("distortion", DistortionOffset, DistortionLength, 0.6),
        new("modulation", ModulationOffset, ModulationLength, 0.6),
        new("level", LevelOffset, LevelLength, 0.15)
    ];
}
