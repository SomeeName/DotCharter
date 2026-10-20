using Avalonia.Controls;
using Avalonia.Threading;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The headless setup race (#192), staged on purpose: another thread takes
///     the UI dispatcher in the window between Avalonia's reset and the
///     session's platform init — the window
///     <see cref="HeadlessUiThreadClaim" /> closes — and the case must still
///     boot, show a window and lay it out.
///     <para>
///         Without the claim this case dies the way the race does, with
///         <c>The calling thread cannot access this object</c> from
///         <c>DefaultRenderLoop.Add</c> / <c>ServerCompositor..ctor</c> /
///         <c>EnsureIsolatedApplication</c>, on every run instead of once in a
///         few hundred cases.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class HeadlessUiThreadClaimUiPerfSmokeTests
{
    [Fact]
    public async Task AThreadThatTakesTheUiDispatcherBeforeSetup_DoesNotFailTheCase()
    {
        Thread? thief = null;
        var thiefOwnedTheUiThread = false;
        var reclaimsBefore = HeadlessUiThreadClaim.Reclaims;
        HeadlessUiThreadClaim.RunBeforeNextClaimForTesting(() =>
        {
            // What a pool thread does when it posts to, or builds an
            // AvaloniaObject for, an app that is between two dispatches.
            thief = new Thread(() => thiefOwnedTheUiThread = Dispatcher.UIThread.CheckAccess())
            {
                Name = "headless-race-thief",
                IsBackground = true
            };
            thief.Start();
            thief.Join();
        });

        var laidOut = await HeadlessSessionFixture.Dispatch(() =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess(), "the session thread is not the UI thread");
            var window = new Window { Width = 320, Height = 200, Content = new TextBlock { Text = "claimed" } };
            window.Show();
            try
            {
                window.UpdateLayout();
                return window.Content is TextBlock { Bounds: { Width: > 0, Height: > 0 } };
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

        // The staging really happened, and really stole the UI thread: without
        // these the case would pass by never racing at all.
        Assert.NotNull(thief);
        Assert.True(thiefOwnedTheUiThread, "the staged thread did not become the UI thread — nothing was raced");
        Assert.Equal(reclaimsBefore + 1, HeadlessUiThreadClaim.Reclaims);
        Assert.True(laidOut, "the window's content measured zero after the reclaim");
    }
}
