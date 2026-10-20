using System.Collections.ObjectModel;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

public sealed class RigTempoTests
{
    [Fact]
    public void SyncDisablesTheManualTimeControl_AndRestoresItsStoredValueWhenTurnedOff()
    {
        var type = new DspModuleType("delay", "Delay", [new("timeMs", "Time", 10, 2000, 300), new("sync", "Sync", 0, 1, 0)]);
        var chain = new FakeDspChainService([type]);
        var node = new PedalNodeViewModel(chain, chain.AddNode("delay")!, type, FakeLocalization.English());
        var time = Param(node, "timeMs");
        Assert.True(time.IsControlActive);
        Param(node, "sync").Value = 1;
        Assert.False(time.IsControlActive);
        Assert.False(string.IsNullOrWhiteSpace(time.InactiveHint));
        Assert.Equal(300, time.Value);
        Param(node, "sync").Value = 0;
        Assert.True(time.IsControlActive);
        Assert.Null(time.InactiveHint);
        Assert.Equal(300, time.Value);
    }

    [Fact]
    public void NoTargets_HasNoInventedTempo_AndCannotTapOrWrite()
    {
        var (chain, nodes) = Create();
        using var tempo = new RigTempoViewModel(nodes);
        Assert.Null(tempo.BPM);
        Assert.False(tempo.HasTargets);
        Assert.False(tempo.HasMixedValues);
        Assert.False(tempo.TapCommand.CanExecute(null));
        tempo.BPM = 140;
        tempo.TapCommand.Execute(null);
        Assert.Empty(chain.ParamSets);
    }

    [Fact]
    public void WritesOnlyActualDelayBpmTargets_WithoutChangingSyncOrOtherParameters()
    {
        var (chain, nodes) = Create();
        var first = Add(chain, nodes, "delay");
        var second = Add(chain, nodes, "delay");
        Add(chain, nodes, "reverb"); // matching parameter names do not make this a delay
        Param(second, "sync").Value = 1;
        Param(second, "bpm").Value = 100;
        chain.ParamSets.Clear();
        using var tempo = new RigTempoViewModel(nodes);
        Assert.Equal(120d, tempo.BPM);
        Assert.True(tempo.HasMixedValues);
        tempo.BPM = 137;
        Assert.Equal(new[] { (first.NodeId, "bpm", 137f), (second.NodeId, "bpm", 137f) }, chain.ParamSets);
        Assert.False(tempo.HasMixedValues);
        Assert.Equal(0, Param(first, "sync").Value);
        Assert.Equal(1, Param(second, "sync").Value);
    }

    [Fact]
    public void LiveEditsAndNodeReplacement_RefreshProjectionAndDetachOldTargets()
    {
        var (chain, nodes) = Create();
        var old = Add(chain, nodes, "delay");
        using var tempo = new RigTempoViewModel(nodes);
        var changes = new List<string?>();
        tempo.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Param(old, "bpm").Value = 95;
        Assert.Equal(95d, tempo.BPM);
        Assert.Contains(nameof(RigTempoViewModel.BPM), changes);
        nodes.Clear();
        Assert.Null(tempo.BPM);
        var current = Add(chain, nodes, "delay");
        changes.Clear();
        Param(old, "bpm").Value = 80;
        Assert.Empty(changes);
        chain.ParamSets.Clear();
        tempo.BPM = 160;
        Assert.Equal((current.NodeId, "bpm", 160f), Assert.Single(chain.ParamSets));
        tempo.Dispose();
        chain.ParamSets.Clear();
        tempo.BPM = 170;
        Assert.Empty(chain.ParamSets);
        Assert.False(tempo.TapCommand.CanExecute(null));
    }

    [Fact]
    public void LegacyDelayWithoutSyncMetadata_IsNotATempoTarget()
    {
        var type = new DspModuleType("delay", "Legacy delay", [new("bpm", "BPM", 20, 400, 120)]);
        var chain = new FakeDspChainService([type]);
        var nodes = new ObservableCollection<PedalNodeViewModel>();
        Add(chain, nodes, "delay");
        using var tempo = new RigTempoViewModel(nodes);
        Assert.False(tempo.HasTargets);
        tempo.BPM = 150;
        Assert.Empty(chain.ParamSets);
    }

