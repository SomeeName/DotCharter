using System.Reflection;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     Tools ▸ Tone &amp; sound ▸ Generate tones with AI, as the user sees it (#216): the
///     run is a status-rail task with progress and a cancel, the generator never runs
///     on the calling (UI) thread, and every outcome is the rail's main line — the
///     success line was an Info log entry the rail never shows.
/// </summary>
public sealed class AiToneGenerationRunTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _audio = Path.Combine(Path.GetTempPath(), $"dc-ai-tone-{Guid.NewGuid():N}.wav");
    private readonly ProjectState _state = new();
    private readonly RecordingStatusBarService _rail = new();
    private readonly FakeAppLogger _logger = new();
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly ChartProject _project = new();
    private readonly Arrangement _arrangement = new() { Name = "Lead" };
    private readonly UndoStepProxy _maintenance;
    private readonly IChartMaintenanceService _maintenanceProxy;

    public AiToneGenerationRunTests()
    {
        File.WriteAllBytes(_audio, [0]);
        var beats = GenerateFakes.Beats();
        foreach (var beat in beats) _project.Beats.Add(beat);
        _arrangement.ToneChanges.Add(GenerateFakes.At(4000, beats, "Verse"));
        _arrangement.ToneChanges.Add(GenerateFakes.At(8000, beats, "Chorus"));
        _project.Arrangements.Add(_arrangement);
        _state.ActiveProject = _project;
        _state.ActiveArrangement = _arrangement;
        _state.CurrentAudioFileName = _audio;
        _state.TotalAudioLengthMs = 16_000;
        _maintenanceProxy = DispatchProxy.Create<IChartMaintenanceService, UndoStepProxy>();
        _maintenance = (UndoStepProxy)(object)_maintenanceProxy;
    }

    public void Dispose() => File.Delete(_audio);

    [Fact]
    public async Task A_run_is_a_rail_task_with_progress_and_its_result_is_the_rails_line()
    {
        var generator = new FakeGenerator((request, status, fraction, _) =>
        {
            fraction?.Report(0.0);
            status?.Report("Tone 1 of 2: asking the model…");
            fraction?.Report(0.5);
            return Generated(request, "verse", "chorus");
        });

        await Run(generator).RunAsync();

        Assert.Equal(
            [$"begin:{AiToneGenerationRun.TaskId}:{_loc.Format("ToneGenerate_Running", "Lead")}",
             $"complete:{AiToneGenerationRun.TaskId}"],
            _rail.Log);
        Assert.False(_rail.LastTask!.IsIndeterminate);
        Assert.NotNull(_rail.LastTask.CancelAction);
        Assert.Contains(_rail.Progress, p => p.Label == "Tone 1 of 2: asking the model…" && p.Progress == 0.0);
        Assert.Contains(_rail.Progress, p => p.Progress == 0.5);

        Assert.Equal(
            _loc.PluralWith("ToneGenerate_DoneOne", "ToneGenerate_Done", 2, 2, 2, "verse"),
            _state.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Success, _state.MainDisplaySeverity);
        Assert.Equal(["verse", "chorus"], _project.ToneDefinitions.Select(d => d.Key).ToArray());
        Assert.Equal(1, _maintenance.UndoSteps);
    }

    [Fact]
    public async Task The_generator_never_runs_on_the_calling_thread()
    {
        // The generator body is SYNCHRONOUS and blocks: called inline, RunAsync would
        // not return until it gave up waiting, and the run would already be complete.
        using var release = new ManualResetEventSlim();
        var generator = new FakeGenerator((request, _, _, _) =>
        {
            release.Wait(Patience);
            return Generated(request, "verse", "chorus");
        });

        var run = Run(generator).RunAsync();
        Assert.False(run.IsCompleted, "RunAsync blocked its caller: the generator ran on the calling thread.");

        release.Set();
        await run;
        Assert.Equal(StatusSeverity.Success, _state.MainDisplaySeverity);
    }

    [Fact]
    public async Task The_pills_cancel_stops_the_run_writes_nothing_and_says_so()
    {
        // The generator runs on the thread pool, and under a loaded gate the pool can
        // take longer than any fixed budget to start it. So nothing here is bounded by a
        // wall-clock number: the test awaits the real "generator started" signal and the
        // generator awaits the real cancel; a genuine hang ends with the test run's token.
        var test = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator((_, _, _, ct) =>
        {
            started.SetResult();
            WaitHandle.WaitAny([ct.WaitHandle, test.WaitHandle]);
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("the cancel never arrived");
        });

        var run = Run(generator).RunAsync();
        await started.Task.WaitAsync(test);
        _rail.LastTask!.CancelAction!();
        await run;

        Assert.Equal(_loc["ToneGenerate_Cancelled"], _state.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, _state.MainDisplaySeverity);
        Assert.Equal($"complete:{AiToneGenerationRun.TaskId}", _rail.Log[^1]);
        Assert.Empty(_project.ToneDefinitions);
        Assert.Equal(0, _maintenance.UndoSteps);
    }

    [Fact]
    public async Task A_project_switched_mid_run_is_not_written_into()
    {
        using var release = new ManualResetEventSlim();
        var generator = new FakeGenerator((request, _, _, _) =>
        {
            release.Wait(Patience);
            return Generated(request, "verse", "chorus");
        });

        var run = Run(generator).RunAsync();
        _state.ActiveProject = new ChartProject();
        release.Set();
        await run;

        Assert.Equal(_loc["ToneGenerate_ProjectChanged"], _state.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, _state.MainDisplaySeverity);
        Assert.Empty(_project.ToneDefinitions);
        Assert.Equal(["Verse", "Chorus"], _arrangement.ToneChanges.Select(c => c.ToneName).ToArray());
    }

    [Fact]
    public async Task No_generated_tone_puts_the_first_reason_on_the_rail_as_an_error()
    {
        var generator = new FakeGenerator((request, _, _, _) => new ToneSetGenerationResult(
            request.Windows.Select(w => new GeneratedTone(w, null, null, "no AI provider is set up")).ToList(),
            TimeSpan.Zero, []));

        await Run(generator).RunAsync();

        Assert.Equal(_loc.Format("ToneGenerate_NoneGeneratedWhy", "no AI provider is set up"), _state.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Error, _state.MainDisplaySeverity);
        Assert.Equal(0, _maintenance.UndoSteps);
    }

    [Fact]
    public async Task Nothing_to_generate_from_is_said_on_the_rail_and_asks_nothing()
    {
        _arrangement.ToneChanges.Clear();
        var generator = new FakeGenerator((request, _, _, _) => Generated(request));

        await Run(generator).RunAsync();

        Assert.Equal(_loc["ToneGenerate_NoToneChanges"], _state.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, _state.MainDisplaySeverity);
        Assert.Equal(0, generator.Calls);
        Assert.Empty(_rail.Log);
    }

    [Fact]
    public async Task The_charters_notes_reach_every_request_beside_the_metadata_and_none_changes_nothing()
    {
        var contexts = new List<string?>();
        var generator = new FakeGenerator((request, _, _, _) =>
        {
            contexts.Add(request.Context);
            return Generated(request, "verse", "chorus");
        });
        _project.Chart.Artist = "Sakasma";

        await Run(generator).RunAsync();
        await Run(generator).RunAsync("  The chorus is a   cranked Marshall.\n");

        var metadata = AiUntrustedText.Block("band: Sakasma, song: Untitled Song, arrangement: Lead");
        Assert.Equal(metadata, contexts[0]);
        Assert.Equal(metadata + "\nthe charter's notes: The chorus is a cranked Marshall.", contexts[1]);
    }


    [Fact]
    public async Task The_generated_tones_are_shown_before_anything_is_written_and_declining_writes_nothing()
    {
        var shown = new List<(string Title, string Body, int DefinitionsAtThatPoint)>();
        var generator = new FakeGenerator((request, _, _, _) => Generated(request, "verse", "chorus"));

        await Run(generator, (title, body) =>
        {
            shown.Add((title, body, _project.ToneDefinitions.Count));
            return Task.FromResult(false);
        }).RunAsync();

        var (shownTitle, shownBody, written) = Assert.Single(shown);
        Assert.Equal(_loc["ToneGenerate_ConfirmTitle"], shownTitle);
        Assert.Equal(0, written);
        Assert.StartsWith(_loc.PluralWith("ToneGenerate_ConfirmOne", "ToneGenerate_Confirm", 2, 2, "Lead"), shownBody, StringComparison.Ordinal);
        Assert.Contains("verse", shownBody, StringComparison.Ordinal);
        Assert.Contains("chorus", shownBody, StringComparison.Ordinal);
        Assert.Contains(AiToneText.Summary(ToneOf("verse")), shownBody, StringComparison.Ordinal);

        Assert.Empty(_project.ToneDefinitions);
        Assert.Equal(["Verse", "Chorus"], _arrangement.ToneChanges.Select(c => c.ToneName).ToArray());
        Assert.Equal(0, _maintenance.UndoSteps);
        Assert.Equal(_loc["ToneGenerate_Declined"], _state.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, _state.MainDisplaySeverity);
    }

    [Fact]
    public async Task Confirming_writes_the_set_as_one_undo_step()
    {
        var generator = new FakeGenerator((request, _, _, _) => Generated(request, "verse", "chorus"));
        var asked = 0;

        await Run(generator, (_, _) =>
        {
            asked++;
            return Task.FromResult(true);
        }).RunAsync();

        Assert.Equal(1, asked);
        Assert.Equal(["verse", "chorus"], _project.ToneDefinitions.Select(d => d.Key).ToArray());
        Assert.Equal(1, _maintenance.UndoSteps);
    }

    [Fact]
    public async Task Nothing_generated_asks_nothing()
    {
        var generator = new FakeGenerator((request, _, _, _) => new ToneSetGenerationResult(
            request.Windows.Select(w => new GeneratedTone(w, null, null, "no AI provider is set up")).ToList(),
            TimeSpan.Zero, []));
        var asked = 0;

        await Run(generator, (_, _) =>
        {
            asked++;
            return Task.FromResult(true);
        }).RunAsync();

        Assert.Equal(0, asked);
    }

    private AiToneGenerationRun Run(IRsToneGenerator generator, Func<string, string, Task<bool>>? confirm = null) =>
        new(_state, _maintenanceProxy, _rail, _loc, _logger, () => generator, confirm ?? ((_, _) => Task.FromResult(true)));

    private static RsTone ToneOf(string key)
    {
        var (amp, cab) = GenerateFakes.RealGuitarGear();
        return new RsTone(
            key, key, RsToneAuthoring.DefaultVolumeDb, [RsToneAuthoring.CleanDescriptorUIName],
            new RsAmp(amp, RsGearCatalog.Shared.GetCategory(amp), new Dictionary<string, float>(StringComparer.Ordinal)),
            new RsCab(cab, RsGearCatalog.Shared.GetCategory(cab), new Dictionary<string, float>(StringComparer.Ordinal)),
            [], [], []);
    }

    private static ToneSetGenerationResult Generated(ToneGenerateRequest request, params string[] keys)
    {
        var tones = request.Windows.Select((window, i) => new GeneratedTone(window, ToneOf(keys[i]),
            new ToneGenerationProvenance(
                window, request.Instrument, [ToneGenerationSources.Audio], "fake-model", "", null, "no-renderer", null),
            null)).ToList();
        return new ToneSetGenerationResult(tones, TimeSpan.Zero, []);
    }

    /// <summary>A generator whose body runs synchronously on whatever thread calls it.</summary>
    private sealed class FakeGenerator(
        Func<ToneGenerateRequest, IProgress<string>?, IProgress<double>?, CancellationToken, ToneSetGenerationResult> body)
        : IRsToneGenerator
    {
        public int Calls { get; private set; }

        public Task<ToneSetGenerationResult> GenerateAsync(
            ToneGenerateRequest request, IProgress<string>? status, CancellationToken ct,
            IProgress<double>? fraction = null)
        {
            Calls++;
            return Task.FromResult(body(request, status, fraction, ct));
        }
    }

    /// <summary><see cref="IChartMaintenanceService" /> whose one-undo-step seam runs
    /// the mutation and counts it; every other member records and answers the default.</summary>
    public class UndoStepProxy : RecordingProxy
    {
        public int UndoSteps { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IChartMaintenanceService.RunAsSingleUndoStep)) return base.Invoke(method, args);
            ((Action)args![0]!).Invoke();
            UndoSteps++;
            return true;
        }
    }
}
