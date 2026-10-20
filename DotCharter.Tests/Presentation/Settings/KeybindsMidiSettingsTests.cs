using System.Windows.Input;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Infrastructure.Input;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Keybinds in MIDI mode (#310): the same rows learn a controller
///     press into the same table, count as unsaved changes, save with the same
///     Save, revert with the same Revert — and say plainly when no device can
///     send a press, while staying editable.
/// </summary>
public sealed class KeybindsMidiSettingsTests
{
    private sealed class RecordingDispatch : IKeybindDispatch
    {
        public int Refreshes { get; private set; }

        public void RefreshBinds() => Refreshes++;

        public string? DescribeBinding(KeyChord chord) => null;
    }

    private static readonly MidiTrigger Pad = MidiTrigger.Note(9, 36);

    private static (KeybindsSettingsViewModel Vm, ProjectState State, FakeConfigurationService Cfg, FakeMidiTriggerInput Midi,
        RecordingDispatch Dispatch) Build()
    {
        var cfg = new FakeConfigurationService();
        var state = new ProjectState { Keybinds = cfg.Keybinds };
        var midi = new FakeMidiTriggerInput();
        midi.Connect("Akai MPK mini 3");
        var dispatch = new RecordingDispatch();
        var vm = new KeybindsSettingsViewModel(cfg, state, dispatch, FakeLocalization.English(), midi) { IsMidiMode = true };
        return (vm, state, cfg, midi, dispatch);
    }

    private static KeybindRowViewModel Row(KeybindsSettingsViewModel vm, string property) =>
        vm.Groups.SelectMany(g => g.Rows).Cast<KeybindRowViewModel>().Single(r => r.Property == property);

    [Fact]
    public void LearnThenPress_BindsThatRow_AsOneUnsavedChange()
    {
        var (vm, state, _, midi, _) = Build();
        var row = Row(vm, nameof(KeybindConfig.PlayPause));

        row.LearnMidiCommand.Execute(null);
        Assert.True(row.IsMidiLearning);
        Assert.Equal(FakeLocalization.English()["Keybind_Midi_Listening"], row.MidiText);

        midi.Press(Pad);

        Assert.False(row.IsMidiLearning);
        Assert.Equal(Pad, state.Keybinds.GetMidiTrigger(nameof(KeybindConfig.PlayPause)));
        Assert.True(row.IsMidiBound);
        Assert.Equal("C2 · ch 10", row.MidiText);
        Assert.Equal(["midi.PlayPause"], vm.UnsavedSettings);
    }

    [Fact]
    public void APress_WithNoRowLearning_OrInKeyboardMode_BindsNothing()
    {
        var (vm, state, _, midi, _) = Build();
        var row = Row(vm, nameof(KeybindConfig.PlayPause));

        midi.Press(Pad);
        Assert.Empty(state.Keybinds.MidiBindings);

        row.LearnMidiCommand.Execute(null);
        vm.IsKeyboardMode = true;
        Assert.False(row.IsMidiLearning);
        midi.Press(Pad);
        Assert.Empty(state.Keybinds.MidiBindings);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void LearningAgainOnTheSameRow_Cancels_AndAnotherRow_TakesOver()
    {
        var (vm, _, _, _, _) = Build();
        var play = Row(vm, nameof(KeybindConfig.PlayPause));
        var undo = Row(vm, nameof(KeybindConfig.Undo));

        play.LearnMidiCommand.Execute(null);
        undo.LearnMidiCommand.Execute(null);
        Assert.False(play.IsMidiLearning);
        Assert.True(undo.IsMidiLearning);

        undo.LearnMidiCommand.Execute(null);
        Assert.False(undo.IsMidiLearning);
    }

    [Fact]
    public void LearningATriggerAnotherVerbHolds_MovesIt_AndBothRowsSaySo()
    {
        var (vm, state, _, midi, _) = Build();
        var play = Row(vm, nameof(KeybindConfig.PlayPause));
        var undo = Row(vm, nameof(KeybindConfig.Undo));
        state.Keybinds.SetMidiTrigger(nameof(KeybindConfig.PlayPause), Pad);
        var playAnnounced = new List<string?>();
        play.PropertyChanged += (_, e) => playAnnounced.Add(e.PropertyName);

        undo.LearnMidiCommand.Execute(null);
        midi.Press(Pad);

        Assert.False(play.IsMidiBound);
        Assert.True(undo.IsMidiBound);
        Assert.Contains(nameof(IKeybindRow.MidiText), playAnnounced);
    }

    [Fact]
    public void ADigitFamilyRow_CannotLearn_AndSaysWhy()
    {
        var (vm, _, _, _, _) = Build();
        var bookmark = Row(vm, nameof(KeybindConfig.BookmarkBase));

        Assert.False(bookmark.IsMidiBindable);
        Assert.False(bookmark.LearnMidiCommand.CanExecute(null));
        Assert.Equal(FakeLocalization.English()["Keybind_Midi_KeyboardOnly"], bookmark.MidiText);
        Assert.DoesNotContain(vm.UnsavedSettings, k => k == "midi.BookmarkBase");
    }

    [Fact]
    public void Save_PersistsTheMidiBinds_InTheSameFile_AndRebuildsTheDispatch()
    {
        var (vm, state, cfg, midi, dispatch) = Build();
        Row(vm, nameof(KeybindConfig.Undo)).LearnMidiCommand.Execute(null);
        midi.Press(MidiTrigger.ControlChange(0, 20));

        vm.Save();

        Assert.Same(state.Keybinds, cfg.Keybinds);
        Assert.Equal(MidiTrigger.ControlChange(0, 20), cfg.Keybinds.GetMidiTrigger(nameof(KeybindConfig.Undo)));
        Assert.Equal(1, dispatch.Refreshes);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void Revert_DropsAnUnsavedBind_AndStopsLearning()
    {
        var (vm, state, cfg, midi, _) = Build();
        cfg.Keybinds = new KeybindConfig(); // what is on disk: no binds
        var play = Row(vm, nameof(KeybindConfig.PlayPause));
        play.LearnMidiCommand.Execute(null);
        midi.Press(Pad);
        Row(vm, nameof(KeybindConfig.Undo)).LearnMidiCommand.Execute(null);

        vm.Revert();

        Assert.Empty(state.Keybinds.MidiBindings);
        Assert.False(play.IsMidiBound);
        Assert.False(Row(vm, nameof(KeybindConfig.Undo)).IsMidiLearning);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void ClearAll_UnbindsEveryTrigger_AndTheKeyboardResetLeavesThemAlone()
    {
        var (vm, state, _, _, _) = Build();
        state.Keybinds.SetMidiTrigger(nameof(KeybindConfig.PlayPause), Pad);
        state.Keybinds.SetMidiTrigger(nameof(KeybindConfig.Undo), MidiTrigger.ControlChange(0, 20));
        vm.Rebaseline();

        vm.ResetToDefaultsCommand.Execute(null);
        Assert.Equal(2, state.Keybinds.MidiBindings.Count);

        vm.ClearMidiBindingsCommand.Execute(null);
        Assert.Empty(state.Keybinds.MidiBindings);
        Assert.Equal(["midi.PlayPause", "midi.Undo"], vm.UnsavedSettings.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ARowClear_UnbindsJustThatRow()
    {
        var (vm, state, _, _, _) = Build();
        state.Keybinds.SetMidiTrigger(nameof(KeybindConfig.PlayPause), Pad);
        var play = Row(vm, nameof(KeybindConfig.PlayPause));

        play.ClearMidiCommand.Execute(null);

        Assert.False(play.IsMidiBound);
        Assert.Equal("—", play.MidiText);
    }

    [Fact]
    public void TheMode_IsOneFlag_EveryRowFollows_AndItIsNotASetting()
    {
        var (vm, _, _, _, _) = Build();
        var rows = vm.Groups.SelectMany(g => g.Rows).ToList();
        Assert.All(rows, r => Assert.True(r.ShowsMidi));

        vm.IsKeyboardMode = true;

        Assert.False(vm.IsMidiMode);
        Assert.All(rows, r => Assert.False(r.ShowsMidi));
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void TheStatus_SaysWhyNoPressCanArrive_AndFollowsTheDevice()
    {
        var cfg = new FakeConfigurationService();
        var midi = new FakeMidiTriggerInput();
        var english = FakeLocalization.English();
        using var vm = new KeybindsSettingsViewModel(cfg, new ProjectState { Keybinds = cfg.Keybinds },
            new RecordingDispatch(), english, midi);
        var announced = new List<string?>();
        vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        Assert.False(vm.IsMidiInputLive);
        Assert.Equal(english["Settings_MidiInput_NoDevice"], vm.MidiInputStatus);

        midi.DeviceName = "Akai MPK mini 3";
        Assert.Equal(english.Format("Settings_MidiInput_DeviceWaiting", "Akai MPK mini 3"), vm.MidiInputStatus);
        midi.Error = "in use";
        Assert.Equal(english.Format("Settings_MidiInput_DeviceError", "Akai MPK mini 3", "in use"), vm.MidiInputStatus);

        midi.Connect("Akai MPK mini 3");
        Assert.True(vm.IsMidiInputLive);
        Assert.Equal(english.Format("Settings_MidiInput_Live", "Akai MPK mini 3"), vm.MidiInputStatus);
        Assert.Contains(nameof(IKeybindsSettingsScreen.IsMidiInputLive), announced);

        midi.IsAvailable = false;
        Assert.False(vm.IsMidiInputLive);
        Assert.Equal(english["Settings_MidiInput_NoEngine"], vm.MidiInputStatus);
    }

    [Fact]
    public void WithNoDevice_TheRowsStayEditable()
    {
        var cfg = new FakeConfigurationService();
        cfg.Keybinds.SetMidiTrigger(nameof(KeybindConfig.PlayPause), Pad);
        var state = new ProjectState { Keybinds = cfg.Keybinds };
        var midi = new FakeMidiTriggerInput();
        using var vm = new KeybindsSettingsViewModel(cfg, state, new RecordingDispatch(), FakeLocalization.English(), midi);
        vm.IsMidiMode = true;
        var play = Row(vm, nameof(KeybindConfig.PlayPause));

        Assert.True(play.LearnMidiCommand.CanExecute(null));
        play.ClearMidiCommand.Execute(null);

        Assert.Empty(state.Keybinds.MidiBindings);
    }

    [Fact]
    public void SearchingForTheMode_KeepsEveryRow()
    {
        var (vm, _, _, _, _) = Build();

        vm.ApplySearch("midi");

        Assert.All(vm.Groups, g => Assert.True(g.IsVisible));
        Assert.All(vm.Groups.SelectMany(g => g.Rows), r => Assert.True(r.IsVisible));
    }

    [Fact]
    public void InMidiMode_ItStillHonoursTheContract_AndLetsGoOfTheInputOnDispose()
    {
        var (vm, _, _, midi, _) = Build();
        Assert.Empty(ContractConformanceTests.Violations(typeof(IKeybindsSettingsScreen), vm));
        Assert.IsAssignableFrom<ICommand>(vm.ClearMidiBindingsCommand);
        Assert.Equal(1, midi.PressedSubscribers);

        vm.Dispose();

        Assert.Equal(0, midi.PressedSubscribers);
    }

    [Fact]
    public void EveryMidiBindableRow_IsTracked()
    {
        var (vm, state, _, _, _) = Build();
        var bindable = vm.Groups.SelectMany(g => g.Rows).Cast<KeybindRowViewModel>().Where(r => r.IsMidiBindable).ToList();
        Assert.True(bindable.Count > 100, $"only {bindable.Count} rows can take a MIDI bind");

        var number = 0;
        foreach (var row in bindable)
        {
            state.Keybinds.SetMidiTrigger(row.Property, MidiTrigger.Note(number / 128, number % 128));
            number++;
        }

        Assert.Equal(bindable.Count, vm.UnsavedSettings.Count);
        Assert.All(vm.UnsavedSettings, k => Assert.StartsWith("midi.", k, StringComparison.Ordinal));
    }
}