    [Fact]
    public void TapsUseMonotonicIntervals_RejectBounce_AndResetAfterLongPause()
    {
        var (chain, nodes) = Create();
        Add(chain, nodes, "delay");
        var clock = new Clock();
        using var tempo = new RigTempoViewModel(nodes, clock);
        tempo.TapCommand.Execute(null);
        clock.Advance(100);
        tempo.TapCommand.Execute(null);
        Assert.Empty(chain.ParamSets);
        clock.Advance(500); // 600 ms from accepted first beat, not 500 from the rejected bounce
        tempo.TapCommand.Execute(null);
        Assert.Equal(100d, tempo.BPM);
        clock.Advance(3001);
        tempo.TapCommand.Execute(null);
        Assert.Single(chain.ParamSets);
        clock.Advance(150);
        tempo.TapCommand.Execute(null);
        Assert.Equal(400d, tempo.BPM);
        clock.Advance(3000);
        tempo.TapCommand.Execute(null);
        Assert.Equal(20d, tempo.BPM);
    }

    [Fact]
    public void TempoWritesClampToTargetBounds_AndRejectNonFiniteValues()
    {
        var (chain, nodes) = Create();
        Add(chain, nodes, "delay");
        using var tempo = new RigTempoViewModel(nodes);
        tempo.BPM = 900;
        Assert.Equal(400d, tempo.BPM);
        tempo.BPM = -1;
        Assert.Equal(20d, tempo.BPM);
        chain.ParamSets.Clear();
        tempo.BPM = double.NaN;
        tempo.BPM = double.PositiveInfinity;
        tempo.BPM = null;
        Assert.Empty(chain.ParamSets);
    }

    [Fact]
    public void LazyPedalboardTempo_FollowsTheBoardsLiveNodes()
    {
        var (chain, _) = Create();
        var board = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        using var tempo = board.Tempo;
        Assert.Same(tempo, board.Tempo);
        Assert.False(tempo.HasTargets);
        board.AddGear(chain.Catalog[0]);
        Assert.True(tempo.HasTargets);
        tempo.BPM = 150;
        Assert.Equal(150, Param(Assert.Single(board.Nodes), "bpm").Value);
    }

    [Fact]
    public void ReplacingTheSeatNodes_StartsAFreshTapSession()
    {
        var (chain, nodes) = Create();
        Add(chain, nodes, "delay");
        var clock = new Clock();
        using var tempo = new RigTempoViewModel(nodes, clock);
        tempo.TapCommand.Execute(null);
        clock.Advance(600);
        nodes.Clear();
        Add(chain, nodes, "delay");
        tempo.TapCommand.Execute(null);
        Assert.Empty(chain.ParamSets);
        clock.Advance(400);
        tempo.TapCommand.Execute(null);
        Assert.Equal(150d, tempo.BPM);
    }

    private static PedalParamViewModel Param(PedalNodeViewModel node, string id) => Assert.Single(node.Params, p => p.Id == id);

    private static (FakeDspChainService Chain, ObservableCollection<PedalNodeViewModel> Nodes) Create()
    {
        DspParamInfo[] parameters = [new("bpm", "BPM", 20, 400, 120), new("sync", "Sync", 0, 1, 0), new("mix", "Mix", 0, 1, .3f)];
        return (new FakeDspChainService([new("delay", "Delay", parameters), new("reverb", "Reverb", parameters)]), []);
    }

    private static PedalNodeViewModel Add(FakeDspChainService chain, ObservableCollection<PedalNodeViewModel> nodes, string type)
    {
        var node = chain.AddNode(type)!;
        var model = new PedalNodeViewModel(chain, node, chain.Catalog.Single(t => t.Id == type), FakeLocalization.English());
        nodes.Add(model);
        return model;
    }

    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(long milliseconds) => _milliseconds += milliseconds;
    }
}
