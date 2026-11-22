using System.Reflection;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Parsing.Psarc;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>A section tone set owns its playable project bodies and timeline references in one undo step.</summary>
[Collection("GameplayToneHud")]
public sealed class ToneSetPlacementTests
{
    [Fact]
    public async Task FirstToneSet_RefreshesPreparedTonesBeforeTheRunStarts()
    {
        RsTonePlaybackCoordinator? coordinator = null;
        var catalog = new[] { "amp", "cab", "gain" }.Select(id => new DspModuleType(id, id, [])).ToList();
        var chain = new FakeDspChainService(catalog);
        using var h = new PlayModeReviewHarness(tones: (state, config) =>
        {
            config.AppConfig.UseInGameTones = true;
            return coordinator = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, config);
        });
        await (Task)PlayModeReviewHarness.Invoke(h.Vm, "RefreshInGameTonesAsync")!;
        h.Vm.CurrentScreen = PlayModeScreen.TuningCheck;
        Assert.False(coordinator!.LastSummary.HasTones);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(h.Vm.HasInGameTones) && h.Vm.HasInGameTones) prepared.TrySetResult();
        };
        var tone = new DspTonePreset("user", "First clean", DspToneCategory.Clean, "", new DcRigPreset([]), DspToneOrigin.User);
        var entry = new ToneSetEntry(new ToneSection("a", "Intro", ToneStems.Guitar, TimeSpan.Zero, TimeSpan.FromSeconds(1)), tone);
        var place = typeof(PlayModeToneRigViewModel).GetMethod("PlaceToneSet", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal(1, place.Invoke(h.Vm.ToneRig, [new[] { entry }]));
        Assert.True(h.Vm.InGameTonesLoading || h.Vm.HasInGameTones,
            "Applying the first tone set never refreshed the prepared no-tones summary");
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(coordinator.LastSummary.HasTones);
        h.Begin();

        Assert.True(coordinator.IsDriving);
        coordinator.EndRun();
        await coordinator.RestoreCompletion;
    }

    [Fact]
    public void PlacingASet_DefinesEveryReferencedTone_AndUndoesAndRedoesTogether()
    {
        var project = new ChartProject();
        project.Beats.Add(new BeatMarker { Time = 0, Bpm = 120 });
        project.Beats.Add(new BeatMarker { Time = 500, Bpm = 120 });
        var arrangement = new Arrangement();
        project.Arrangements.Add(arrangement);
        var existing = new ToneDefinition { Key = "Clean", Name = "Existing", Json = "{}" };
        project.ToneDefinitions.Add(existing);
        var state = new ProjectState { ActiveProject = project, ActiveArrangement = arrangement };
        var history = new HistoryService(new FakeAppLogger());
        var vm = new PlayModeToneRigViewModel(state, null, null, null, history,
            static () => null, static () => null, static () => null, static () => null,
            static () => null, static () => null, static () => null, static () => null,
            static () => null, static () => null, static () => null);
        var tone = new DspTonePreset("user", "Clean", DspToneCategory.Clean, "", new DcRigPreset([]), DspToneOrigin.User);
        var entries = new[]
        {
            new ToneSetEntry(new ToneSection("a", "Intro", ToneStems.Guitar, TimeSpan.Zero, TimeSpan.FromSeconds(1)), tone),
            new ToneSetEntry(new ToneSection("b", "Verse", ToneStems.Guitar, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), tone)
        };
        var place = typeof(PlayModeToneRigViewModel).GetMethod("PlaceToneSet", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(place);

        Assert.Equal(2, place.Invoke(vm, [entries]));
        Assert.Equal(3, project.ToneDefinitions.Count);
        Assert.Equal(2, arrangement.ToneChanges.Count);
        foreach (var change in arrangement.ToneChanges)
        {
            var definition = Assert.Single(project.ToneDefinitions, d => d.Key == change.ToneName);
            Assert.NotSame(existing, definition);
            var parsed = RsToneJsonParser.Parse(new PsarcToneDefinition(definition.Key, definition.Name, definition.Json));
            Assert.Equal(definition.Key, parsed.Key);
            Assert.NotNull(parsed.Amp);
        }

        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Same(existing, Assert.Single(project.ToneDefinitions));
        Assert.Empty(arrangement.ToneChanges);
        history.Redo();
        Assert.Equal(3, project.ToneDefinitions.Count);
        Assert.Equal(2, arrangement.ToneChanges.Count);
    }
}
