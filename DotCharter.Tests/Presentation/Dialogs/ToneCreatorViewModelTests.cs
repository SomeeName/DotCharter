using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Editing;
using DotCharter.Tests.Ai.Chart;
using DotCharter.Tests.Ai.Generate;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The Tone designer's AI tone creator over a FAKE model and the REAL creator, mapper and catalogue gate: the
///     scope, the gate's refusals by name, Refine carrying the proposal, Accept as ONE undo step (replace, new tone in
///     a free slot, tone change at the playhead), Stop and Cancel writing nothing, a full slot set refused with why.
/// </summary>
public sealed class ToneCreatorViewModelTests
{
    private const string Description = "Master of Puppets rhythm, tight, scooped, no reverb";

    [Fact]
    public async Task The_scope_is_Rocksmith_gear_only_and_the_rig_scope_is_refused_with_its_reason()
    {
        var harness = new Harness();
        var vm = harness.Open();

        vm.IsDotCharterRig = true;

        Assert.False(vm.IsDotCharterRig);
        Assert.True(vm.IsRocksmithGearOnly);
        Assert.False(vm.CanUseDotCharterRig);
        Assert.Contains("Rocksmith", vm.DotCharterRigReason, StringComparison.Ordinal);

        vm.Description = Description;
        await Run(vm.CreateCommand);

        var prompt = Assert.Single(harness.Model.Prompts);
        Assert.Equal(RsToneGeneratePrompt.SystemPrompt(bass: false), prompt.System);
        Assert.Contains(Description, Text(prompt), StringComparison.Ordinal);
        Assert.True(vm.HasProposal);
    }

