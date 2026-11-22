using Avalonia.Input;
using DotCharter.UI.Views.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     #588 row 2: the Settings page's TUNNEL handler took Ctrl+F (and
///     Ctrl+Shift+F) for its search box before the armed keybind box below it
///     ever saw the key, so neither chord could be captured as a binding. The
///     page yields every chord while a keybind box is armed.
/// </summary>
public sealed class SettingsFindChordTests
{
    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift)]
    public void AnArmedKeybindBox_KeepsTheFindChord(KeyModifiers modifiers) =>
        Assert.False(SettingsShellView.ClaimsFindChord(Key.F, modifiers, keybindCaptureArmed: true));

    [Fact]
    public void Elsewhere_CtrlF_StillFocusesTheSearch()
    {
        Assert.True(SettingsShellView.ClaimsFindChord(Key.F, KeyModifiers.Control, keybindCaptureArmed: false));
        Assert.False(SettingsShellView.ClaimsFindChord(Key.F, KeyModifiers.None, keybindCaptureArmed: false));
        Assert.False(SettingsShellView.ClaimsFindChord(Key.G, KeyModifiers.Control, keybindCaptureArmed: false));
    }


}
