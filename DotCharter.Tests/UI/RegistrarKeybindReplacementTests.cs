using Avalonia.Input;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI;

/// <summary>The Keybinds screen against the REAL registrar and dispatch: a revert restores dispatch, and a save after it uses the replacement table.</summary>
public sealed class RegistrarKeybindReplacementTests
{
    [Theory]
    [InlineData(nameof(KeybindConfig.BookmarkBase))]
    [InlineData(nameof(KeybindConfig.SetFretBase))]
    [InlineData(nameof(KeybindConfig.StringMaskBase))]
    [InlineData(nameof(KeybindConfig.PenToggleStringBase))]
    public void ClearingADigitFamily_RemovesEverySlot(string family)
    {
        var config = new FakeConfigurationService();
        var state = new ProjectState { Keybinds = new KeybindConfig() };
        state.Keybinds.PenToggleStringBase = new KeyChord(Key.D1, KeyModifiers.Control | KeyModifiers.Alt);
        var property = typeof(KeybindConfig).GetProperty(family)!;
        var modifiers = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;
        property.SetValue(state.Keybinds, new KeyChord(Key.D0, modifiers));
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        var registrar = new AppKeybindRegistrar(handler, state, null!, null!, null!, null!, null!,
            config, null!, null!, null!, null!, null!, null!, new MatchPatternStore(config));
        registrar.RegisterAll();
        handler.RefreshBinds();
        Assert.Equal(family, handler.DescribeBinding(new KeyChord(Key.D1, modifiers)));

        property.SetValue(state.Keybinds, KeyChord.None);
        handler.RefreshBinds();

        for (var digit = 0; digit < 10; digit++)
        {
            var key = (Key)((int)Key.D0 + digit);
            Assert.NotEqual(family, handler.DescribeBinding(new KeyChord(key)));
            Assert.NotEqual(family, handler.DescribeBinding(new KeyChord(key, modifiers)));
        }
    }

    [Fact]
    public void RevertRestoresDispatch_AndSubsequentSaveUsesTheReplacementTable()
    {
        var config = new FakeConfigurationService();
        var state = new ProjectState { Keybinds = new KeybindConfig() };
        var original = state.Keybinds.ToggleWaveform;
        config.Keybinds = new KeybindConfig();
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        // Registration only captures these collaborators; this test dispatches
        // the waveform toggle, which depends solely on ProjectState.
        var registrar = new AppKeybindRegistrar(handler, state, null!, null!, null!, null!, null!,
            config, null!, null!, null!, null!, null!, null!, new MatchPatternStore(config));
        registrar.RegisterAll();
        handler.RefreshBinds();
        using var settings = new KeybindsSettingsViewModel(config, state, handler, FakeLocalization.English(), new FakeMidiTriggerInput());
        var custom = new KeyChord(Key.F24, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt);

        state.Keybinds.ToggleWaveform = custom;
        settings.Revert();

        Assert.Null(handler.DescribeBinding(custom));
        Assert.Equal(nameof(KeybindConfig.ToggleWaveform), handler.DescribeBinding(original));

        state.Keybinds.ToggleWaveform = custom;
        settings.Save();

        Assert.NotEqual(nameof(KeybindConfig.ToggleWaveform), handler.DescribeBinding(original));
        Assert.Equal(nameof(KeybindConfig.ToggleWaveform), handler.DescribeBinding(custom));
        var before = state.ShowWaveform;
        Assert.True(handler.HandleKeyPress(custom.Key, true, true, true));
        Assert.Equal(!before, state.ShowWaveform);

        state.Keybinds.ToggleWaveform = KeyChord.None;
        settings.Save();
        Assert.Null(handler.DescribeBinding(custom));
        Assert.False(handler.HandleKeyPress(custom.Key, true, true, true));
    }
}
