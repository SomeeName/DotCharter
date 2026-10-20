namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     How the <c>StemMixer*Tests</c> wait for a rebuild (#862, after #843/#846).
///     <para>
///         The view model starts a rebuild in its constructor and again on every
///         project change; its stem probe runs on the thread pool, so on a loaded
///         box the build can land at any moment. A wait therefore ends on the
///         STATE the test goes on to read, with no wall-clock budget of its own:
///         a budget that expires silently turns a starved box into a vacuous or
///         half-built read, and one that is asserted turns it into a flaky red.
///         A rebuild that truly never finishes is ended by the test run's
///         cancellation token.
///     </para>
///     <para>
///         A test that attaches a handler must also attach it BEFORE the project
///         arrives: build the view model over an empty <c>ProjectState</c>, attach,
///         then set the project (see <c>StemMixerRebuildSettleTests</c>).
///     </para>
/// </summary>
internal static class StemMixerSettle
{
    /// <summary>
    ///     Waits until <paramref name="until" /> holds, then drains the continuations
    ///     already queued: the strips appear BEFORE the rebuild ends, and the tail
    ///     of <c>FinishRebuild</c> writes state a case may be about to read. With no
    ///     predicate it is only that drain, for a case asserting that NOTHING
    ///     happened.
    /// </summary>
    public static async Task Async(Func<bool>? until = null)
    {
        while (until != null && !until()) await Task.Delay(5, TestContext.Current.CancellationToken);

        for (var i = 0; i < 40; i++) await Task.Yield();
    }
}
