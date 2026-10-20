using System.Globalization;
using System.Reflection;
using System.Text;
using DotCharter.Analysis.AudioToChart;
using DotCharter.Analysis.BeatMap;
using DotCharter.Analysis.Lyrics;
using DotCharter.Analysis.Tone;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     The determinism contract of <c>DotCharter.Analysis</c> — "the same input
///     always produces the same output" — guarded by RE-RUNNING each pipeline
///     rather than by prose.
///     <para>
///         Each analyzer already owns a hand-written <c>Analyze_IsDeterministic</c>
///         beside its behaviour tests. Two things those cannot do, and this class
///         does:
///     </para>
///     <list type="number">
///         <item>
///             <b>The catalogue is DERIVED, not listed.</b>
///             <see cref="EveryPublicAnalysisEntryPoint_IsDrivenByAReRunPipeline" />
///             reflects over the shipped assembly for every public type carrying an
///             <c>Analyze</c>/<c>Align</c> entry point and demands a runner for each,
///             so a SIXTH analyzer cannot ship with no determinism coverage — the
///             failure mode a hand-maintained list has by construction. A name alone
///             does not satisfy it: the runner is a delegate that must actually
///             execute the pipeline and return its output, so "covered" and
///             "re-run and compared" are the same act.
///         </item>
///         <item>
///             <b>Execution HISTORY is varied.</b> Every existing determinism test
///             calls one analyzer twice in a row in a clean process, so it is blind
///             to state shared between instances or between analyzers — a static
///             cache, a pooled buffer, a lazily built kernel table.
///             <see cref="APipelinesOutputDoesNotDependOnWhatRanBeforeIt" /> runs the
///             subject, then runs every OTHER pipeline, then runs the subject again.
///         </item>
///     </list>
///     <para>
///         <b>What this does NOT prove.</b> The fixtures are synthesised (plucks,
///         clicks, hand-built emission matrices), so this says nothing about
///         accuracy on real recordings — it is a repeatability guard, not a quality
///         one. It also runs in ONE process, so it cannot see per-process
///         randomization (notably <see cref="string" /> hashing, which reorders
///         <c>Dictionary&lt;string,…&gt;</c> enumeration between runs) or
///         cross-machine floating-point differences. Every runner asserts its
///         output is non-trivial precisely so the comparison can never pass by
///         comparing two empty results.
///     </para>
/// </summary>
public sealed class AnalysisDeterminismTests
{
    private const int Rate = AnalysisTestSignals.SampleRate;

    /// <summary>One analysis entry point plus a runner that drives it end to end
    /// and folds its WHOLE output into a comparable string.</summary>
    private sealed record Pipeline(string TypeName, Func<string> Run);

    private static Pipeline[] Pipelines() =>
    [
        new(nameof(AudioToChartAnalyzer), RunAudioToChart),
        new(nameof(BeatMapAnalyzer), RunBeatMap),
        new(nameof(ChartAudioLintAnalyzer), RunChartAudioLint),
        new(nameof(CtcForcedAligner), RunCtcForcedAligner),
        new(nameof(LyricAligner), RunLyricAligner),
        new(nameof(RecordingAligner), RunRecordingAligner),
        new(nameof(ToneAudioAnalyzer), RunToneAudio)
    ];

    public static TheoryData<string> PipelineNames()
    {
        var data = new TheoryData<string>();
        foreach (var pipeline in Pipelines()) data.Add(pipeline.TypeName);
        return data;
    }

