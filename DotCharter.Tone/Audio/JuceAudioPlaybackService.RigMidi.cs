// Partial: rig MIDI control input; engine lifetime stays under _gate.
using DotCharter.Tone.Audio.Midi;
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService : IRigMidiInputSource
{
    private JuceMidiInput? _rigMidiInput;
    private string? _rigMidiDeviceName;
    private string? _rigMidiInputError;
    private int _rigMidiGeneration;

    public event Action<int, int, int, int>? RigMidiControlReceived;
    public event Action<int, int, int>? RigMidiProgramReceived;
    public event Action<int, int, int, int>? RigMidiNoteReceived;
    public int RigMidiInputGeneration => Volatile.Read(ref _rigMidiGeneration);
    public bool RigMidiInputConnected => Volatile.Read(ref _rigMidiInput) is not null;
    public event Action? RigMidiInputStatusChanged;
    public string? RigMidiInputError { get { lock (_gate) return _rigMidiInputError; } }

    public IReadOnlyList<string> GetRigMidiInputs()
    {
        try { return JuceAudioEngine.EnumerateMidiInputs(); }
        catch (Exception ex)
        {
            lock (_gate) _rigMidiInputError = ex.Message;
            RigMidiInputStatusChanged?.Invoke();
            return [];
        }
    }

    public bool SelectRigMidiInput(string? deviceName)
    {
        bool opened;
        lock (_gate)
        {
            if (IsDisposing) return false;
            _rigMidiDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
            opened = RestoreRigMidiInputLocked(_engine);
        }
        RigMidiInputStatusChanged?.Invoke();
        return opened;
    }

    private void CloseRigMidiInputLocked()
    {
        Interlocked.Increment(ref _rigMidiGeneration);
        var input = _rigMidiInput;
        _rigMidiInput = null;
        input?.Dispose();
    }

    private bool RestoreRigMidiInputLocked(JuceAudioEngine? engine)
    {
        try
        {
            CloseRigMidiInputLocked();
            _rigMidiInputError = null;
            if (_rigMidiDeviceName is null) return true;
            if (engine is null) return false; // selection is replayed when the engine starts
            var devices = JuceAudioEngine.EnumerateMidiInputs();
            var index = devices.ToList().FindIndex(n => string.Equals(n, _rigMidiDeviceName, StringComparison.Ordinal));
            if (index < 0)
            {
                _rigMidiInputError = "MIDI input device is unavailable.";
                return false;
            }
            var generation = Volatile.Read(ref _rigMidiGeneration);
            _rigMidiInput = engine.OpenMidiInput(index, message =>
            {
                // This is the MIDI thread. Subscribers enqueue bounded control updates;
                // they must never acquire the engine gate or touch UI controls here.
                if (generation != Volatile.Read(ref _rigMidiGeneration)) return;
                if (RigMidiControlMessage.TryDecode(message.Data, out var channel, out var controller, out var value))
                    RigMidiControlReceived?.Invoke(generation, channel, controller, value);
                else if (RigMidiProgramMessage.TryDecode(message.Data, out var pcChannel, out var program))
                    RigMidiProgramReceived?.Invoke(generation, pcChannel, program);
                else if (RigMidiNoteMessage.TryDecode(message.Data, out var noteChannel, out var note, out var velocity))
                    RigMidiNoteReceived?.Invoke(generation, noteChannel, note, velocity);
            });
            return true;
        }
        catch (Exception ex)
        {
            _rigMidiInputError = ex.Message;
            return false;
        }
    }
}
