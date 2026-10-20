using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     Auto-backup only runs for a DIRTY project, and its filesystem probes run
///     off the UI thread.
///     <para>
///         The tick asked only "enabled? due? not running?", so an untouched
///         project paid a whole-project assemble + deep clone on the UI thread
///         every five minutes and wrote one more identical zip — the gate
///         <c>CrashRecoveryService</c> has had since 0.49, one timer over. And
///         <c>Directory.CreateDirectory</c> + the unique-name <c>File.Exists</c>
///         loop sat before the <c>Task.Run</c>, on a folder that is routinely a
///         network share.
///     </para>
/// </summary>
public sealed class AutoBackupGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    /// <summary>Nothing has been backed up yet, so any stamp counts as new.</summary>
    private const long Fresh = AutoBackupGate.NoBackupStamp;

    [Fact]
    public void ACleanProject_NeverRuns_AndRestartsTheInterval()
    {
        // Long overdue, but nothing changed: reset, so the interval starts at
        // the first edit rather than at launch.
        Assert.Equal(AutoBackupGate.Verdict.ResetSchedule,
            AutoBackupGate.Decide(enabled: true, inProgress: false, isDirty: false,
                changeStamp: 7, lastBackupStamp: Fresh,
                nowUtc: T0 + TimeSpan.FromHours(1), lastUtc: T0, interval: Five));
    }

    [Fact]
    public void ADirtyProject_RunsOnceTheIntervalHasPassed_AndNotBefore()
    {
        Assert.Equal(AutoBackupGate.Verdict.Skip,
            AutoBackupGate.Decide(true, false, true, 7, Fresh, T0 + TimeSpan.FromMinutes(4), T0, Five));
        Assert.Equal(AutoBackupGate.Verdict.Run,
            AutoBackupGate.Decide(true, false, true, 7, Fresh, T0 + Five, T0, Five));
    }

    [Fact]
    public void Disabled_ResetsTheSchedule_AndInFlight_JustSkips()
    {
        Assert.Equal(AutoBackupGate.Verdict.ResetSchedule,
            AutoBackupGate.Decide(enabled: false, inProgress: false, isDirty: true,
                changeStamp: 7, lastBackupStamp: Fresh, nowUtc: T0 + Five, lastUtc: T0, interval: Five));
        // A backup already writing must not reset the clock under itself.
        Assert.Equal(AutoBackupGate.Verdict.Skip,
            AutoBackupGate.Decide(true, inProgress: true, isDirty: true,
                changeStamp: 7, lastBackupStamp: Fresh, nowUtc: T0 + Five, lastUtc: T0, interval: Five));
    }

    // ---- dirty is not "changed since the last backup" -------------------------

    /// <summary>
    ///     THE regression, walked on an injected clock: one edit, then the
    ///     project is left alone. The first due tick writes a backup; every tick
    ///     for the next hour must write NOTHING, because the zip on disk already
    ///     holds exactly this content. Gated on dirty alone, each of those ticks
    ///     paid a whole-project assemble + deep clone on the UI thread and left
    ///     one more identical archive behind — and the owner's no-retention
    ///     choice means nothing ever prunes them.
    /// </summary>
    [Fact]
    public void ADirtyButIdleProject_BacksUpOnce_AndThenRests()
    {
        const long edited = 12;

        // The first due tick after the edit: nothing has been written yet.
        Assert.Equal(AutoBackupGate.Verdict.Run,
            AutoBackupGate.Decide(true, false, true, edited, Fresh, T0 + Five, T0, Five));

        // ...and now the backup holds `edited`. Walk the next hour of ticks.
        var last = T0 + Five;
        for (var minute = 10; minute <= 65; minute += 5)
        {
            Assert.Equal(AutoBackupGate.Verdict.ResetSchedule,
                AutoBackupGate.Decide(true, false, true, edited, edited,
                    T0 + TimeSpan.FromMinutes(minute), last, Five));
        }
    }

    /// <summary>
    ///     The control that keeps the guard above from being "never back a
    ///     project up twice": ONE more edit moves the stamp and the next due tick
    ///     runs again. Without this the fix would be a silent way to lose work.
    /// </summary>
    [Fact]
    public void OneMoreEdit_MakesItDueAgain()
    {
        const long backedUp = 12;
        var last = T0 + Five;

        // Still resting on the old stamp...
        Assert.Equal(AutoBackupGate.Verdict.ResetSchedule,
            AutoBackupGate.Decide(true, false, true, backedUp, backedUp, last + Five, last, Five));

        // ...and the moment the node id moves, the interval binds again: not
        // yet, then yes.
        Assert.Equal(AutoBackupGate.Verdict.Skip,
            AutoBackupGate.Decide(true, false, true, backedUp + 1, backedUp,
                last + TimeSpan.FromMinutes(4), last, Five));
        Assert.Equal(AutoBackupGate.Verdict.Run,
            AutoBackupGate.Decide(true, false, true, backedUp + 1, backedUp, last + Five, last, Five));
    }

    /// <summary>
    ///     The unchanged arm RESETS rather than skips, and that is load-bearing:
    ///     the next backup has to land one interval after the next EDIT. Were it
    ///     a Skip the clock would run on through the idle stretch, and the first
    ///     keystroke after an hour would trigger a backup immediately — a hitch
    ///     on the UI thread at exactly the wrong moment.
    /// </summary>
    [Fact]
    public void TheUnchangedArm_RestartsTheInterval_RatherThanLettingItRunOn()
    {
        Assert.Equal(AutoBackupGate.Verdict.ResetSchedule,
            AutoBackupGate.Decide(true, false, true, 12, 12, T0 + TimeSpan.FromHours(1), T0, Five));
    }

    /// <summary>
    ///     The wiring: the tick consults the dirty tracker through the gate, and
    ///     the two filesystem calls sit inside the worker's lambda. The session
    ///     service needs the whole container, so this half is a source lint —
    ///     the <c>ProjectSessionSaveThreadingTests</c> shape.
    /// </summary>
    [Fact]
    public void TheTick_GoesThroughTheGate_AndTheFilesystemProbesRunOnTheWorker()
    {
        var src = Code("DotCharter.Services/Session/ProjectAutoBackup.cs");

        var tick = src.IndexOf("internal void Tick(", StringComparison.Ordinal);
        Assert.True(tick >= 0, "ProjectAutoBackup.Tick has moved — repoint this lint");
        var tickBody = src[tick..src.IndexOf("internal async Task CreateBackupAsync(", tick, StringComparison.Ordinal)];
        Assert.Contains("AutoBackupGate.Decide(", tickBody);
        Assert.Contains("_dirtyTracker.IsDirty", tickBody);
        // ...and the second signal: dirty alone repeats forever.
        Assert.Contains("_dirtyTracker.ChangeStamp", tickBody);
        Assert.Contains("_lastBackupStamp", tickBody);

        var create = src.IndexOf("internal async Task CreateBackupAsync(", StringComparison.Ordinal);
        var body = src[create..src.IndexOf("internal static string UniqueArchivePath(", create, StringComparison.Ordinal)];
        var worker = body.IndexOf("await Task.Run(", StringComparison.Ordinal);
        Assert.True(worker >= 0, "the backup no longer runs its file work on a worker");
        // Both probes AFTER the worker starts, i.e. inside its lambda — nothing
        // filesystem-shaped between the method start and Task.Run.
        var before = body[..worker];
        Assert.DoesNotContain("Directory.CreateDirectory(", before);
        Assert.DoesNotContain("UniqueArchivePath(", before);
        var after = body[worker..];
        Assert.Contains("Directory.CreateDirectory(backupDirectory);", after);
        Assert.Contains("UniqueArchivePath(backupDirectory, baseName, timestamp)", after);
    }

    private static string Code(string relativePath)
    {
        return SourceText.StripComments(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar))));
    }
}
