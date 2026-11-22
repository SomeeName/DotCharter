using Avalonia.Input;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     #588 row 3: Avalonia reports an OS auto-repeat as one more KeyDown with no
///     flag, so holding Space toggled play/pause on every repeat and holding a pen
///     digit entered and removed the same note over and over. A second press of the
///     same chord with no release in between is a repeat: a TOGGLE verb ignores it
///     (still consuming the key), an incremental verb keeps repeating.
/// </summary>
public sealed class KeybindAutoRepeatTests
{
    [Fact]
    public void HoldingAToggle_RunsItOnce_AndTheRepeatIsStillConsumed()
    {
        var (handler, counts) = Handler();

        Assert.True(handler.HandleKeyPress(Key.Space, false, false, false));
        Assert.True(handler.HandleKeyPress(Key.Space, false, false, false));
        Assert.True(handler.HandleKeyPress(Key.Space, false, false, false));

        Assert.Equal(1, counts["PlayPause"]);
    }

    [Fact]
    public void AReleaseBetweenPresses_MakesTheNextPressReal()
    {
        var (handler, counts) = Handler();

        handler.HandleKeyPress(Key.Space, false, false, false);
        handler.HandleKeyRelease(Key.Space);
        handler.HandleKeyPress(Key.Space, false, false, false);

        Assert.Equal(2, counts["PlayPause"]);
    }

    [Fact]
    public void HoldingAnIncrementalVerb_KeepsRepeating()
    {
        var (handler, counts) = Handler();

        for (var i = 0; i < 4; i++) Assert.True(handler.HandleKeyPress(Key.Right, false, false, false));

        Assert.Equal(4, counts["SeekForward"]);
    }

    [Fact]
    public void AnotherKeyOrADeactivation_EndsTheHold()
    {
        var (handler, counts) = Handler();

        handler.HandleKeyPress(Key.Space, false, false, false);
        handler.HandleKeyPress(Key.Right, false, false, false);
        handler.HandleKeyPress(Key.Space, false, false, false);
        handler.ResetHeldKeys();
        handler.HandleKeyPress(Key.Space, false, false, false);

        Assert.Equal(3, counts["PlayPause"]);
    }

    [Theory]
    [InlineData("OpenMixPopover")]
    [InlineData("OpenToolsPopover")]
    [InlineData("OpenTracksPopover")]
    [InlineData("ClearSelection")]
    public void DiscreteVerbsIgnoreRepeatsAndRearmOnReleaseOrDeactivation(string verb)
    {
        var count = 0;
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        handler.Register(new KeyChord(Key.F24), () => count++, verb);
        handler.RefreshBinds();
        handler.HandleKeyPress(Key.F24, false, false, false);
        handler.HandleKeyPress(Key.F24, false, false, false);
        Assert.Equal(1, count);
        handler.HandleKeyRelease(Key.F24);
        handler.HandleKeyPress(Key.F24, false, false, false);
        Assert.Equal(2, count);
        handler.ResetHeldKeys();
        handler.HandleKeyPress(Key.F24, false, false, false);
        Assert.Equal(3, count);
    }

    [Fact]
    public void WindowAndRegisteredTogglePathsShareOneHold()
    {
        var (handler, counts) = Handler();
        var windowActions = 0;
        handler.HandleKeyPress(Key.Space, false, false, false);
        handler.HandleToggleKeyPress(Key.Space, false, false, false, () => windowActions++);
        Assert.Equal(0, windowActions);
        handler.HandleKeyRelease(Key.Space);
        handler.HandleToggleKeyPress(Key.Space, false, false, false, () => windowActions++);
        handler.HandleKeyPress(Key.Space, false, false, false);
        Assert.Equal(1, windowActions);
        Assert.Equal(1, counts["PlayPause"]);
        handler.ResetHeldKeys();
        handler.HandleToggleKeyPress(Key.Space, false, false, false, () => windowActions++);
        Assert.Equal(2, windowActions);
    }

    [Fact]
    public void ADelegatedControlPressAlsoHoldsTheFollowingWindowToggle()
    {
        var (handler, _) = Handler();
        var actions = 0;
        Assert.False(handler.NoteKeyPress(Key.Escape, false, false, false));
        handler.HandleToggleKeyPress(Key.Escape, false, false, false, () => actions++);
        Assert.Equal(0, actions);
        handler.HandleKeyRelease(Key.Escape);
        handler.HandleToggleKeyPress(Key.Escape, false, false, false, () => actions++);
        Assert.Equal(1, actions);
    }

    [Theory]
    [InlineData("PlayPause", true)]
    [InlineData("ToggleMetronome", true)]
    [InlineData("ToggleHopo", true)]
    [InlineData("PenToggleStringBase", true)]
    [InlineData("StringMaskBase", true)]
    [InlineData("BookmarkBase", true)]
    [InlineData("OpenMixPopover", true)]
    [InlineData("OpenToolsPopover", true)]
    [InlineData("OpenTracksPopover", true)]
    [InlineData("ClearSelection", true)]
    [InlineData("SetFretBase", false)]
    [InlineData("SeekForward", false)]
    [InlineData("AnchorFretUp", false)]
    public void ToggleVerbs_AreTheOnesThatFlipState(string label, bool toggle) =>
        Assert.Equal(toggle, ChartKeybindHandler.IsToggleVerb(label));

    private static (ChartKeybindHandler Handler, Dictionary<string, int> Counts) Handler()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["PlayPause"] = 0, ["SeekForward"] = 0 };
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        handler.Register(new KeyChord(Key.Space), () => counts["PlayPause"]++, "PlayPause");
        handler.Register(new KeyChord(Key.Right), () => counts["SeekForward"]++, "SeekForward");
        handler.RefreshBinds();
        return (handler, counts);
    }
}
