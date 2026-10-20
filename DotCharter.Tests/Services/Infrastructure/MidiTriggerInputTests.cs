using DotCharter.Tone.Audio.Midi;
using DotCharter.Services.Infrastructure.Input;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     The MIDI controller's presses as the editor binds see them (#310): decoded
///     on the MIDI thread, delivered on the UI thread, never merged or dropped —
///     a note-on is a press, and a CC is a press only on its way UP through 64.
/// </summary>
public sealed class MidiTriggerInputTests
{
    private sealed class Source : IRigMidiInputSource
    {
        public int Generation { get; set; } = 1;

        public bool Connected { get; set; } = true;

        public IReadOnlyList<string> GetRigMidiInputs() => [];

        public bool SelectRigMidiInput(string? deviceName) => true;

        public string? RigMidiInputError { get; set; }

        public int RigMidiInputGeneration => Generation;

        public bool RigMidiInputConnected => Connected;

        public event Action<int, int, int, int>? RigMidiControlReceived;

        public event Action? RigMidiInputStatusChanged;

        public event Action<int, int, int, int>? RigMidiNoteReceived;

        public void Note(int channel, int note, int velocity = 100, int? generation = null) =>
            RigMidiNoteReceived?.Invoke(generation ?? Generation, channel, note, velocity);

        public void Control(int channel, int controller, int value, int? generation = null) =>
            RigMidiControlReceived?.Invoke(generation ?? Generation, channel, controller, value);

        public void Status() => RigMidiInputStatusChanged?.Invoke();

        public int Subscribers => (RigMidiNoteReceived?.GetInvocationList().Length ?? 0) +
                                  (RigMidiControlReceived?.GetInvocationList().Length ?? 0) +
                                  (RigMidiInputStatusChanged?.GetInvocationList().Length ?? 0);
    }

    /// <summary>A UI thread that runs only when the test pumps it.</summary>
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int Posted { get; private set; }

        public bool CheckAccess() => true;

        public void Post(Action action) => Enqueue(action);

        public void PostNormal(Action action) => Enqueue(action);

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public void Pump()
        {
            while (_queue.TryDequeue(out var action)) action();
        }

