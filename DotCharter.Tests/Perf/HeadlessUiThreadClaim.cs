using System.Reflection;
using Avalonia;
using Avalonia.Threading;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Makes the headless session's dispatch thread the UI thread of every
///     isolated application, before anything is built on it. This is the fix
///     for the headless suite's SETUP race (#192): a case dying with
///     <c>The calling thread cannot access this object because a different
///     thread owns it</c> from <c>DefaultRenderLoop.Add</c> /
///     <c>ServerCompositor..ctor</c> / <c>EnsureIsolatedApplication</c>.
///     <para>
///         <b>The cause</b> (Avalonia 12.1.3). <c>Dispatcher.UIThread</c> is the
///         static <c>s_uiThread</c>, and it is "whichever dispatcher was created
///         FIRST": the constructor does <c>s_uiThread ??= this</c>, on whatever
///         thread it runs. <c>HeadlessUnitTestSession.EnsureIsolatedApplication</c>
///         calls <c>Dispatcher.ResetBeforeUnitTests()</c>, which sets
///         <c>s_uiThread</c> to null and forgets every thread's dispatcher, and
///         only then <c>AppBuilder.SetupUnsafe()</c>, whose first dispatcher
///         access on the session thread is deep inside platform init (the
///         headless <c>Compositor</c>'s render loop). In between, the runtime,
///         text-shaping and rendering subsystems initialise — and ANY other
///         thread that reads <c>Dispatcher.UIThread</c> or
///         <c>Dispatcher.CurrentDispatcher</c> (a <c>Post</c> from a pool thread,
///         an <c>AvaloniaObject</c> constructed off-thread — its constructor reads
///         <c>CurrentDispatcher</c>) creates a dispatcher bound to ITSELF and
///         becomes the UI thread. The compositor then asks the UI dispatcher
///         whether it is on the UI thread, and the answer is no.
///     </para>
///     <para>
///         <b>The fix.</b> <see cref="InstallInto" /> runs <see cref="Claim" />
///         as the FIRST platform initializer (only options bindings precede it).
///         It takes Avalonia's own dispatcher lock — the one every dispatcher
///         constructor and every slow <c>UIThread</c> read takes — and inside it
///         makes the calling thread's dispatcher the UI one; if another thread
///         got there first, it repeats Avalonia's own reset and then claims.
///         Nothing has been built on the stolen dispatcher at that point, so the
///         reset loses nothing Avalonia's own reset would not have. Once claimed,
///         <c>??=</c> means no other thread can take it until the next reset.
///         Every reclaim is written to <see cref="LogPath" /> with the thief
///         thread's identity, so a product thread that touches the dispatcher
///         between apps is named, not hidden.
///     </para>
///     <para>
///         The three reflection bindings are asserted by
///         <c>Infrastructure/HeadlessDrainLintTests</c>; a null one turns the
///         claim off silently and restores the race.
///         <c>Perf/HeadlessUiThreadClaimUiPerfSmokeTests</c> steals the UI thread
///         on purpose before setup and asserts the case still runs.
///     </para>
/// </summary>
internal static class HeadlessUiThreadClaim
{
    /// <summary><c>Dispatcher.s_globalLock</c>: taken by every dispatcher constructor and every slow <c>UIThread</c> read.</summary>
    internal static readonly FieldInfo? GlobalLockField =
        typeof(Dispatcher).GetField("s_globalLock", BindingFlags.Static | BindingFlags.NonPublic);

