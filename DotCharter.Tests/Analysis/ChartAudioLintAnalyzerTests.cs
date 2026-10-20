using DotCharter.Analysis.AudioToChart;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     v0.29 audio-verified chart lint: fully synthetic — a rendered tone
///     either confirms its charted pitch or produces a finding naming the
///     pitch that actually sounded. Deterministic per the Analysis contract.
/// </summary>
public sealed class ChartAudioLintAnalyzerTests
{
    private const int Sr = 44100;

    /// <summary>0.5 s silence + 1.5 s of a plucked-ish tone + 0.5 s silence.
    /// The silence matters: the offline noise floor is a low percentile of
    /// the WHOLE clip, so an all-signal clip would estimate "noise" at
    /// playing level and gate everything away. Harmonics make the presence
    /// test realistic (and feed the sub-octave/harmonic rules real energy).</summary>
    private static float[] RenderTone(double midi)
    {
        var n = (int)(Sr * 2.5);
        var samples = new float[n];
        var f0 = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
        var start = (int)(Sr * 0.5);
        var end = (int)(Sr * 2.0);
        for (var i = start; i < end; i++)
        {
            var t = (i - start) / (double)Sr;
            var envelope = Math.Min(1.0, t * 50.0) * Math.Exp(-t * 0.8);
            samples[i] = (float)(envelope * (
                0.60 * Math.Sin(2 * Math.PI * f0 * t) +
                0.25 * Math.Sin(2 * Math.PI * f0 * 2 * t) +
                0.10 * Math.Sin(2 * Math.PI * f0 * 3 * t)));
        }

        return samples;
    }

    private static LintExpectedSound Note(double timeMs, int midi) => new(timeMs, [midi], IsChord: false);

