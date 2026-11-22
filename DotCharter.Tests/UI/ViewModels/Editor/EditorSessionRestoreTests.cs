using System.Text.Json;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     the editor's session switches come back after a restart. Each case sets
///     the value on one transport, "restarts" (the config round-trips through JSON, the
///     way the file does), builds a NEW transport over that store and reads it back,
///     plus, where the engine has the value, from the audio service.
/// </summary>
public sealed class EditorSessionRestoreTests
{
    private static FakeConfigurationService Restart(FakeConfigurationService old) => new()
    {
        AppConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(old.AppConfig))!,
    };

    private static (TransportViewModel Transport, FakeAudioPlaybackService Audio) Boot(FakeConfigurationService config)
    {
        var audio = new FakeAudioPlaybackService();
        return (new TransportViewModel(audio, new ProjectState(), config, FakeLocalization.KeysOnly(),
            new OnboardingWizardRig.InlineDispatcher()), audio);
    }

    public static TheoryData<string> Settings =>
    [
        "SongVolume", "InputVolume", "MidiVolume", "Volume", "MonitorVolume", "IsSongMuted", "IsSongSoloed",
        "IsInputMuted", "IsInputSoloed", "IsMidiMuted", "IsMidiSoloed", "IsMasterMuted", "Pan", "PitchShift",
        "MetronomeEnabled", "ClapsEnabled", "MidiEnabled", "MidiInstrument", "PlaybackSpeed", "GridDivisor",
        "TonePreview",
    ];

    [Theory]
    [MemberData(nameof(Settings))]
    public void ASetting_SurvivesARestart(string setting)
    {
        var config = new FakeConfigurationService();
        var (first, _) = Boot(config);
        Change(first, setting);

        var (second, audio) = Boot(Restart(config));

        Assert.True(IsRestored(second, audio, setting), setting + " was not restored");
    }

    [Fact]
    public void ChartSoundAt200PercentSurvivesARestart()
    {
        var config = new FakeConfigurationService();
        var (first, _) = Boot(config);
        first.MidiVolume = 2.0;

        var (second, audio) = Boot(Restart(config));

        Assert.Equal(2.0, second.MidiVolume);
        Assert.Equal(2.0, audio.VstInstrumentVolume);
    }

    private static void Change(TransportViewModel t, string setting)
    {
        switch (setting)
        {
            case "SongVolume": t.SongVolume = 0.4; break;
            case "InputVolume": t.InputVolume = 0.65; break;
            case "MidiVolume": t.MidiVolume = 0.25; break;
            case "Volume": t.Volume = 0.8; break;
            case "MonitorVolume": t.MonitorVolume = 0.35; break;
            case "IsSongMuted": t.IsSongMuted = true; break;
            case "IsSongSoloed": t.IsSongSoloed = true; break;
            case "IsInputMuted": t.IsInputMuted = true; break;
            case "IsInputSoloed": t.IsInputSoloed = true; break;
            case "IsMidiMuted": t.IsMidiMuted = true; break;
            case "IsMidiSoloed": t.IsMidiSoloed = true; break;
            case "IsMasterMuted": t.IsMasterMuted = true; break;
            case "Pan": t.Pan = -0.5; break;
            case "PitchShift": t.PitchShift = 2; break;
            case "MetronomeEnabled": t.MetronomeEnabled = true; break;
            case "ClapsEnabled": t.ClapsEnabled = true; break;
            case "MidiEnabled": t.MidiEnabled = true; break;
            case "MidiInstrument": t.SelectedMidiInstrumentIndex = 40; break;
            case "PlaybackSpeed": t.PlaybackSpeed = 0.75; break;
            case "GridDivisor": t.GridDivisor = 16; break;
            case "TonePreview": t.RememberTonePreview(true, true); break;
            default: throw new ArgumentOutOfRangeException(nameof(setting), setting, null);
        }
    }

    private static bool IsRestored(TransportViewModel t, FakeAudioPlaybackService audio, string setting) => setting switch
    {
        "SongVolume" => t.SongVolume == 0.4 && audio.SongVolume == 0.4,
        "InputVolume" => t.InputVolume == 0.65 && audio.InputVolume == 0.65,
        "MidiVolume" => t.MidiVolume == 0.25 && audio.VstInstrumentVolume == 0.25,
        "Volume" => t.Volume == 0.8 && audio.Volume == 0.8,
        "MonitorVolume" => t.MonitorVolume == 0.35 && audio.MonitorVolume == 0.35,
        "IsSongMuted" => t.IsSongMuted && audio.SongVolume == 0,
        "IsSongSoloed" => t.IsSongSoloed,
        "IsInputMuted" => t.IsInputMuted && audio.InputVolume == 0,
        "IsInputSoloed" => t.IsInputSoloed,
        "IsMidiMuted" => t.IsMidiMuted && audio.VstInstrumentVolume == 0,
        "IsMidiSoloed" => t.IsMidiSoloed,
        "IsMasterMuted" => t.IsMasterMuted && audio.Volume == 0,
        "Pan" => t.Pan == -0.5 && audio.Pan == -0.5,
        "PitchShift" => t.PitchShift == 2 && audio.PitchShift == 2,
        "MetronomeEnabled" => t.MetronomeEnabled,
        "ClapsEnabled" => t.ClapsEnabled,
        "MidiEnabled" => t.MidiEnabled,
        "MidiInstrument" => t.SelectedMidiInstrumentIndex == 40 && t.SelectedMidiInstrumentName == t.MidiInstruments[40],
        "PlaybackSpeed" => t.PlaybackSpeed == 0.75 && audio.PlaybackSpeed == 0.75,
        "GridDivisor" => t.GridDivisor == 16,
        "TonePreview" => t.SavedTonePreview == (true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
    };

    [Fact]
    public void ADefaultConfig_RestoresTheFirstRunValues()
    {
        var (t, audio) = Boot(new FakeConfigurationService());
        Assert.Equal(1.0, t.SongVolume);
        Assert.Equal(1.0, t.PlaybackSpeed);
        Assert.False(t.MidiEnabled);
        Assert.Equal(27, t.SelectedMidiInstrumentIndex);
        Assert.Equal((false, false), t.SavedTonePreview);
        Assert.Equal(1.0, audio.SongVolume);
    }

    [Fact]
    public void ASliderDrag_WritesTheConfigInMemoryOnly_AndTheRestoreItselfWritesNothing()
    {
        var config = new FakeConfigurationService();
        var (first, _) = Boot(config);
        Assert.Empty(config.Writes);

        first.SongVolume = 0.5;
        first.SongVolume = 0.55;
        // Update() is the coalesced door (its disk write is debounced): one call per
        // snapped step, never a SaveAppConfig per tick on the UI thread.
        Assert.Equal(0.55, config.AppConfig.EditorSession.SongVolume);

        var restarted = Restart(config);
        _ = Boot(restarted);
        Assert.Empty(restarted.Writes);
    }
}
