using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Tones;

/// <summary>
///     #215: "Generate tones from audio" decoded the whole song once PER placed tone change,
///     inline on the UI thread. The run is now Capture (UI) → Analyse (any thread, one decode
///     per path) → Apply (UI), and the Tone designer awaits the middle step in <c>Task.Run</c>
///     (pinned by <c>UI/ToneGenerationReachabilityTests</c>).
/// </summary>
public sealed class ToneGenerationRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-tonegen-" + Guid.NewGuid().ToString("N"));

    public ToneGenerationRunTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void A_run_over_several_changes_decodes_the_audio_once()
    {
        var (project, arrangement, path) = ProjectWithChanges("clean", "crunch", "lead");
        var loads = 0;
        var service = new ToneGenerationService(new FakeAppLogger(), load: (_, _) =>
        {
            loads++;
            return Guitarish(30);
        });
        var run = new ToneGenerationRun(new HistoryService(new FakeAppLogger()), new ExportTargetWriter(), new FakeAppLogger());

        var plan = run.Analyse(ToneGenerationRun.Capture(project, arrangement, path), service);

        Assert.Equal(3, plan.Candidates.Count);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void Capture_touches_no_file_and_Analyse_does_not_write_the_project()
    {
        var (project, arrangement, path) = ProjectWithChanges("clean", "lead");
        var input = ToneGenerationRun.Capture(project, arrangement, path);
        var service = new ToneGenerationService(new FakeAppLogger(), load: (_, _) => Guitarish(30));
        var run = new ToneGenerationRun(new HistoryService(new FakeAppLogger()), new ExportTargetWriter(), new FakeAppLogger());

        run.Analyse(input, service);

        Assert.Empty(project.ToneDefinitions);
        Assert.Equal("", arrangement.ToneA);
        Assert.Equal(Path.Combine(_dir, "song.wav"), input.SongCandidate);
    }

    [Fact]
    public void Apply_writes_each_generated_tone_as_an_undoable_edit_and_declares_it()
    {
        var (project, arrangement, _) = ProjectWithChanges("clean");
        var history = new HistoryService(new FakeAppLogger());
        var run = new ToneGenerationRun(history, new ExportTargetWriter(), new FakeAppLogger());

        var outcome = run.Apply(project, arrangement, Plan(("clean", "clean")));

        Assert.Single(outcome.Generated);
        Assert.Contains(project.ToneDefinitions, t => t.Key == "clean");
        Assert.Equal("clean", arrangement.ToneA);
        while (history.CanUndo) history.Undo();
        Assert.Empty(project.ToneDefinitions);
        Assert.True(string.IsNullOrEmpty(arrangement.ToneA));
    }

    [Fact]
    public void Apply_skips_a_name_the_project_gained_while_the_analysis_ran()
    {
        var (project, arrangement, _) = ProjectWithChanges("clean");
        var history = new HistoryService(new FakeAppLogger());
        var run = new ToneGenerationRun(history, new ExportTargetWriter(), new FakeAppLogger());
        var plan = Plan(("clean", "clean"));
        project.ToneDefinitions.Add(new ToneDefinition { Key = "mine", Name = "Clean", Json = "{}" });

        var outcome = run.Apply(project, arrangement, plan);

        Assert.Empty(outcome.Generated);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal("{}", Assert.Single(project.ToneDefinitions).Json);
        Assert.False(history.CanUndo);
    }

    private static ToneGenerationPlan Plan(params (string Key, string Name)[] tones) =>
        new(tones.Select(t => new ToneGenerationCandidate(t.Key, t.Name,
            new ToneGenerationResult(ToneGenerationStatus.Generated, new ToneSpan(0, t.Name, 0, 8000), ToneJson: "{\"Key\":\"" + t.Key + "\"}")))
            .ToList(), 0, tones.Length);

    /// <summary>A 30 s chart at 120 bpm with a tone change every 8 s, and a song file on disk.</summary>
    private (ChartProject Project, Arrangement Arrangement, string ProjectPath) ProjectWithChanges(params string[] tones)
    {
        File.WriteAllBytes(Path.Combine(_dir, "song.wav"), [0]);
        var project = new ChartProject { AudioFileName = "song.wav" };
        for (var i = 0; i <= 60; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement();
        for (var i = 0; i < tones.Length; i++)
            arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(i * 16), ToneName = tones[i] });
        project.Arrangements.Add(arrangement);
        return (project, arrangement, Path.Combine(_dir, "project.rscp"));
    }

    /// <summary>A plucked, saturated-ish signal: loud, harmonic, not noise.</summary>
    private static PcmAudio Guitarish(int seconds)
    {
        const int rate = 22050;
        var samples = new short[rate * seconds];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)rate;
            var env = Math.Exp(-3 * (t % 0.5));
            var v = Math.Tanh(4 * (Math.Sin(2 * Math.PI * 110 * t) + 0.5 * Math.Sin(2 * Math.PI * 220 * t))) * env;
            samples[i] = (short)(v * 20000);
        }

        return new PcmAudio(samples, 1, rate);
    }
}
