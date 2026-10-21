namespace DotCharter.Tests.Perf;

/// <summary>
///     Bounded wait for the headless composed scene to catch up with layout (#55). Hit testing reads
///     the last RENDERED scene, and <c>ForceRenderTimerTick</c> only prompts a render, so under load
///     the scene can trail a layout that already ran. The wait ticks a bounded number of times and
///     then fails loudly instead of letting a stale scene read as a product fault.
/// </summary>
internal static class RenderQuiescence
{
    public const int DefaultMaxTicks = 20;

    /// <summary>
    ///     Calls <paramref name="tick" /> (settle layout and force a render) until
    ///     <paramref name="reached" /> holds, at most <paramref name="maxTicks" /> times. Checks once
    ///     before ticking so an already-current scene costs nothing. Throws with
    ///     <paramref name="describe" /> when it never does. Returns the ticks used.
    /// </summary>
    public static int WaitUntil(Func<bool> reached, Action tick, Func<string> describe, int maxTicks = DefaultMaxTicks)
    {
        for (var ticks = 0; ticks <= maxTicks; ticks++)
        {
            if (reached()) return ticks;
            if (ticks < maxTicks) tick();
        }

        throw new InvalidOperationException(
            $"the composed scene did not catch up with layout after {maxTicks} render ticks: {describe()}");
    }
}
