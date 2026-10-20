namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     `IsBeatMapLocked` is DOCUMENT state, not a view preference:
///     <c>RscpWriter</c> emits <c>dcBeatMapLocked</c> and
///     <c>RscpHeaderParser</c> reads it back. Toggling it therefore changes the
///     project relative to the file on disk.
///     <para>
///         The toggle lived in <c>MainWindowViewModel.ToggleBeatMapLock</c> and
///         recorded nothing — no history node (it cannot have one; the menu's
///         checkmark reads the flag directly and undo would not refresh it) and
///         no dirty mark. So locking a chart and closing it raised no
///         unsaved-changes prompt and the lock was silently dropped: the next
///         session's tempo-map verbs were free to move a grid the charter had
///         deliberately pinned. It is exactly the case
///         <c>IProjectSessionService.MarkProjectDirty</c> documents — "a change
///         the history cannot represent".
///     </para>
/// </summary>
public sealed class BeatMapLockDirtyTests
{
    [Fact]
    public void ToggleBeatMapLock_FlipsTheFlagAndReportsIt()
    {
        var h = new ChartPowerToolsTests.Harness();

        Assert.True(h.Maintenance.ToggleBeatMapLock());
        Assert.True(h.State.ActiveProject!.IsBeatMapLocked);

        Assert.False(h.Maintenance.ToggleBeatMapLock());
        Assert.False(h.State.ActiveProject!.IsBeatMapLocked);
    }

    [Fact]
    public void ToggleBeatMapLock_MarksTheProjectDirty()
    {
        var h = new ChartPowerToolsTests.Harness();

        h.Maintenance.ToggleBeatMapLock();

        Assert.Equal(1, h.Session.DirtyMarks);
    }

    [Fact]
    public void ToggleBeatMapLock_UnlockingAlsoMarksTheProjectDirty()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.ActiveProject!.IsBeatMapLocked = true;

        h.Maintenance.ToggleBeatMapLock();

        Assert.Equal(1, h.Session.DirtyMarks);
    }

    /// <summary>No project, nothing to lock and nothing to dirty.</summary>
    [Fact]
    public void ToggleBeatMapLock_WithNoProject_ChangesNothing()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.ActiveProject = null;

        Assert.False(h.Maintenance.ToggleBeatMapLock());
        Assert.Equal(0, h.Session.DirtyMarks);
    }
}