    /// <summary><c>Dispatcher.s_uiThread</c>: the dispatcher <c>Dispatcher.UIThread</c> returns.</summary>
    internal static readonly FieldInfo? UiThreadField =
        typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic);

    /// <summary><c>Dispatcher.ResetBeforeUnitTests()</c>: the reset <c>EnsureIsolatedApplication</c> itself performs.</summary>
    internal static readonly MethodInfo? ResetBeforeUnitTestsMethod = typeof(Dispatcher).GetMethod(
        "ResetBeforeUnitTests", BindingFlags.Static | BindingFlags.NonPublic, binder: null, types: Type.EmptyTypes, modifiers: null);

    private static readonly FieldInfo? QueueField =
        typeof(Dispatcher).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic);

    private static int _reclaims;
    private static Action? _beforeNextClaim;

    /// <summary>How many times this process found the UI thread taken by another thread at setup and took it back.</summary>
    internal static int Reclaims => Volatile.Read(ref _reclaims);

    /// <summary>Where each reclaim is written (one line each), next to the drain log.</summary>
    internal static string LogPath => Path.Combine(AppContext.BaseDirectory, "headless-uithread-claims.log");

    /// <summary>
    ///     Test seam: runs <paramref name="action" /> on the session thread at the
    ///     NEXT setup, after Avalonia's reset and before the claim — exactly where
    ///     the race lands. One-shot.
    /// </summary>
    internal static void RunBeforeNextClaimForTesting(Action action) => Volatile.Write(ref _beforeNextClaim, action);

    /// <summary>Puts <see cref="Claim" /> in front of the builder's runtime-platform initializer.</summary>
    internal static AppBuilder InstallInto(AppBuilder builder)
    {
        var runtime = builder.RuntimePlatformServicesInitializer;
        return builder.UseRuntimePlatformSubsystem(() =>
        {
            Claim();
            runtime?.Invoke();
        }, builder.RuntimePlatformServicesName ?? "");
    }

    /// <summary>Makes the calling thread's dispatcher <c>Dispatcher.UIThread</c>. See the type's remarks.</summary>
    internal static void Claim()
    {
        Interlocked.Exchange(ref _beforeNextClaim, null)?.Invoke();
        if (GlobalLockField?.GetValue(null) is not { } globalLock || UiThreadField is null || ResetBeforeUnitTestsMethod is null)
            return;

        lock (globalLock)
        {
            if (UiThreadField.GetValue(null) is Dispatcher owner)
            {
                if (owner.CheckAccess()) return;
                Report(owner);
                ResetBeforeUnitTestsMethod.Invoke(null, null);
            }

            // s_uiThread is null here and this thread has no dispatcher, so this
            // creates one and makes it the UI dispatcher — under the lock every
            // other dispatcher constructor needs.
            Dispatcher.UIThread.VerifyAccess();
        }
    }

    private static void Report(Dispatcher owner)
    {
        var count = Interlocked.Increment(ref _reclaims);
        try
        {
            var thief = owner.Thread;
            var line = $"[headless] UI thread reclaim #{count} before " +
                       $"'{TestContext.Current.Test?.TestDisplayName ?? "(no case)"}': taken by thread " +
                       $"{thief.ManagedThreadId} '{thief.Name ?? "(unnamed)"}' ({ThreadState(thief)}); " +
                       $"its queue head: {QueueHead(owner)}";
            Console.WriteLine(line);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // Diagnostics only: the claim must go ahead whatever this hits.
        }
    }

    // A thread that has already exited throws on most of its state.
    private static string ThreadState(Thread thread) =>
        thread.IsAlive ? $"alive, pool={thread.IsThreadPoolThread}" : "exited";

    // The first job the thief queued on the dispatcher it created, if any — a
    // Post names the code that posted it. Diagnostics only: any failure here
    // must not stop the claim.
    private static string QueueHead(Dispatcher owner)
    {
        try
        {
            var queue = QueueField?.GetValue(owner);
            var head = queue?.GetType().GetMethod("Peek", Type.EmptyTypes)?.Invoke(queue, null);
            if (head is null) return "(empty)";
            var display = head.GetType().GetProperty("DebugDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(head);
            return display?.ToString() ?? head.ToString() ?? "(unknown)";
        }
        catch (Exception e)
        {
            return $"(unreadable: {e.GetType().Name})";
        }
    }
}
