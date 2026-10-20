namespace DotCharter.Tests.Services.Configuration;

/// <summary>The MIDI twin of <see cref="KeyChord" /> (#310) and the binds table on <see cref="KeybindConfig" />.</summary>
public sealed class MidiTriggerTests
{
    [Theory]
    [InlineData("note:10:36", MidiTriggerKind.Note, 9, 36)]
    [InlineData("cc:1:20", MidiTriggerKind.ControlChange, 0, 20)]
    [InlineData("NOTE:16:127", MidiTriggerKind.Note, 15, 127)]
    public void TheStoredForm_RoundTrips(string text, MidiTriggerKind kind, int channel, int number)
    {
        var trigger = MidiTriggerJsonConverter.Parse(text);

        Assert.Equal(new MidiTrigger(kind, channel, number), trigger);
        Assert.Equal(text.ToLowerInvariant(), trigger.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("note:0:60")]
    [InlineData("note:17:60")]
    [InlineData("cc:1:128")]
    [InlineData("pc:1:3")]
    [InlineData("note:1")]
    [InlineData("note:x:60")]
    public void AnythingElse_IsNone(string? text)
    {
        Assert.Equal(MidiTrigger.None, MidiTriggerJsonConverter.Parse(text));
        Assert.False(MidiTrigger.None.IsBound);
        Assert.Equal("None", MidiTrigger.None.ToString());
    }

    [Theory]
    [InlineData(60, "C4")]
    [InlineData(61, "C#4")]
    [InlineData(36, "C2")]
    [InlineData(0, "C-1")]
    [InlineData(127, "G9")]
    public void ANote_IsNamedWithMiddleCAsC4(int note, string name) => Assert.Equal(name, MidiTrigger.NoteName(note));

    [Fact]
    public void TheDisplay_IsLocalized_WithAOneBasedChannel()
    {
        var english = FakeLocalization.English();

        Assert.Equal("C2 · ch 10", MidiTrigger.Note(9, 36).ToDisplayString(english));
        Assert.Equal("CC 20 · ch 1", MidiTrigger.ControlChange(0, 20).ToDisplayString(english));
        Assert.Equal("—", MidiTrigger.None.ToDisplayString(english));
    }

    [Fact]
    public void ATrigger_RunsOneVerb_SoBindingItElsewhere_TakesItAway()
    {
        var table = new KeybindConfig();
        var pad = MidiTrigger.Note(9, 36);
        var announced = new List<string?>();
        table.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        Assert.Null(table.SetMidiTrigger(nameof(KeybindConfig.PlayPause), pad));
        Assert.Equal(nameof(KeybindConfig.PlayPause), table.SetMidiTrigger(nameof(KeybindConfig.Undo), pad));

        Assert.Equal(MidiTrigger.None, table.GetMidiTrigger(nameof(KeybindConfig.PlayPause)));
        Assert.Equal(nameof(KeybindConfig.Undo), table.FindMidiVerb(pad));
        Assert.Equal([nameof(KeybindConfig.MidiBindings), nameof(KeybindConfig.MidiBindings)], announced);
    }

    [Fact]
    public void ADigitFamilyOrAnUnknownVerb_CannotBeBound()
    {
        var table = new KeybindConfig();

        foreach (var verb in KeybindConfig.MidiUnbindableVerbs)
        {
            Assert.False(KeybindConfig.IsMidiBindable(verb));
            Assert.Throws<ArgumentException>(() => table.SetMidiTrigger(verb, MidiTrigger.Note(0, 1)));
        }

        Assert.False(KeybindConfig.IsMidiBindable("NoSuchVerb"));
        Assert.True(KeybindConfig.IsMidiBindable(nameof(KeybindConfig.SeekLeft)));
    }

    [Fact]
    public void ResetToDefault_PutsTheChordsBack_AndLeavesTheMidiBinds_WhichClearHasForItself()
    {
        var table = new KeybindConfig { PlayPause = new KeyChord(Avalonia.Input.Key.F8) };
        table.SetMidiTrigger(nameof(KeybindConfig.PlayPause), MidiTrigger.Note(9, 36));

        table.ResetToDefault();
        Assert.Equal(new KeybindConfig().PlayPause, table.PlayPause);
        Assert.True(table.GetMidiTrigger(nameof(KeybindConfig.PlayPause)).IsBound);

        table.ClearMidiBindings();
        Assert.Empty(table.MidiBindings);
    }
}
