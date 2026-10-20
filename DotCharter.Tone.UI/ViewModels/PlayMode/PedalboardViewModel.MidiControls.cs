// MIDI control bridge shared by the editor and Play Mode rig hosts.
using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class PedalboardViewModel : IDisposable
{
    private RigMidiInputViewModel? _midiControls;
    public RigMidiInputViewModel MidiControls
    {
        get
        {
            if (_midiControls is not null) return _midiControls;
            _midiControls = new(_audio as IRigMidiInputSource, _config, _loc,
                action => { if (_dispatcher is null) Avalonia.Threading.Dispatcher.UIThread.Post(action); else _dispatcher.Post(action); },
                HandleMidiControl, HandleMidiProgram);
            _midiControls.BindRigScope(this);
            _midiControls.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RigMidiInputViewModel.LastMessage)) OnPropertyChanged(nameof(MidiMonitorText));
            };
            Controls.LearningStarted += _midiControls.ClearPending;
            return _midiControls;
        }
    }

    /// <summary>The scene bar's MIDI monitor: the last message the controller sent.
    /// Reads the MIDI bridge only if something already made it — a monitor must
    /// not be what opens the device.</summary>
    public string MidiMonitorText => _midiControls?.LastMessage ?? string.Empty;

    /// <summary>What a program change and the scene CC do right now.</summary>
    internal RigMidiRouting MidiRouting => _midiControls?.Routing ?? LoadMidiRouting();

    private RigMidiRouting LoadMidiRouting()
    {
        try { return RigMidiRouting.From(_config.LoadRigConfig()); }
        catch (Exception) { return RigMidiRouting.Default; }
    }

    /// <summary>
    ///     A CC from the controller, on the UI thread. The SCENE CC (69 by
    ///     default, value 0..7 = A..H) selects a scene; every other CC drives the
    ///     knob assignments. A MIDI learn in progress takes the next CC whatever
    ///     it is — the player is pointing a pedal at a knob.
    /// </summary>
    internal void HandleMidiControl(int channel, int controller, int value)
    {
        if (!Controls.Editor.IsLearning && MidiRouting.IsSceneSelect(channel, controller))
        {
            if (RigMidiRouting.SceneFor(value) is { } scene) SelectSceneAt(scene);
            return;
        }

        Controls.HandleMidiControl(channel, controller, value);
    }

    /// <summary>
    ///     A PROGRAM CHANGE, on the UI thread: program n is entry n of the active
    ///     setlist (0 = the first); with no setlist active, the n-th of the
    ///     player's own tones (My Tones, in their order). A program with nothing
    ///     behind it is ignored, never wrapped onto another tone.
    /// </summary>
    internal void HandleMidiProgram(int channel, int program)
    {
        if (!MidiRouting.AcceptsProgram(channel)) return;
        if (Setlists.SelectProgram(program)) return;
        var own = _presets.UserPresets;
        if (program >= 0 && program < own.Count) Browser.ApplyPreset(own[program]);
    }

    public void Dispose()
    {
        _tempo?.Dispose();
        _moduleMeters?.Dispose();
        _outputMeters?.Dispose();
        _boardMeters?.Dispose();
        if (_midiControls is null) return;
        Controls.LearningStarted -= _midiControls.ClearPending;
        _midiControls.Dispose();
    }
}
