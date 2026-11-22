using System.Diagnostics;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The inspector's issue pass runs on a worker and lands through a dispatcher post, so a
///     headless test that reads the list right after <c>RefreshIssues</c> races it. This pumps the
///     headless dispatcher until the pass has landed (bounded: a hang fails, it never stalls the run).
/// </summary>
internal static class HeadlessIssuePass
{
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(30);

    public static void Settle(InspectorIssuesViewModel issues)
    {
        var settled = issues.SettleAsync();
        var clock = Stopwatch.StartNew();
        while (!settled.IsCompleted)
        {
            if (clock.Elapsed > Detector) throw new TimeoutException("the inspector issue pass never landed");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        settled.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }
}
