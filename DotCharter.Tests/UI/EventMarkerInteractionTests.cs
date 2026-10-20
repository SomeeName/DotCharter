namespace DotCharter.Tests.UI;

/// <summary>
///     Two marker-interaction faults the owner's 2026-07-27 session hit
///     repeatedly. Both are source lints — the interaction tool needs a live
///     pointer and the clipboard coordinator needs a track context — and both
///     guard a BEHAVIOUR that is invisible until it costs you work.
/// </summary>
public sealed class EventMarkerInteractionTests
{
    /// <summary>
    ///     Right-click on an EMPTY event row opens the dialog. Right-click on a
    ///     marker used to DELETE it — three markers ('interlude', 'verse',
    ///     'chorus') were lost that way in one session, each re-authored by hand,
    ///     with nothing on screen saying what had happened. Deleting must be the
    ///     deliberate gesture, not the one that shares a button with "edit".
    /// </summary>
    [Fact]
    public void RightClickOnAMarkerOpensTheDialogAndOnlyShiftDeletes()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Input/Tools/EventInteractionTool.cs"));
        var branch = Between(source, "else if (context.Properties.IsRightButtonPressed)", "private void HandleTonePress");

        Assert.Contains("vm.ShellDialogs.OpenEvent(", branch, StringComparison.Ordinal);
        Assert.Contains("Modifiers.HasFlag(KeyModifiers.Shift)", branch, StringComparison.Ordinal);

        // The delete must sit INSIDE the Shift branch — a bare RemoveEventPoints
        // ahead of the guard would restore the old behaviour while the test kept
        // passing on the presence of the two strings above.
        var shiftAt = branch.IndexOf("Modifiers.HasFlag(KeyModifiers.Shift)", StringComparison.Ordinal);
        var removeAt = branch.IndexOf("RemoveEventPoints", StringComparison.Ordinal);
        Assert.True(removeAt > shiftAt,
            "RemoveEventPoints runs before the Shift guard — right-click still deletes a marker outright.");
    }

    /// <summary>
    ///     Copying in the event rows copies EVENTS. The same session selected a
    ///     note, then a section marker, then copied; the paste dropped the stray
    ///     note and merged it into a chord at the destination, undone one second
    ///     later.
    /// </summary>
    [Fact]
    public void CopyingInTheEventRowsDoesNotCarryHighwayContent()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.Services/Editing/EditingClipboardCoordinator.cs"));
        var copy = Between(source, "public void Copy(TrackEditorContext track", "public (List<GuitarNote>");

        Assert.Contains("EditorFocusArea.TimelineEvents", copy, StringComparison.Ordinal);
        Assert.Contains("notesToCopy.Clear()", copy, StringComparison.Ordinal);
        Assert.Contains("chordsToCopy.Clear()", copy, StringComparison.Ordinal);
        Assert.Contains("handShapesToCopy.Clear()", copy, StringComparison.Ordinal);
        Assert.Contains("anchorsToCopy.Clear()", copy, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' is gone — this guard no longer guards anything.");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' is gone — this guard no longer guards anything.");
        return source.Substring(from, to - from);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