        private void Enqueue(Action action)
        {
            Posted++;
            _queue.Enqueue(action);
        }
    }

    private static (MidiTriggerInput Input, Source Source, QueuedDispatcher Ui, List<MidiTrigger> Presses) Build()
    {
        var source = new Source();
        var ui = new QueuedDispatcher();
        var input = new MidiTriggerInput(source, new FakeConfigurationService(), ui);
        var presses = new List<MidiTrigger>();
        input.Pressed += presses.Add;
        return (input, source, ui, presses);
    }

    [Fact]
    public void ANoteOn_IsOnePress_DeliveredOnlyOnTheUiThread()
    {
        var (input, source, ui, presses) = Build();
        using var _ = input;

        source.Note(9, 36);
        Assert.Empty(presses);

        ui.Pump();
        Assert.Equal([MidiTrigger.Note(9, 36)], presses);
    }

    [Fact]
    public void ABurstBeforeTheUiDrains_IsEveryPressInOrder_NotOneMerged()
    {
        var (input, source, ui, presses) = Build();
        using var _ = input;

        source.Note(0, 60);
        source.Note(0, 60);
        source.Note(0, 62);
        source.Control(0, 20, 127);
        ui.Pump();

        Assert.Equal([MidiTrigger.Note(0, 60), MidiTrigger.Note(0, 60), MidiTrigger.Note(0, 62), MidiTrigger.ControlChange(0, 20)],
            presses);
        Assert.Equal(1, ui.Posted);
    }

    [Fact]
    public void ACc_PressesOnlyOnItsWayUpThrough64()
    {
        var (input, source, ui, presses) = Build();
        using var _ = input;

        source.Control(0, 20, 127); // button down
        source.Control(0, 20, 127); // repeated "on"
        source.Control(0, 20, 0); // button up
        source.Control(0, 20, 127); // down again
        foreach (var value in new[] { 0, 10, 40, 63, 64, 90, 127, 100 }) source.Control(1, 7, value); // a knob turned up
        ui.Pump();

        Assert.Equal([MidiTrigger.ControlChange(0, 20), MidiTrigger.ControlChange(0, 20), MidiTrigger.ControlChange(1, 7)],
            presses);
    }

    [Fact]
    public void AReopenedDevice_StartsEveryControlBelowTheLine()
    {
        var (input, source, ui, presses) = Build();
        using var _ = input;

        source.Control(0, 20, 127);
        source.Generation = 2;
        source.Control(0, 20, 127);
        ui.Pump();

        Assert.Equal(2, presses.Count);
    }

    [Fact]
    public void AMessageFromAClosedDevice_IsDropped()
    {
        var (input, source, ui, presses) = Build();
        using var _ = input;

        source.Note(0, 60, generation: 0);
        source.Control(0, 20, 127, generation: 0);
        source.Note(0, 61, velocity: 0);
        ui.Pump();

        Assert.Empty(presses);
    }

    [Fact]
    public void AUiThreadThatStoppedDraining_HoldsAtMostTheCapacity_AndCountsTheRest()
    {
        var (input, source, ui, presses) = Build();
        using var _ = input;

        for (var i = 0; i < MidiTriggerInput.QueueCapacity + 5; i++) source.Note(0, i % 128);
        ui.Pump();

        Assert.Equal(MidiTriggerInput.QueueCapacity, presses.Count);
        Assert.Equal(5, input.Overflowed);
    }

    [Fact]
    public void Status_ComesFromTheSourceAndTheRigConfig_AndAChangeIsRaisedOnTheUiThread()
    {
        var source = new Source { Connected = false, RigMidiInputError = "busy" };
        var config = new FakeConfigurationService();
        config.AppConfig.RigMidiInputDevice = "Akai MPK mini 3";
        var ui = new QueuedDispatcher();
        using var input = new MidiTriggerInput(source, config, ui);
        var changes = 0;
        input.StatusChanged += () => changes++;

        Assert.True(input.IsAvailable);
        Assert.False(input.IsConnected);
        Assert.Equal("Akai MPK mini 3", input.DeviceName);
        Assert.Equal("busy", input.Error);

        source.Status();
        Assert.Equal(0, changes);
        ui.Pump();
        Assert.Equal(1, changes);
    }

    [Fact]
    public void WithNoMidiSource_ItSaysSo_AndNeverPresses()
    {
        using var input = new MidiTriggerInput(null, new FakeConfigurationService(), new QueuedDispatcher());

        Assert.False(input.IsAvailable);
        Assert.False(input.IsConnected);
        Assert.Null(input.DeviceName);
    }

    [Fact]
    public void Dispose_LetsGoOfTheSource()
    {
        var (input, source, _, _) = Build();
        Assert.Equal(3, source.Subscribers);

        input.Dispose();

        Assert.Equal(0, source.Subscribers);
    }

    [Theory]
    [InlineData(new byte[] { 0x99, 36, 100 }, true, 9, 36)]
    [InlineData(new byte[] { 0x90, 60, 1 }, true, 0, 60)]
    [InlineData(new byte[] { 0x90, 60, 0 }, false, 0, 0)] // velocity 0 is a note-off
    [InlineData(new byte[] { 0x80, 60, 64 }, false, 0, 0)] // note-off
    [InlineData(new byte[] { 0xB0, 20, 127 }, false, 0, 0)] // a CC
    [InlineData(new byte[] { 0x90, 60 }, false, 0, 0)]
    public void ANoteOn_DecodesOnlyWithAVelocity(byte[] bytes, bool decodes, int channel, int note)
    {
        Assert.Equal(decodes, RigMidiNoteMessage.TryDecode(bytes, out var c, out var n, out _));
        Assert.Equal(channel, c);
        Assert.Equal(note, n);
    }
}
