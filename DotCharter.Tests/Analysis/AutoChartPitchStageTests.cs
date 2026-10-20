using DotCharter.Analysis.AudioToChart;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     The two draft changes #391 measured on the corpus: the detector's legato
///     events are reported <see cref="JuceGuitarEventDetector.LegatoLeadSeconds" />
///     later, and the constant-Q verifier can stand in as the pitch stage
///     (<see cref="CqtPitchEvidence" />, <see cref="AudioToChartPitchStage.ConstantQ" />).
///     Synthetic fixtures: this pins the mechanism, the corpus number is in
///     <c>docs/agents/testing/autochart-eval.md</c>.
/// </summary>
public sealed class AutoChartPitchStageTests
{
    private const int Rate = AnalysisTestSignals.SampleRate;

    /// <summary>A plucked A3 that glides — no new attack — to C4 at 0.75 s, held, so the
    /// analyzer reports one picked onset and one legato event.</summary>
    private static float[] PluckThenLegato()
    {
        var buffer = new float[(int)(2.0 * Rate)];
        var start = (int)(0.30 * Rate);
        var change = (int)(0.75 * Rate);
        var end = (int)(1.40 * Rate);
        var phase = 0.0;
        for (var n = start; n < end; n++)
        {
            var t = (double)(n - start) / Rate;
            var envelope = Math.Exp(-t / 2.0) * Math.Min(1.0, t / 0.002);
            var frequency = n < change ? 220.0 : 261.63;
            phase += 2.0 * Math.PI * frequency / Rate;
            buffer[n] = (float)(0.3 * envelope * (Math.Sin(phase) + 0.4 * Math.Sin(2 * phase) + 0.2 * Math.Sin(3 * phase)));
        }

        return buffer;
    }

    private static List<JuceGuitarEvent> RawAnalyzerEvents(float[] buffer)
    {
        var analyzer = new JuceGuitarAnalyzer(Rate);
        var events = new List<JuceGuitarEvent>(analyzer.Push(buffer));
        events.AddRange(analyzer.Push(new float[Rate]));
        return events.Where(e => e.Pitches.Length > 0).ToList();
    }

    [Fact]
    public void A_legato_event_is_reported_later_by_the_measured_lead_and_a_picked_onset_is_not()
    {
        var fixture = PluckThenLegato();
        var raw = RawAnalyzerEvents(fixture);
        var detector = new JuceGuitarEventDetector(Rate);
        var reported = new List<GuitarAudioEvent>(detector.Push(fixture));
        reported.AddRange(detector.Push(new float[Rate]));

        Assert.Contains(raw, e => e.Kind == JuceGuitarEventKind.Legato);
        Assert.Contains(raw, e => e.Kind == JuceGuitarEventKind.Onset);
        Assert.Equal(raw.Count, reported.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var legato = raw[i].Kind == JuceGuitarEventKind.Legato;
            Assert.Equal(legato, reported[i].IsLegato);
            Assert.Equal(raw[i].StreamSeconds + (legato ? JuceGuitarEventDetector.LegatoLeadSeconds : 0.0),
                reported[i].TimeSeconds, 12);
        }
    }

    [Fact]
    public void The_correction_is_the_measured_forty_milliseconds_and_only_for_legato()
    {
        Assert.Equal(0.040, JuceGuitarEventDetector.LegatoLeadSeconds);
        Assert.Equal(1.040, JuceGuitarEventDetector.ReportedSeconds(legato: true, 1.0), 12);
        Assert.Equal(1.0, JuceGuitarEventDetector.ReportedSeconds(legato: false, 1.0));
    }

    /// <summary>Two plucks: the stage names each one's pitch at its onset, and nothing
    /// at a moment of silence.</summary>
    [Fact]
    public void The_cqt_stage_names_the_plucked_pitch()
    {
        var buffer = new float[3 * Rate];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 110.00); // A2, 45
        AnalysisTestSignals.AddPluck(buffer, 1.50, 196.00); // G3, 55
        var evidence = CqtPitchEvidence.FromWholeClip(buffer, Rate);

