using DotCharter.Tone.Audio.Midi;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Infrastructure.Input;

/// <summary>
///     The MIDI controller as the editor's bindings see it: PRESSES, delivered on
///     the UI thread, and whether a device is open to send them. Settings ▸
///     Keybinds learns a trigger from it; the editor's dispatch runs the bound verb.
/// </summary>
public interface IMidiTriggerInput
{
    /// <summary>
    ///     One press — a note-on, or a CC crossing up through 64 — raised ON THE
    ///     UI THREAD, in arrival order. A press is never merged with another or
    ///     dropped (short of <see cref="MidiTriggerInput.QueueCapacity" /> waiting
    ///     on a UI thread that stopped draining).
    /// </summary>
    event Action<MidiTrigger>? Pressed;

    /// <summary>Raised on the UI thread when the device, its connection or its error may have changed.</summary>
    event Action? StatusChanged;

    /// <summary>False when the audio engine has no MIDI input at all (the null transport).</summary>
    bool IsAvailable { get; }

    /// <summary>True while the chosen device is open and its messages arrive.</summary>
    bool IsConnected { get; }

    /// <summary>The MIDI input picked in the rig's MIDI panel, or null.</summary>
    string? DeviceName { get; }

    /// <summary>Why the device did not open, when it did not.</summary>
    string? Error { get; }
}

/// <summary>
///     <see cref="IMidiTriggerInput" /> over the rig's MIDI input
///     (<see cref="IRigMidiInputSource" />) — the ONE MIDI input device the app
///     opens, picked in the rig's MIDI panel and restored at startup. Decoding
///     runs on the MIDI thread and costs a lock and a queue slot; everything a
///     press DOES runs on the UI thread through <see cref="IUiDispatcher" />.
/// </summary>
public sealed class MidiTriggerInput : IMidiTriggerInput, IDisposable
{
    /// <summary>Presses held for a UI thread that has not drained them yet.</summary>
    public const int QueueCapacity = 1024;

    private readonly IRigMidiInputSource? _source;
    private readonly IRigConfigService _config;
    private readonly IUiDispatcher _ui;
    private readonly object _gate = new();
    private readonly Queue<MidiTrigger> _pending = new();

    // Which side of 64 each (channel, CC) last sat on, so a CC is a press only on
    // the way UP: a button's 127 presses, its 0 releases, a knob fires once.
    private readonly bool[] _controlHigh = new bool[16 * 128];
    private int _edgeGeneration = int.MinValue;
    private bool _posted;
    private bool _disposed;

    public MidiTriggerInput(IRigMidiInputSource? source, IRigConfigService config, IUiDispatcher ui)
    {
        _source = source;
        _config = config;
        _ui = ui;
        if (source is null) return;
        source.RigMidiNoteReceived += OnNote;
        source.RigMidiControlReceived += OnControl;
        source.RigMidiInputStatusChanged += OnStatus;
    }

    public event Action<MidiTrigger>? Pressed;

    public event Action? StatusChanged;

    public bool IsAvailable => _source is not null;

    public bool IsConnected => _source?.RigMidiInputConnected == true;

    public string? DeviceName
    {
        get
        {
            try { return _config.LoadRigConfig().RigMidiInputDevice is { Length: > 0 } name ? name : null; }
            catch (Exception) { return null; }
        }
    }

    public string? Error => _source?.RigMidiInputError;

    /// <summary>Presses refused because <see cref="QueueCapacity" /> were already waiting.</summary>
    public int Overflowed { get; private set; }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
        }

        if (_source is null) return;
        _source.RigMidiNoteReceived -= OnNote;
        _source.RigMidiControlReceived -= OnControl;
        _source.RigMidiInputStatusChanged -= OnStatus;
    }

    // ── MIDI thread ───────────────────────────────────────────────────────────

    private void OnNote(int generation, int channel, int note, int velocity)
    {
        if ((uint)channel > 15 || (uint)note > 127 || velocity is < 1 or > 127) return;
        Enqueue(generation, MidiTrigger.Note(channel, note));
    }

    private void OnControl(int generation, int channel, int controller, int value)
    {
        if ((uint)channel > 15 || (uint)controller > 127 || (uint)value > 127) return;
        lock (_gate)
        {
            if (_disposed || generation != _source?.RigMidiInputGeneration) return;
            if (generation != _edgeGeneration)
            {
                // A reopened device starts every control below the line again.
                Array.Clear(_controlHigh);
                _edgeGeneration = generation;
            }

            var slot = channel * 128 + controller;
            var wasHigh = _controlHigh[slot];
            var high = value >= 64;
            _controlHigh[slot] = high;
            if (!high || wasHigh) return;
        }

        Enqueue(generation, MidiTrigger.ControlChange(channel, controller));
    }

    private void Enqueue(int generation, MidiTrigger trigger)
    {
        lock (_gate)
        {
            if (_disposed || generation != _source?.RigMidiInputGeneration) return;
            if (_pending.Count >= QueueCapacity)
            {
                Overflowed++;
                return;
            }

            _pending.Enqueue(trigger);
            if (_posted) return;
            _posted = true;
        }

        _ui.PostNormal(Drain);
    }

    private void OnStatus() => _ui.PostNormal(() =>
    {
        if (!_disposed) StatusChanged?.Invoke();
    });

    // ── UI thread ─────────────────────────────────────────────────────────────

    private void Drain()
    {
        MidiTrigger[] presses;
        lock (_gate)
        {
            _posted = false;
            if (_disposed) return;
            presses = [.. _pending];
            _pending.Clear();
        }

        foreach (var press in presses) Pressed?.Invoke(press);
    }
}
