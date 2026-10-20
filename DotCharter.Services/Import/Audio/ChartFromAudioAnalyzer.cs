using DotCharter.Analysis.AudioToChart;

namespace DotCharter.Services.Import.Audio;

/// <summary>
///     What the chart-from-audio wizard drafted: the chartable notes, their mean
///     confidence (0–1), the confidence floor the sensitivity mapped to, and the
///     separated stem it listened to instead of the chosen file (<c>null</c>: the
///     file itself — see <see cref="ChartFromAudioStemLocator" />). <c>Confidences</c> is each
/// note's own confidence, parallel to <c>Notes</c> — the merge review's per-phrase figure;
///     null when only the mean is known.
/// </summary>
public sealed record ChartFromAudioDraft(IReadOnlyList<GuitarNote> Notes, double AverageConfidence, double MinConfidence,
    string? StemName = null, IReadOnlyList<double>? Confidences = null);

/// <summary>
///     The chart-from-audio wizard's one verb: read a WAV and draft notes on a
///     beat grid for an arrangement. A seam because the analyzer lives in
///     <c>DotCharter.Analysis</c>, which the presentation layer may not reference.
///     Synchronous and slow — the caller runs it off the UI thread. It takes no
///     token, because neither the WAV reader nor the analyzer accepts one.
/// </summary>
public interface IChartFromAudioAnalyzer
{
    /// <summary><c>sensitivity</c> is 0–100; higher keeps more, lower-confidence, notes.</summary>
    ChartFromAudioDraft Analyze(string wavPath, IReadOnlyList<BeatMarker> beats, Arrangement? template,
        double sensitivity);
}

/// <summary>
///     The wizard's analyzer. Listens to the arrangement instrument's ALREADY
///     separated stem when <see cref="IChartFromAudioStemLocator" /> finds one for the
///     chosen file, and to the file itself otherwise; it never separates.
/// </summary>
/// <param name="stems">Finds the stem; <c>null</c> reads the app's own stem cache.</param>
public sealed class ChartFromAudioAnalyzer(IChartFromAudioStemLocator? stems = null) : IChartFromAudioAnalyzer
{
    private readonly IChartFromAudioStemLocator _stems = stems ?? new ChartFromAudioStemLocator();

    /// <summary>The wizard's sensitivity slider before the user touches it — and
    /// the setting the auto-chart corpus score (<c>AutoChartCorpusProbeTests</c>)
    /// is measured at, so the published number is the out-of-the-box draft.</summary>
    public const double DefaultSensitivity = 50;

    /// <summary>Sensitivity 0 is strict (0.35), 100 keeps almost everything (0.02).</summary>
    public static double MinConfidenceFor(double sensitivity) => 0.35 - sensitivity / 100.0 * 0.33;

    /// <summary>
    ///     The analyzer options the wizard drafts with for <paramref name="template" />
    ///     at <paramref name="sensitivity" />. The draft is stamped with THIS
    ///     arrangement's tuning, string count and capo when it is applied, so the
    ///     pitch→fret map is built from the same three or the two disagree — see
    ///     <see cref="ChartFromAudioOptions" />. Public so the corpus harness scores
    ///     exactly the options the dialog uses rather than a copy of them.
    /// </summary>
    public static AudioToChartOptions OptionsFor(Arrangement? template, double sensitivity) => new()
    {
        MinConfidence = (float)MinConfidenceFor(sensitivity),
        OpenStringMidi = ChartFromAudioOptions.OpenStringMidiFor(template),
        MinFret = ChartFromAudioOptions.MinFretFor(template),
        MaxNotesPerOnset = MaxNotesPerOnsetFor(template),
        PitchStage = AudioToChartPitchStage.ConstantQ
    };

    /// <summary>
    /// How many pitches one onset may become. A bass part is charted as single
    ///     notes, and three on every bass onset (1.9 on average) is too many;
    ///     two keeps the pitch stage's second guess for the octave it most often confuses
    ///     and, measured on the corpus with the constant-Q stage, scores the same note F1
    ///     as three (full mix 10.6 vs 10.7 %, bass stems 23.1 vs 23.2 %) with 29 % fewer
    ///     notes to delete. One costs recall (9.3 %). Guitar keeps three.
    /// </summary>
    public static int MaxNotesPerOnsetFor(Arrangement? template) =>
        template?.ArrangementType == ArrangementType.Bass ? BassMaxNotesPerOnset : GuitarMaxNotesPerOnset;

    /// <summary>Pitches per onset for a bass arrangement.</summary>
    public const int BassMaxNotesPerOnset = 2;

    /// <summary>Pitches per onset for every other arrangement.</summary>
    public const int GuitarMaxNotesPerOnset = 3;

    public ChartFromAudioDraft Analyze(string wavPath, IReadOnlyList<BeatMarker> beats, Arrangement? template,
        double sensitivity)
    {
        var options = OptionsFor(template, sensitivity);
        var stem = _stems.TryLocate(wavPath, template?.ArrangementType);
        var (samples, sampleRate) = stem == null ? WavMonoReader.Read(wavPath) : WavMonoReader.Read(stem.ReadWavBytes());
        var draft = new AudioToChartAnalyzer().Analyze(samples, sampleRate, beats.ToList(), options);
        var average = draft.Notes.Count == 0 ? 0 : draft.Notes.Average(n => n.Confidence);
        return new ChartFromAudioDraft(draft.Notes.Select(n => n.Note).ToList(), average, options.MinConfidence,
            stem?.StemName, draft.Notes.Select(n => (double)n.Confidence).ToList());
    }
}