        Assert.Equal(45, evidence.PitchesAt(0.50, 40, 88, 1).Single().Midi);
        Assert.Equal(55, evidence.PitchesAt(1.50, 40, 88, 1).Single().Midi);
        Assert.Equal(1f, evidence.PitchesAt(1.50, 40, 88, 3)[0].Salience);
    }

    /// <summary>The pluck's 2nd and 3rd harmonics are real peaks; they are partials of
    /// the confirmed fundamental and must not become notes of their own.</summary>
    [Fact]
    public void The_partials_of_a_confirmed_pitch_are_not_drafted_as_notes()
    {
        var buffer = new float[2 * Rate];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 110.00); // A2 = 45; partials 57 (A3), 64 (E4)
        var pitches = CqtPitchEvidence.FromWholeClip(buffer, Rate).PitchesAt(0.50, 40, 88, 6).Select(p => p.Midi).ToList();

        Assert.Contains(45, pitches);
        Assert.DoesNotContain(57, pitches);
        Assert.DoesNotContain(64, pitches);
    }

    /// <summary>The range is the arrangement's: a pitch it cannot play is never offered.</summary>
    [Fact]
    public void The_cqt_stage_only_offers_pitches_the_arrangement_can_play()
    {
        var buffer = new float[2 * Rate];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 110.00); // A2 = 45
        var evidence = CqtPitchEvidence.FromWholeClip(buffer, Rate);

        Assert.DoesNotContain(evidence.PitchesAt(0.50, 46, 88, 6), p => p.Midi == 45);
        Assert.Empty(evidence.PitchesAt(0.50, 40, 88, 0));
    }

    [Fact]
    public void An_excerpt_is_asked_on_the_songs_clock()
    {
        var buffer = new float[2 * Rate];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 110.00);
        var evidence = CqtPitchEvidence.FromWholeClip(buffer, Rate, clipStartSeconds: 100.0);

        Assert.Equal(45, evidence.PitchesAt(100.50, 40, 88, 1).Single().Midi);
    }

    /// <summary>The whole-clip stage cannot run on the streaming overload, and the drafting
    /// half refuses to run it without the clip's CQT — neither falls back silently.</summary>
    [Fact]
    public void The_cqt_stage_is_refused_where_there_is_no_whole_clip()
    {
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var options = new AudioToChartOptions { PitchStage = AudioToChartPitchStage.ConstantQ };
        IEnumerable<ReadOnlyMemory<float>> chunks = [new float[Rate].AsMemory()];

        Assert.Throws<ArgumentException>(() => new AudioToChartAnalyzer().Analyze(chunks, Rate, beats, options));
        Assert.Throws<ArgumentException>(() => AudioToChartAnalyzer.DraftFromEvents([], beats, options));
    }

    /// <summary>End to end on the buffer overload: the CQT stage drafts the plucks right, and
    /// detect-then-draft with the clip's evidence is the same draft (the harness relies on it).</summary>
    [Fact]
    public void The_cqt_stage_drafts_the_plucks_and_detect_then_draft_matches_analyze()
    {
        var buffer = new float[3 * Rate];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 110.00); // A2
        AnalysisTestSignals.AddPluck(buffer, 1.00, 146.83); // D3
        AnalysisTestSignals.AddPluck(buffer, 1.75, 196.00); // G3
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);
        var options = new AudioToChartOptions { MaxNotesPerOnset = 1, PitchStage = AudioToChartPitchStage.ConstantQ };
        var analyzer = new AudioToChartAnalyzer();

        var whole = analyzer.Analyze(buffer, Rate, beats, options);
        var split = AudioToChartAnalyzer.DraftFromEvents(analyzer.DetectEvents(buffer, Rate), beats, options,
            CqtPitchEvidence.FromWholeClip(buffer, Rate));

        Assert.Equal([45, 50, 55], whole.Notes.Select(n => n.Midi));
        Assert.Equal(whole.Notes.Select(n => (n.TimeSeconds, n.Midi, n.Confidence, n.Note.StringIndex, n.Note.Fret)),
            split.Notes.Select(n => (n.TimeSeconds, n.Midi, n.Confidence, n.Note.StringIndex, n.Note.Fret)));
    }
}
