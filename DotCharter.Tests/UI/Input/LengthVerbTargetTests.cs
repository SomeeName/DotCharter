using static DotCharter.UI.Input.TimelineWheelAdjuster;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     Increase / Decrease Length (keybind, Edit menu, command palette) lengthens whatever is
///     SELECTED, in the wheel's order: sounds, then hand shapes, then the lyric.
///     <para>
///         Until 2026-09-25 every one of them called <c>AdjustSelectedNotesSustain</c> directly, so
///         with a hand shape or a lyric selected the key did nothing while the wheel lengthened
///         both — the owner went for "lengthen the lyric" and it lengthened a note (the stale
///         selection half of that is <c>LyricClickSelectionTests</c>).
///     </para>
/// </summary>
public sealed class LengthVerbTargetTests
{
    [Theory]
    [InlineData(true, true, true, LengthTarget.Sounds)]
    [InlineData(true, false, false, LengthTarget.Sounds)]
    [InlineData(false, true, true, LengthTarget.HandShapes)]
    [InlineData(false, true, false, LengthTarget.HandShapes)]
    [InlineData(false, false, true, LengthTarget.Lyric)]
    [InlineData(false, false, false, LengthTarget.None)]
    public void TheVerb_LengthensTheSelection_InTheWheelsOrder(bool sounds, bool shapes, bool lyric,
        LengthTarget expected) => Assert.Equal(expected, LengthTargetFor(sounds, shapes, lyric));

    /// <summary>A key press has no pointer: the selection-only path never falls back to HOVER.</summary>
    [Fact]
    public void TheSelectionPath_TurnsOffTheHoverFallbacks()
    {
        var adjuster = Read("DotCharter.UI", "Input", "TimelineWheelAdjuster.cs");
        var body = adjuster[adjuster.IndexOf("public WheelAdjustmentResult ApplyToSelection", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("public WheelAdjustmentResult TryAdjustSelectedSoundSustain", StringComparison.Ordinal)];

        Assert.Contains("TryAdjustHandShapeLength(vm, deltaSteps, includeHovered: false)", body);
        Assert.Contains("TryAdjustLyricSustain(vm, deltaSteps, includeHovered: false)", body);
        Assert.DoesNotContain("Hovered", body.Replace("includeHovered", ""));
    }

    /// <summary>The key, the menu and the palette share ONE path: the shell's commands, which go
    /// through <c>ApplyToSelection</c>. The keybind used to call the note-only service itself.</summary>
    [Fact]
    public void EveryDoorGoesThroughApplyToSelection()
    {
        var registrar = Read("DotCharter.UI", "Input", "AppKeybindRegistrar.cs");
        Assert.Contains("Keybinds.IncreaseLength,\n            () => _shell.IncreaseLengthCommand.Execute(null)", registrar);
        Assert.Contains("Keybinds.DecreaseLength,\n            () => _shell.DecreaseLengthCommand.Execute(null)", registrar);

        var shell = Read("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Editing.cs");
        var verb = shell[shell.IndexOf("private void IncreaseLength()", StringComparison.Ordinal)..];
        verb = verb[..verb.IndexOf("private void ToggleBookmark", StringComparison.Ordinal)];
        Assert.Contains(".ApplyToSelection(this, deltaSteps)", verb);
        Assert.DoesNotContain("AdjustSelectedNotesSustain", verb);
    }

    private static string Read(params string[] parts) =>
        UiSourceTree.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts])).Replace("\r\n", "\n");
}
