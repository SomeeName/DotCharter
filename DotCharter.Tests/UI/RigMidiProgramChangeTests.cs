using DotCharter.Tone.Audio.Midi;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The MIDI bridge's PROGRAM CHANGE half and the performance settings: a PC
///     from the MIDI thread reaches the rig on the UI post (a burst is "go to the
///     last one", never a queue of tone switches), the last message is shown so a
///     player can see the pedal works, and the channel / program-change / scene-CC
///     settings persist. The CC half is <c>UI/RigMidiInputTests</c>; what the rig
///     DOES with them is <c>UI/RigSetlistsTests</c>.
/// </summary>
public sealed class RigMidiProgramChangeTests
{
    [Fact]
    public void AProgramChange_ReachesTheRigOnTheUiPost_AndABurstIsItsLastOne()
    {
        var source = new Source();
        var posted = new Queue<Action>();
        var programs = new List<(int Channel, int Program)>();
        using var model = new RigMidiInputViewModel(source, null, FakeLocalization.English(), posted.Enqueue, (_, _, _) => { },
            (channel, program) => programs.Add((channel, program)));

        source.SendProgram(2, 3);
        source.SendProgram(2, 4);
        source.SendProgram(2, 5);
        Assert.Empty(programs);                       // nothing on the MIDI thread
        Assert.Single(posted);                        // one post for the burst

        posted.Dequeue()();

        Assert.Equal([(2, 5)], programs);
        Assert.Contains("5", model.LastMessage);
        Assert.Contains("3", model.LastMessage);      // channel 3 (1-based) — the pedal's own number
    }

    [Fact]
    public void AStaleGeneration_OrAClearedQueue_DropsTheProgramChange()
    {
        var source = new Source();
        var posted = new Queue<Action>();
        var programs = new List<int>();
        using var model = new RigMidiInputViewModel(source, null, FakeLocalization.English(), posted.Enqueue, (_, _, _) => { },
            (_, program) => programs.Add(program));

        source.SendProgram(0, 7);
        model.ClearPending();                         // e.g. a MIDI learn started
        posted.Dequeue()();
        source.RigMidiInputGeneration++;              // the device was reopened
        source.SendProgram(0, 8, generation: 0);

        Assert.Empty(programs);
        Assert.Empty(posted);
    }

    [Fact]
    public void ThePerformanceSettings_PersistAndReload()
    {
        var config = new FakeConfigurationService();
        using (var model = new RigMidiInputViewModel(new Source(), config, FakeLocalization.English(), _ => { }, (_, _, _) => { }))
        {
            Assert.Equal(RigMidiRouting.Default, model.Routing);
            model.SelectedChannel = model.ChannelOptions.Single(o => o.Channel == 10);
            model.IsProgramChangeEnabled = false;
            model.SceneCc = 80;
        }

        var saved = config.LoadAppConfig();
        Assert.Equal((10, false, 80), (saved.RigMidiChannel, saved.RigMidiProgramChange, saved.RigMidiSceneCc));

        using var again = new RigMidiInputViewModel(new Source(), config, FakeLocalization.English(), _ => { }, (_, _, _) => { });
        Assert.Equal(new RigMidiRouting(10, false, 80), again.Routing);
        again.IsSceneCcEnabled = false;
        Assert.Equal(-1, config.LoadAppConfig().RigMidiSceneCc);
        Assert.Equal(17, again.ChannelOptions.Count);  // omni + 16
    }

    private sealed class Source : IRigMidiInputSource
    {
        public IReadOnlyList<string> GetRigMidiInputs() => ["Controller"];
        public bool SelectRigMidiInput(string? deviceName) => true;
        public string? RigMidiInputError => null;
        public int RigMidiInputGeneration { get; set; }
        public bool RigMidiInputConnected => true;
        public event Action<int, int, int, int>? RigMidiControlReceived;
        public event Action? RigMidiInputStatusChanged;
        public event Action<int, int, int>? RigMidiProgramReceived;

        public void SendProgram(int channel, int program, int? generation = null) =>
            RigMidiProgramReceived?.Invoke(generation ?? RigMidiInputGeneration, channel, program);

        // keep the compiler honest about the two events this fake never raises
        public void Unused()
        {
            RigMidiControlReceived?.Invoke(0, 0, 0, 0);
            RigMidiInputStatusChanged?.Invoke();
        }
    }
}