    [Fact]
    public void ACorrectlyChartedNote_Verifies_WithNoFindings()
    {
        var result = ChartAudioLintAnalyzer.Analyze(RenderTone(57), Sr, [Note(750, 57)]);

        Assert.Equal(1, result.SoundsChecked);
        Assert.Equal(1, result.SoundsVerified);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void AWrongChartedPitch_Fails_AndNamesWhatTheAudioSays()
    {
        // Audio plays A3 (57); the chart claims B3 (59).
        var result = ChartAudioLintAnalyzer.Analyze(RenderTone(57), Sr, [Note(750, 59)]);

        Assert.Equal(0, result.SoundsVerified);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(59, finding.ChartedMidi);
        Assert.Equal(57, finding.ObservedMidi);
        Assert.True(finding.ObservedConfidence > 0);
    }

    [Fact]
    public void ANoteChartedIntoSilence_Fails_WithNothingObserved()
    {
        var result = ChartAudioLintAnalyzer.Analyze(new float[Sr * 2], Sr, [Note(1000, 57)]);

        var finding = Assert.Single(result.Findings);
        Assert.Null(finding.ObservedMidi);
    }

    [Fact]
    public void UnpitchedSounds_AreSkippedEntirely()
    {
        var result = ChartAudioLintAnalyzer.Analyze(RenderTone(57), Sr,
            [new LintExpectedSound(750, [57], IsChord: false, IsPitched: false)]);

        Assert.Equal(0, result.SoundsChecked);
        Assert.Empty(result.Findings);
    }

    /// <summary>
    ///     A chord's findings come out in the CHART's string order, and the dedup
    ///     that removes doubled octaves must not reorder them. This is output a
    ///     charter reads top to bottom, and the determinism contract is
    ///     byte-identical output — not "whatever <c>HashSet&lt;int&gt;</c>
    ///     enumerates on the runtime build we happen to ship", which is
    ///     explicitly unspecified.
    /// </summary>
    [Fact]
    public void AChordsFindings_ComeOutInTheChartedOrder_WithDuplicatesRemoved()
    {
        // Silence, so every charted pitch fails and every one produces a finding.
        // Deliberately descending, with midi 64 doubled: an insertion-ordered
        // container preserves 64, 59, 55; a sorted or hash-ordered one does not.
        var chord = new LintExpectedSound(1000, [64, 59, 64, 55], IsChord: true);

        var result = ChartAudioLintAnalyzer.Analyze(new float[Sr * 2], Sr, [chord]);

        Assert.Equal([64, 59, 55], result.Findings.Select(f => f.ChartedMidi));
        Assert.Equal(1, result.SoundsChecked);
        Assert.Equal(0, result.SoundsVerified);
    }

    /// <summary>
    ///     A pitch the CQT cannot address is UNKNOWABLE, not wrong. The shipping range is
    ///     <c>LiveCqtConfig.MinMidi</c> up 6 octaves and a bit at quarter-tone resolution
    ///     (<c>BinCount</c> bins), so anything below the floor or at/over the ceiling has no
    ///     bin — <c>JuceChartVerifier.Verify</c> returns a flat no-hit for it, and the probe
    ///     (which only looks NEARBY) finds nothing either. Before the guard that meant a
    ///     5-string bass's low B filed one "charted B0 — silent" finding per note, on a chart
    ///     that is completely correct. Relative to the config on purpose: these cases named
    ///     MIDI 23 / 26 / 27 as "under the floor" while it was E1, and those are exactly the
    ///     notes the floor was lowered to A0 to CHECK in 0.57.4.
    /// </summary>
    [Theory]
    [InlineData(-1)]   // one semitone under the floor
    [InlineData(-5)]   // well under it
    [InlineData(+1)]   // one semitone over the ceiling
    public void APitchOutsideTheVerifiableRange_IsNotChecked_AndFilesNoFinding(int beyondEdge)
    {
        var midi = beyondEdge < 0 ? Floor + beyondEdge : Ceiling + beyondEdge - 1;

        var result = ChartAudioLintAnalyzer.Analyze(RenderTone(57), Sr, [Note(750, midi)]);

        Assert.Equal(0, result.SoundsChecked);
        Assert.Equal(0, result.SoundsVerified);
        Assert.Empty(result.Findings);
    }

    /// <summary>The edges themselves ARE checked — the guard must not shave a
    /// semitone off a range the live verifier can hear.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APitchOnTheEdgeOfTheVerifiableRange_IsStillChecked(bool bottom)
    {
        var midi = bottom ? Floor : Ceiling - 1;

        var result = ChartAudioLintAnalyzer.Analyze(new float[Sr * 2], Sr, [Note(1000, midi)]);

        Assert.Equal(1, result.SoundsChecked);
        Assert.Single(result.Findings);
    }

    /// <summary>A 5-string bass's low B and a bass dropped to D: below E1, and so
    /// unknowable to the lint until the range moved down to A0. Now they are checked.</summary>
    [Theory]
    [InlineData(23)]
    [InlineData(26)]
    public void ALowBassNote_IsChecked(int midi)
    {
        var result = ChartAudioLintAnalyzer.Analyze(new float[Sr * 2], Sr, [Note(1000, midi)]);

        Assert.Equal(1, result.SoundsChecked);
    }

    /// <summary>A chord straddling the floor keeps its reachable tones: only
    /// the unreachable one drops out, and the chord still counts as checked.</summary>
    [Fact]
    public void AChordStraddlingTheFloor_KeepsOnlyThePitchesTheCqtCanHear()
    {
        var chord = new LintExpectedSound(1000, [Floor - 2, 40, 47], IsChord: true);

        var result = ChartAudioLintAnalyzer.Analyze(new float[Sr * 2], Sr, [chord]);

        Assert.Equal(1, result.SoundsChecked);
        Assert.Equal([40, 47], result.Findings.Select(f => f.ChartedMidi));
    }

    // The verifiable range: [Floor, Ceiling) in MIDI, two quarter-tone bins a semitone.
    private const int Floor = JuceSharp.Dsp.LiveCqtConfig.MinMidi;
    private const int Ceiling = JuceSharp.Dsp.LiveCqtConfig.MinMidi + JuceSharp.Dsp.LiveCqtConfig.BinCount / 2;

    [Fact]
    public void Analyze_IsDeterministic()
    {
        var pcm = RenderTone(57);
        var sounds = new[] { Note(750, 57), Note(750, 62) };

        var a = ChartAudioLintAnalyzer.Analyze(pcm, Sr, sounds);
        var b = ChartAudioLintAnalyzer.Analyze(pcm, Sr, sounds);

        Assert.Equal(a.SoundsVerified, b.SoundsVerified);
        Assert.Equal(a.Findings, b.Findings);
    }
}
