namespace DotCharter.Tone.Audio;

/// <summary>
///     Shutdown-safe entry to a gate that a fire-and-forget worker queues on. The
///     gate is disposed at teardown, so a worker queued around shutdown must not
///     throw out of an unobserved task (a spurious crash report on a clean exit).
/// </summary>
internal static class SemaphoreSlimExtensions
{
    /// <summary>Waits for the gate; false when it was already disposed (nothing to release).</summary>
    public static async Task<bool> TryEnterAsync(this SemaphoreSlim gate)
    {
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