    [Fact]
    public async Task A_bass_arrangement_asks_the_bass_catalogue_and_refuses_guitar_gear_by_name()
    {
        var (guitarAmp, _) = GenerateFakes.RealGuitarGear();
        var harness = new Harness(_ => GenerateFakes.ValidReply());
        harness.State.ActiveArrangement = harness.Project.Arrangements.Single(a => a.ArrangementType == ArrangementType.Bass);
        var vm = harness.Open();
        vm.Description = "growly pick bass";

        await Run(vm.CreateCommand);

        Assert.Equal(RsToneGeneratePrompt.SystemPrompt(bass: true), Assert.Single(harness.Model.Prompts).System);
        Assert.False(vm.HasProposal);
        Assert.Contains(guitarAmp, vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invented_gear_is_refused_by_name_and_nothing_can_be_accepted()
    {
        var harness = new Harness(_ => GenerateFakes.ValidReply(ampKey: "Amp_MesaBoogieMarkV"));
        var vm = harness.Open();
        vm.Description = Description;

        await Run(vm.CreateCommand);

        Assert.False(vm.HasProposal);
        Assert.Contains("Amp_MesaBoogieMarkV", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.AcceptCommand.CanExecute(null));
        Assert.Empty(harness.History.GetUndoSteps());
    }

    [Fact]
    public async Task Refine_sends_the_current_proposal_and_the_instruction()
    {
        var (_, cab) = GenerateFakes.RealGuitarGear();
        var amps = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Amp).Where(g => !g.Key.StartsWith("Bass_", StringComparison.Ordinal))
            .Select(g => g.Key).Take(2).ToList();
        var harness = new Harness(call => GenerateFakes.ValidReply(ampKey: amps[call], cabKey: cab));
        var vm = harness.Open();
        vm.Description = Description;
        await Run(vm.CreateCommand);

        Assert.False(vm.RefineCommand.CanExecute(null));
        vm.RefineText = "more gain, less reverb";
        await Run(vm.RefineCommand);

        var refine = Text(harness.Model.Prompts[1]);
        Assert.DoesNotContain(RsToneGeneratePrompt.PreviousHeader, Text(harness.Model.Prompts[0]), StringComparison.Ordinal);
        Assert.Contains(RsToneGeneratePrompt.PreviousHeader, refine, StringComparison.Ordinal);
        var previous = refine[refine.IndexOf(RsToneGeneratePrompt.PreviousHeader, StringComparison.Ordinal)..];
        Assert.Contains(amps[0], previous, StringComparison.Ordinal);
        Assert.Contains("more gain, less reverb", refine, StringComparison.Ordinal);
        Assert.Contains(Description, refine, StringComparison.Ordinal);
        var refined = Proposed(harness);
        Assert.Equal(amps[1], refined.Amp.GearKey);
        Assert.Contains(vm.ProposalRows, r => r.Gear == DotCharter.Services.Ai.Chart.AiToneText.Gear(refined.Amp));
        Assert.Equal("", vm.RefineText);
    }

    [Fact]
    public async Task Accept_replace_writes_the_edited_tone_as_one_undo_step_and_undo_puts_it_back()
    {
        var harness = new Harness();
        var before = harness.Project.ToneDefinitions.Single().Json;
        var vm = harness.Open("clean");
        Assert.True(vm.IsReplaceEdited);
        vm.Description = Description;
        await Run(vm.CreateCommand);

        vm.AcceptCommand.Execute(null);

        Assert.Equal("clean", await vm.Result);
        var written = Assert.Single(harness.Project.ToneDefinitions);
        Assert.Equal("clean", written.Key);
        Assert.Equal("Clean", written.Name);
        Assert.NotEqual(before, written.Json);
        Assert.Single(harness.History.GetUndoSteps());
        harness.History.Undo();
        Assert.Equal(before, Assert.Single(harness.Project.ToneDefinitions).Json);
    }

    [Fact]
    public async Task Accept_as_a_new_tone_declares_it_in_a_free_slot_as_one_undo_step()
    {
        var harness = new Harness();
        var lead = harness.Lead;
        var vm = harness.Open("clean");
        vm.IsSaveAsNew = true;
        vm.Description = Description;
        await Run(vm.CreateCommand);
        vm.NewToneName = "Rhythm";
        Assert.Contains("Tone B", vm.NewToneSlotText, StringComparison.Ordinal);

        vm.AcceptCommand.Execute(null);

        var key = await vm.Result;
        Assert.Equal("Rhythm", key);
        Assert.Equal(2, harness.Project.ToneDefinitions.Count);
        Assert.Equal("Rhythm", lead.ToneB);
        Assert.Contains("Rhythm", lead.ToneNames);
        Assert.Single(harness.History.GetUndoSteps());
        harness.History.Undo();
        Assert.Single(harness.Project.ToneDefinitions);
        Assert.Equal("", lead.ToneB);
        Assert.DoesNotContain("Rhythm", lead.ToneNames);
    }

    [Fact]
    public async Task Accept_with_a_tone_change_at_the_playhead_is_still_one_undo_step()
    {
        var harness = new Harness();
        harness.State.CurrentTimeMs = 1000;
        var vm = harness.Open("clean");
        vm.IsSaveAsNew = true;
        vm.Description = Description;
        await Run(vm.CreateCommand);
        vm.PlaceAtPlayhead = true;

        vm.AcceptCommand.Execute(null);

        var key = await vm.Result;
        var change = Assert.Single(harness.Lead.ToneChanges);
        Assert.Equal(key, change.ToneName);
        Assert.Equal(1000, change.GetTimeMs(harness.Project.Beats), 3);
        Assert.Single(harness.History.GetUndoSteps());
        harness.History.Undo();
        Assert.Empty(harness.Lead.ToneChanges);
        Assert.Single(harness.Project.ToneDefinitions);
    }

    [Fact]
    public async Task Cancel_writes_nothing_and_ends_the_audition()
    {
        var harness = new Harness();
        var before = harness.Project.ToneDefinitions.Single().Json;
        var vm = harness.Open("clean");
        vm.Description = Description;
        await Run(vm.CreateCommand);
        vm.IsAuditioning = true;
        Assert.Equal(Proposed(harness).Amp.GearKey, Assert.Single(harness.Preview.Applied).Amp.GearKey);

        vm.CancelCommand.Execute(null);

        Assert.Null(await vm.Result);
        Assert.Equal(1, harness.Preview.Stops);
        Assert.False(harness.Preview.IsActive);
        Assert.Empty(harness.History.GetUndoSteps());
        Assert.Equal(before, Assert.Single(harness.Project.ToneDefinitions).Json);
        Assert.Equal("", harness.Lead.ToneB);
    }

    [Fact]
    public async Task A_full_slot_set_refuses_a_new_tone_with_its_reason()
    {
        var harness = new Harness();
        var lead = harness.Lead;
        (lead.ToneB, lead.ToneC, lead.ToneD) = ("clean", "clean", "clean");
        var vm = harness.Open("clean");
        vm.IsSaveAsNew = true;
        vm.Description = Description;
        await Run(vm.CreateCommand);

        Assert.Contains("Lead", vm.AcceptUnavailableReason, StringComparison.Ordinal);
        Assert.True(vm.HasAcceptUnavailableReason);
        Assert.False(vm.AcceptCommand.CanExecute(null));
        vm.AcceptCommand.Execute(null);
        Assert.False(vm.Result.IsCompleted);
        Assert.Empty(harness.History.GetUndoSteps());

        vm.IsReplaceEdited = true;
        Assert.True(vm.AcceptCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_provider_call_runs_off_the_calling_thread_and_Stop_cancels_it_writing_nothing()
    {
        using var entered = new ManualResetEventSlim();
        var harness = new Harness(blocking: entered);
        var vm = harness.Open("clean");
        vm.Description = Description;

        var run = ((IAsyncRelayCommand)vm.CreateCommand).ExecuteAsync(null);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the model was never asked");
        Assert.False(run.IsCompleted);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CreateCommand.CanExecute(null));

        vm.StopCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(vm.IsBusy);
        Assert.False(vm.HasProposal);
        Assert.Equal(FakeLocalization.English()["Dialog_ToneCreator_Stopped"], vm.Status);
        Assert.Empty(harness.History.GetUndoSteps());
    }

    [Fact]
    public void Opened_on_a_placed_tone_it_measures_where_that_tone_plays_not_at_the_playhead()
    {
        var audio = Path.Combine(Path.GetTempPath(), $"dc-creator-{Guid.NewGuid():N}.ogg");
        File.WriteAllBytes(audio, [0]);
        try
        {
            var harness = new Harness();
            harness.State.CurrentAudioFileName = audio;
            harness.State.TotalAudioLengthMs = 8000;
            harness.State.CurrentTimeMs = 6000;
            var beats = harness.Project.Beats.ToList();
            harness.Lead.ToneChanges.Add(GenerateFakes.At(1000, beats, "clean"));
            harness.Lead.ToneChanges.Add(GenerateFakes.At(5000, beats, "other"));

            var atPlayhead = harness.Open();
            var onClean = harness.Open("clean");

            Assert.DoesNotContain("where", atPlayhead.SongEvidenceText, StringComparison.Ordinal);
            Assert.Contains("where clean plays (0:00", onClean.SongEvidenceText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(audio);
        }
    }

    private static Task Run(ICommand command) => ((IAsyncRelayCommand)command).ExecuteAsync(null);

    private static string Text(ToneDesignPrompt prompt) => prompt.Messages[0].Text.ReplaceLineEndings("\n");

    private static RsTone Proposed(Harness harness) =>
        RsToneReplyReader.Read(harness.LastReply!, "ai_tone", "x", RsGearCatalog.Shared, out _);

    /// <summary>A Lead (Base and A = "clean", B–D free) and a Bass arrangement over a 16-beat map, one tone defined.</summary>
    internal sealed class Harness
    {
        private readonly Func<int, string> _reply;

        public Harness(Func<int, string>? reply = null, ManualResetEventSlim? blocking = null)
        {
            _reply = reply ?? (_ => GenerateFakes.ValidReply());
            Project = new ChartProject { Chart = { SongName = "Battery", Artist = "Band" } };
            for (var i = 0; i < 16; i++) Project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4 });
            Project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = "clean", ToneA = "clean" });
            Project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass" });
            Project.ToneDefinitions.Add(new ToneDefinition
            {
                Key = "clean", Name = "Clean",
                Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone("clean", "Clean", RsGearCatalog.Shared))
            });
            State.ActiveProject = Project;
            State.ActiveArrangement = Lead;
            Model = blocking is null
                ? new FakeChartModel((_, call) => LastReply = _reply(call))
                : new FakeChartModel((_, _) => throw new InvalidOperationException("unused"));
            if (blocking is not null) BlockingModel = new BlockingModel(blocking);
        }

        public ChartProject Project { get; }
        public Arrangement Lead => Project.Arrangements[0];
        public ProjectState State { get; } = new();
        public HistoryService History { get; } = new(new FakeAppLogger());
        public FakeChartModel Model { get; }
        public BlockingModel? BlockingModel { get; }
        public RecordingPreview Preview { get; } = new();
        public string? LastReply { get; private set; }

        public ToneCreatorViewModel Open(string? editedKey = null, IRsToneFeedbackLoop? loop = null)
        {
            var services = new ChartEditDialogServices(RecordingProxy.Create<IChartEditorSurface>().Proxy, History, State,
                new FakeConfigurationService(), FakeLocalization.English(), new FakeAppLogger());
            IToneDesignModel model = BlockingModel is { } blocking ? blocking : Model;
            var creator = new ToneCreator(new LanguageModelRsToneMapper(() => model, "fake"), new GenerateFakes.FakeIsolator(),
                new MeasuredToneEvidenceSource());
            var vm = new ToneCreatorViewModel(services, creator, loop ?? RecordingProxy.Create<IRsToneFeedbackLoop>().Proxy);
            var edited = editedKey is null ? null : Project.ToneDefinitions.Single(d => d.Key == editedKey);
            vm.Configure(new ToneCreatorRequest(edited?.Key, edited?.Name, Preview));
            return vm;
        }
    }

    /// <summary>A model that blocks SYNCHRONOUSLY until cancelled: run inline, it would hold the caller.</summary>
    internal sealed class BlockingModel(ManualResetEventSlim entered) : IToneDesignModel
    {
        public AiProviderKind Kind => AiProviderKind.ClaudeCli;

        public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
        {
            entered.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("never cancelled");
        }

        public Task<AiProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new AiProbeResult(true, "ok", "fake"));
    }

    /// <summary>The designer's preview target, recorded.</summary>
    internal sealed class RecordingPreview : ITonePreviewTarget
    {
        public List<RsTone> Applied { get; } = [];
        public int Stops { get; private set; }
        public bool IsAvailable => true;
        public bool IsActive { get; private set; }

        public void Apply(RsTone tone)
        {
            Applied.Add(tone);
            IsActive = true;
        }

        public bool Stop()
        {
            if (!IsActive) return false;
            Stops++;
            IsActive = false;
            return true;
        }
    }
}