    /// <summary>
    ///     The coverage half: the set of public <c>Analyze</c>/<c>Align</c> entry
    ///     points in the shipped assembly must be exactly the set this class
    ///     re-runs. Adding an analyzer fails this until a runner exists for it,
    ///     and a runner is by definition a re-run.
    /// </summary>
    [Fact]
    public void EveryPublicAnalysisEntryPoint_IsDrivenByAReRunPipeline()
    {
        var declared = typeof(AudioToChartAnalyzer).Assembly.GetExportedTypes()
            .Where(t => t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.DeclaredOnly)
                .Any(m => m.Name is "Analyze" or "Align"))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var covered = Pipelines()
            .Select(p => p.TypeName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, covered);
    }

    [Theory]
    [MemberData(nameof(PipelineNames))]
    public void APipelineProducesTheIdenticalOutputWhenItIsRunAgain(string name)
    {
        var run = Single(name).Run;

        var first = run();
        var second = run();

        Assert.Equal(first, second);
    }

    /// <summary>
    ///     The blind spot the per-analyzer tests share: they call ONE analyzer
    ///     twice in a row. Anything cached in a static, pooled across instances or
    ///     built lazily on first use is identical on both of those calls and would
    ///     never show. Here the subject is run, every other pipeline is run against
    ///     it, and the subject is run again.
    /// </summary>
    [Theory]
    [MemberData(nameof(PipelineNames))]
    public void APipelinesOutputDoesNotDependOnWhatRanBeforeIt(string name)
    {
        var subject = Single(name);

        var alone = subject.Run();
        foreach (var other in Pipelines())
        {
            if (!string.Equals(other.TypeName, name, StringComparison.Ordinal))
                other.Run();
        }

        Assert.Equal(alone, subject.Run());
    }

    private static Pipeline Single(string name) =>
        Pipelines().Single(p => string.Equals(p.TypeName, name, StringComparison.Ordinal));

    // ------------------------------------------------------------- runners
    //
    // Each returns the analyzer's whole output, field by field, with "R"
    // round-trip formatting — a comparison of rounded text would hide exactly
    // the last-bit drift these tests exist to catch.

    private static string RunAudioToChart()
    {
        var buffer = new float[(int)(3.0 * Rate)];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 110.00); // A2
        AnalysisTestSignals.AddPluck(buffer, 1.00, 146.83); // D3
        AnalysisTestSignals.AddPluck(buffer, 1.75, 196.00); // G3

        // Both pitch stages: the constant-Q one (#391) is what the dialog drafts with.
        var text = new StringBuilder();
        foreach (var stage in new[] { AudioToChartPitchStage.Detector, AudioToChartPitchStage.ConstantQ })
        {
            var draft = new AudioToChartAnalyzer().Analyze(
                buffer, Rate, AnalysisTestSignals.ConstantBeats(120, 8),
                new AudioToChartOptions { MaxNotesPerOnset = 1, PitchStage = stage });

            Assert.NotEmpty(draft.Notes);
            text.Append(stage).Append('\n');
            foreach (var note in draft.Notes)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"{note.TimeSeconds:R}|{note.Midi}|{note.FrequencyHz:R}|{note.Confidence:R}|" +
                    $"{note.Note.StringIndex}|{note.Note.Fret}|{note.Note.Position.DoubleValue:R}|{note.Note.Hopo}\n");
            }
        }

        return text.ToString();
    }

    private static string RunBeatMap()
    {
        var buffer = new float[(int)(30.0 * Rate)];
        var interval = 60.0 / 120.0;
        var index = 0;
        for (var t = 0.5; t < 30.0 - interval; t += interval, index++)
            AnalysisTestSignals.AddClick(buffer, t, index % 4 == 0 ? 0.9f : 0.45f);

        var result = new BeatMapAnalyzer().Analyze(buffer, Rate);

        Assert.NotEmpty(result.Beats);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{result.AverageBpm:R}\n");
        foreach (var beat in result.Beats)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{beat.TimeSeconds:R}|{beat.Bpm:R}|{beat.Confidence:R}|{beat.IsDownbeat}\n");
        }

        return text.ToString();
    }

    private static string RunChartAudioLint()
    {
        var buffer = new float[(int)(2.0 * Rate)];
        AnalysisTestSignals.AddPluck(buffer, 0.75, 220.0, 1.0); // A3, midi 57

        // One pitch that sounds and one that does not, so the result carries both
        // a verification and a finding — a run that produced neither would compare
        // two empty reports and prove nothing.
        var result = ChartAudioLintAnalyzer.Analyze(buffer, Rate,
        [
            new LintExpectedSound(750, [57], IsChord: false),
            new LintExpectedSound(750, [62], IsChord: false)
        ]);

        Assert.Equal(2, result.SoundsChecked);
        Assert.NotEmpty(result.Findings);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{result.SoundsChecked}|{result.SoundsVerified}\n");
        foreach (var finding in result.Findings)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{finding.TimeMs:R}|{finding.ChartedMidi}|{finding.Confidence:R}|" +
                $"{finding.ObservedMidi}|{finding.ObservedConfidence:R}\n");
        }

        return text.ToString();
    }

    private static string RunCtcForcedAligner()
    {
        const int width = 4, blank = 0, a = 1, b = 2;
        var result = CtcForcedAligner.Align(
            DominantFrames(width, [a, a, a, b, b, b]), frameCount: 6, width, [a, b], blank);

        Assert.True(result.Succeeded, result.Failure);
        Assert.NotEmpty(result.Tokens);

        return Describe(result);
    }

    private static string RunLyricAligner()
    {
        // The shipping vocabulary shape: <blank> first, then single-character
        // tokens (see LyricVocabulary.FromTokens).
        var vocabulary = LyricVocabulary.FromTokens(["<blank>", "a", "b", "z"]);
        var sheet = LyricSheetParser.Parse("ab za");
        var emissions = new CtcEmissions(
            DominantFrames(4, [1, 1, 2, 2, 3, 3, 1, 1]), FrameCount: 8, Width: 4, SecondsPerFrame: 0.02);

        var result = LyricAligner.Align(sheet, vocabulary, emissions);

        Assert.True(result.Succeeded, result.Failure);
        Assert.NotEmpty(result.Syllables);

        var text = new StringBuilder();
        foreach (var syllable in result.Syllables)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{syllable.SyllableIndex}|{syllable.StartSeconds:R}|{syllable.EndSeconds:R}|" +
                $"{syllable.Confidence:R}|{syllable.IsInterpolated}\n");
        }

        return text.ToString();
    }

    private static string RunRecordingAligner()
    {
        // The drifting-tempo fixture with one section played wrong, so the coarse warp, the
        // beat placement and every term of the confidence (including its low arm) all run.
        var fixture = new RecordingAlignmentFixture(mismatchedSection: 2);
        var result = fixture.Align(fixture.Grid(i => i == 20), TestContext.Current.CancellationToken);

        Assert.Equal(RecordingAlignmentFixture.Beats, result.BeatTimesMs.Count);

        var text = new StringBuilder();
        foreach (var t in result.BeatTimesMs) text.Append(CultureInfo.InvariantCulture, $"{t:R}\n");
        foreach (var s in result.Sections)
            text.Append(CultureInfo.InvariantCulture, $"{s.StartBeat}|{s.EndBeat}|{s.Confidence:R}|{s.EventCount}|{s.MeanShiftMs:R}\n");
        return text.ToString();
    }

    private static string RunToneAudio()
    {
        // Three plucked notes and a noise bed: the span carries a fundamental to
        // find AND unpitched content, so both branches of the harmonic-ratio
        // decision run. A clean single tone would compare two answers that never
        // exercised the detector's failure path.
        var buffer = new float[(int)(3.0 * Rate)];
        AnalysisTestSignals.AddPluck(buffer, 0.10, 110.00);
        AnalysisTestSignals.AddPluck(buffer, 1.05, 146.83);
        AnalysisTestSignals.AddPluck(buffer, 2.00, 196.00);

        var state = 0x9E3779B9;
        for (var i = 0; i < buffer.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[i] += 0.01f * (state / (float)uint.MaxValue * 2f - 1f);
        }

        var profile = ToneAudioAnalyzer.Analyze(buffer, Rate);

        Assert.False(profile.IsSilent);
        Assert.True(profile.SpectralCentroidHz > 0);

        return string.Create(CultureInfo.InvariantCulture,
            $"{profile.Rms:R}|{profile.PeakDb:R}|{profile.CrestFactorDb:R}|" +
            $"{profile.SpectralCentroidHz:R}|{profile.SpectralFlatness:R}|{profile.HarmonicRatio:R}|" +
            $"{profile.LowEnergy:R}|{profile.MidEnergy:R}|{profile.HighEnergy:R}|" +
            $"{profile.Sustain:R}|{profile.Drive:R}\n");
    }

    private static string Describe(CtcAlignment alignment)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{alignment.Succeeded}|{alignment.TotalLogProb:R}|{alignment.Failure}\n");
        foreach (var token in alignment.Tokens)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{token.StartFrame}|{token.EndFrame}|{token.Score:R}\n");
        }

        return text.ToString();
    }

    /// <summary>A log-softmax emission block where one named symbol carries 97 %
    /// of each frame and the rest share what is left.</summary>
    private static float[] DominantFrames(int width, int[] dominantPerFrame)
    {
        var logProbs = new float[dominantPerFrame.Length * width];
        var rest = 0.03 / (width - 1);
        for (var frame = 0; frame < dominantPerFrame.Length; frame++)
        {
            for (var symbol = 0; symbol < width; symbol++)
            {
                logProbs[frame * width + symbol] =
                (float)Math.Log(symbol == dominantPerFrame[frame] ? 0.97 : rest);
            }
        }

        return logProbs;
    }
}
