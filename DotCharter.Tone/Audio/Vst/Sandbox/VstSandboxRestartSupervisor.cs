namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// Drives restart attempts until one sticks or the crash budget runs out. A restart
/// whose replay throws, or whose new child dies during the replay, would be
/// swallowed (the crash handler returns while a restart is in flight) and leave the
/// chain dead with no retry. Here every failed attempt is charged to the
/// <see cref="VstSandboxRestartPolicy"/>, so retries back off and are BOUNDED, and the
/// give-up path runs when the budget is spent.
/// </summary>
internal static class VstSandboxRestartSupervisor
{
    /// <param name="attempt">One restart; returns true when the new child is up and the replay
    /// held, false when the child died meanwhile. May throw.</param>
    /// <param name="policy">Counts each failed attempt and yields the backoff, or null when the budget is spent.</param>
    /// <param name="clock">Elapsed time fed to the policy.</param>
    /// <param name="delay">Waits out the backoff.</param>
    /// <param name="onRetry">Reports a failed attempt (its exception, if any) and the backoff
    /// before the next one.</param>
    /// <param name="onGaveUp">Runs once the policy has no budget left.</param>
    /// <param name="nextBackoff">Optional ownership-checked budget update; null ends this restart cycle.</param>
    /// <returns>True when an attempt succeeded, false when it gave up.</returns>
    public static async Task<bool> RunAsync(
        Func<Task<bool>> attempt, VstSandboxRestartPolicy policy, Func<TimeSpan> clock,
        Func<TimeSpan, Task> delay, Action<Exception?, TimeSpan> onRetry, Func<Task> onGaveUp,
        Func<TimeSpan?>? nextBackoff = null)
    {
        while (true)
        {
            Exception? failure = null;
            try
            {
                if (await attempt().ConfigureAwait(false)) return true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var backoff = nextBackoff != null ? nextBackoff() : policy.OnCrash(clock());
            if (backoff == null)
            {
                await onGaveUp().ConfigureAwait(false);
                return false;
            }
            onRetry(failure, backoff.Value);
            if (backoff.Value > TimeSpan.Zero) await delay(backoff.Value).ConfigureAwait(false);
        }
    }
}
