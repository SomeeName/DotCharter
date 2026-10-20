namespace DotCharter.Services.Editing;

/// <summary>
///     Whether an auto-backup tick does anything — the pure half of
///     <c>ProjectAutoBackup.Tick</c>.
///     <para>
///         <b>The dirty gate is the point.</b> Auto-backup defaults ON at a
///         five-minute interval, and its tick asked only "enabled? due? not
///         already running?" — never whether anything had CHANGED. So an
///         untouched project (opened to play it, or saved a minute ago) paid a
///         whole-project <c>AssembleProject</c> + deep clone on the UI thread
///         every five minutes — MAP.md calls that clone "the ONLY heavy UI-thread
///         step of a save", here it was an unprompted hitch while charting — and
///         then wrote one more identical zip into the project folder, forever.
///         <c>CrashRecoveryService.RunTickAsync</c> has exactly this gate
///         and documents why; this is the same rule one timer over.
///     </para>
///     <para>
///         A CLEAN project resets the schedule rather than merely skipping, so the
///         interval restarts at the first edit — a backup lands five minutes into
///         a session of changes, not five minutes after launch regardless.
///     </para>
///     <para>
///         <b>Dirty is not "changed since the last backup", and gating on it
///         alone was still one identical zip every interval, forever.</b> A
///         project stays dirty for as long as it is unsaved, so an unsaved chart
///         left open overnight wrote a backup every five minutes of byte-for-byte
///         the same content — and the owner's no-retention choice means nothing
///         ever prunes them. The second signal is
///         <c>ProjectDirtyTracker.ChangeStamp</c>: the undo history's node id IS
///         the change stamp, so "the stamp has not moved since the last backup"
///         is exactly "nothing has changed since it". An idle dirty project backs
///         up ONCE and then rests until the next edit.
///     </para>
///     <para>
///         The unchanged case RESETS rather than skips, for the same reason the
///         clean one does: the next backup then lands one interval after the next
///         EDIT, not immediately after it because the clock ran on meanwhile.
///     </para>
/// </summary>
public static class AutoBackupGate
{
    public enum Verdict
    {
        /// <summary>Nothing this tick; the schedule is untouched (not due, or a backup is in flight).</summary>
        Skip,

        /// <summary>Nothing to back up (disabled, or the project is clean): restart the interval from now.</summary>
        ResetSchedule,

        /// <summary>Write one.</summary>
        Run
    }

    /// <summary>Stamp a holder carries before it has ever written a backup for
    /// the current project. No real <c>ChangeStamp</c> equals it, so the first
    /// due tick of a dirty project always runs.</summary>
    public const long NoBackupStamp = long.MinValue;

    /// <summary>
    ///     <c>changeStamp</c> is <c>ProjectDirtyTracker.ChangeStamp</c> right
    ///     now; <c>lastBackupStamp</c> is the stamp captured when the last
    ///     backup of THIS project started, or <see cref="NoBackupStamp" /> when
    ///     there has not been one.
    /// </summary>
    public static Verdict Decide(bool enabled, bool inProgress, bool isDirty,
        long changeStamp, long lastBackupStamp,
        DateTimeOffset nowUtc, DateTimeOffset lastUtc, TimeSpan interval)
    {
        if (inProgress) return Verdict.Skip;
        if (!enabled || !isDirty) return Verdict.ResetSchedule;
        // Dirty, but not one edit since the zip that already holds this exact
        // content.
        if (changeStamp == lastBackupStamp) return Verdict.ResetSchedule;
        return nowUtc - lastUtc < interval ? Verdict.Skip : Verdict.Run;
    }
}
