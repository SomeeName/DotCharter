using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Midi;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

public sealed class RigMidiInputTests
{
    [Fact]
    public async Task RebuildStatusBeforeConnectionCompletionUsesCurrentConnectedState()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source
        {
            RigMidiInputConnected = false,
            Open = _ => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); return false; }
        };
        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var ready = new SemaphoreSlim(0);
        var received = new List<int>();
        using var model = new RigMidiInputViewModel(source, null, FakeLocalization.English(),
            action => { posted.Enqueue(action); ready.Release(); }, (_, _, value) => received.Add(value));
        model.SelectedDevice = "Controller";
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            source.RigMidiInputConnected = true;
            source.NotifyStatus(); // rebuild notification runs while connection is still pending
            Assert.True(posted.TryDequeue(out var status));
            status!();
            Assert.True(ready.Wait(0, TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); }
        await ready.WaitAsync(TestContext.Current.CancellationToken);
        while (posted.TryDequeue(out var action)) action();
        source.Send(0, 7, 99);
        while (posted.TryDequeue(out var action)) action();
        Assert.Equal(99, Assert.Single(received));
    }

    [Fact]
    public void RigIdentityChangesDiscardQueuedControls_OrdinaryParameterChangesDoNot()
    {
        var source = new Source();
        var posted = new Queue<Action>();
        var received = new List<int>();
        var chains = new Chains();
        var rig = TestRigBuilders.Pedalboard(chains.For(0), new FakeDspTonePresetService(), FakeLocalization.English(), chains: chains);
        using var model = new RigMidiInputViewModel(source, null, FakeLocalization.English(), posted.Enqueue, (_, _, value) => received.Add(value));
        model.BindRigScope(rig);
        rig.SetSeats([new(0, "A"), new(1, "B")]);
        source.Send(0, 7, 12);
        rig.SelectedSeat = rig.Seats[1];
        posted.Dequeue()();
        Assert.Empty(received);
        var node = rig.AddGear(chains.For(1).Catalog[0])!;
        source.Send(0, 7, 13);
        rig.SelectedNode = node;
        posted.Dequeue()();
        Assert.Empty(received);
        source.Send(0, 7, 14);
        rig.ResyncAll();
        posted.Dequeue()();
        Assert.Empty(received);
        source.Send(0, 7, 15);
        rig.Nodes[0].Params[0].Value = .2f;
        posted.Dequeue()();
        Assert.Equal(15, Assert.Single(received));
    }
    private sealed class Chains : IPlayerDspChains
    {
        public IReadOnlyList<IDspChainService> All { get; } = [new FakeDspChainService(), new FakeDspChainService()];
        public int MaxPlayers => 2;
        public IDspChainService For(int player) => All[player];
        public void AttachEngineToAll(nint engine) { }
    }

    [Fact]
    public async Task DeviceSelectionSuspendsOldMessagesUntilTheCurrentConnectionCompletes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Open = _ => { entered.SetResult(); return release.Task.GetAwaiter().GetResult(); } };
        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var ready = new SemaphoreSlim(0);
        var received = new List<int>();
        using var model = new RigMidiInputViewModel(source, null, FakeLocalization.English(), action => { posted.Enqueue(action); ready.Release(); },
            (_, _, value) => received.Add(value));
        model.SelectedDevice = "Controller";
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            source.Send(0, 7, 12); // old device callback while replacement is blocked
            Assert.Empty(posted);
            model.ClearPending(); // user starts learning during replacement
            source.Send(0, 7, 13);
            Assert.Empty(posted);
        }
        finally { release.TrySetResult(true); }
        await ready.WaitAsync(TestContext.Current.CancellationToken);
        while (posted.TryDequeue(out var action)) action();
        source.Send(0, 7, 99);
        while (posted.TryDequeue(out var action)) action();
        Assert.Equal(99, Assert.Single(received));
    }

    [Fact]
    public void EngineRebuildDiscardsAlreadyQueuedMessagesFromThePreviousNativeInput()
    {
        var source = new Source();
        var posted = new Queue<Action>();
        var received = new List<int>();
        using var model = new RigMidiInputViewModel(source, null, FakeLocalization.English(), posted.Enqueue, (_, _, value) => received.Add(value));
        source.Send(0, 7, 12);
        source.RigMidiInputGeneration++;
        posted.Dequeue()();
        Assert.Empty(received);
        source.Send(0, 7, 99);
        posted.Dequeue()();
        Assert.Equal(99, Assert.Single(received));
    }

    [Fact]
    public void CoalescesPerControllerAndChannel_AndDiscardsMessagesBeforeLearningOrDisposal()
    {
        var source = new Source();
        var posted = new Queue<Action>();
        var received = new List<(int Channel, int Controller, int Value)>();
        using var model = new RigMidiInputViewModel(source, null, FakeLocalization.English(), posted.Enqueue,
            (channel, controller, value) => received.Add((channel, controller, value)));
        for (var value = 0; value < 128; value++) source.Send(0, 7, value);
        source.Send(1, 7, 42);
        source.Send(16, 7, 99); // malformed controller messages never reach the bank
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(new[] { (0, 7, 127), (1, 7, 42) }, received);
        received.Clear();
        source.Send(0, 9, 1);
        model.ClearPending(); // arming MIDI learn drops an already queued message
        source.Send(0, 10, 2);
        posted.Dequeue()();
        Assert.Equal((0, 10, 2), Assert.Single(received));
        source.Send(0, 11, 3);
        model.Dispose();
        posted.Dequeue()();
        Assert.Single(received);
        Assert.Equal(0, source.SubscriberCount);
    }

    [Theory]
    [InlineData(new byte[] { 0xb0, 7, 127 }, true, 0)]
    [InlineData(new byte[] { 0xbf, 1, 0 }, true, 15)]
    [InlineData(new byte[] { 0x90, 60, 100 }, false, 0)]
    [InlineData(new byte[] { 0xb0, 128, 1 }, false, 0)]
    [InlineData(new byte[] { 0xb0, 1, 128 }, false, 0)]
    [InlineData(new byte[] { 0xb0, 1 }, false, 0)]
    [InlineData(new byte[] { 0xf0, 1, 2, 0xf7 }, false, 0)]
    public void OnlyCompleteControlChangesAreDecoded(byte[] bytes, bool expected, int expectedChannel)
    {
        Assert.Equal(expected, RigMidiControlMessage.TryDecode(bytes, out var channel, out var controller, out var value));
        if (!expected) return;
        Assert.Equal(expectedChannel, channel);
        Assert.Equal(bytes[1], controller);
        Assert.Equal(bytes[2], value);
    }

    private sealed class Source : IRigMidiInputSource
    {
        public IReadOnlyList<string> GetRigMidiInputs() => ["Controller"];
        public Func<string?, bool>? Open { get; init; }
        public bool SelectRigMidiInput(string? deviceName) => Open?.Invoke(deviceName) ?? true;
        public string? RigMidiInputError => null;
        public int RigMidiInputGeneration { get; set; }
        public bool RigMidiInputConnected { get; set; } = true;
        public event Action<int, int, int, int>? RigMidiControlReceived;
        public event Action? RigMidiInputStatusChanged;
        public void NotifyStatus() => RigMidiInputStatusChanged?.Invoke();
        public int SubscriberCount => RigMidiControlReceived?.GetInvocationList().Length ?? 0;
        public void Send(int channel, int controller, int value) => RigMidiControlReceived?.Invoke(RigMidiInputGeneration, channel, controller, value);
    }
}
