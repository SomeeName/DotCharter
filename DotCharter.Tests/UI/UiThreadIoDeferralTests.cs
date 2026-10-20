using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Two commands used to do their file I/O on the UI thread, and both froze a
///     visible surface for as long as the disk took.
///     <list type="bullet">
///         <item>
///             <c>ExportMenuViewModel.CaptureHighwayScreenshot</c> PNG-encoded a
///             full-window bitmap and wrote it inline — deflate plus a file write
///             between the keypress and the OSD confirmation.
///         </item>
///         <item>
///             <c>PlayModeViewModel.AddReceivedSongToLibrary</c> <c>File.Copy</c>-d a
///             LAN-received psarc into the user's library folder, which is
///             routinely a NETWORK SHARE — tens of megabytes with the results
///             screen and the lobby frozen behind it.
///         </item>
///     </list>
///     <para>
///         Source lints plus reflection, for the same reason as
///         <c>StartupWorkDeferralTests</c>: both view models need the whole app
///         booted, so their behavioural tests are <c>*UiPerfSmokeTests</c> outside
///         the default gate. Never a wall-clock assertion — these assert the
///         STRUCTURE that makes the freeze impossible (what is inside the
///         <c>Task.Run</c> and what deliberately is not), and the reflection half
///         catches the rename that would silently unbind the XAML.
///     </para>
/// </summary>
public sealed class UiThreadIoDeferralTests
{
    /// <summary>
    ///     The split that makes the screenshot correct AND non-blocking: the
    ///     <c>PrintWindow</c> read-back of the native swapchain must stay on the
    ///     UI thread (it is the only way to read the swapchain and it is not
    ///     thread-safe), while the encode + write must not. Both
    ///     <c>SetMessage</c> calls stay outside the worker — the OSD channel is
    ///     UI-thread state.
    /// </summary>
    [Fact]
    public void TheScreenshotEncodesAndWritesOffTheUiThread_ButCapturesOnIt()
    {
        var method = MethodBody(
            Source("DotCharter.UI", "ViewModels", "Shell", "ExportMenuViewModel.cs"),
            "private async Task CaptureHighwayScreenshotAsync()");
        Assert.NotNull(method);

        var worker = InnermostTaskRunBody(method);
        Assert.NotNull(worker);

        // The capture is BEFORE the worker; the encode + write are inside it.
        var captureIndex = method.IndexOf("HighwayScreenshotProvider?.Invoke()", StringComparison.Ordinal);
        var taskRunIndex = method.IndexOf("Task.Run(", StringComparison.Ordinal);
        Assert.True(captureIndex >= 0, "The PrintWindow capture must still happen.");
        Assert.True(captureIndex < taskRunIndex,
            "HighwayScreenshotProvider reads back the native swapchain; it must stay on the UI thread.");

        Assert.Contains("shot.Save(", worker, StringComparison.Ordinal);
        // The file is created through IExportTargetWriter (which also makes the
        // screenshots folder), still inside the worker.
        Assert.Contains("_exportFiles.Create(", worker, StringComparison.Ordinal);

        // …and all three status reports (no capture, saved, failed) stay on the
        // dispatcher.
        Assert.DoesNotContain("SetMessage", worker, StringComparison.Ordinal);
        Assert.Equal(3, Occurrences(method, "_projectState.SetMessage("));
    }

    /// <summary>
    ///     Renaming a method to <c>…Async</c> renames the generated command
    ///     unless the toolkit's suffix-stripping does its job — and a command
    ///     bound by name from XAML fails SILENTLY, as a dead button. Three call
    ///     sites name the screenshot command (the keybind registrar, the command
    ///     palette, the View menu).
    /// </summary>
    [Theory]
    [InlineData(typeof(ExportMenuViewModel), "CaptureHighwayScreenshotCommand")]
    public void TheGeneratedCommandKeepsItsBoundName(Type viewModel, string commandName)
    {
        var property = viewModel.GetProperty(commandName);
        Assert.True(property != null,
            $"{viewModel.Name}.{commandName} is bound by name from XAML/C#; renaming it silently kills the button.");
        Assert.True(typeof(IAsyncRelayCommand).IsAssignableFrom(property!.PropertyType),
            $"{commandName} must be async now — a synchronous command cannot await the worker.");
    }

    /// <summary>
    ///     The sheet PDF export was the straggler: both sibling exporters
    ///     (Guitar Pro, EOF) snapshot the chart and serialize it on a worker,
    ///     while this one handed <c>SheetPdfExporter</c> the LIVE project,
    ///     arrangement and beat collection and let <c>AlphaTexExporter.Export</c>
    ///     — a full walk of the level — run inline on the UI thread ahead of the
    ///     first await. Moving that walk onto a worker is only safe WITH the
    ///     snapshot: an <c>ObservableCollection</c> enumerated off-thread while
    ///     the editor mutates it throws, and the beat map is what every emitted
    ///     position is resolved through.
    /// </summary>
    [Fact]
    public void TheSheetPdfExportSnapshotsTheChartBeforeHandingItToTheExporter()
    {
        var method = MethodBody(
            Source("DotCharter.UI", "ViewModels", "Shell", "ExportMenuViewModel.cs"),
            "private async Task ExportSheetPdf()");
        Assert.NotNull(method);

        Assert.Contains("SnapshotForExport(arrangement, level)", method, StringComparison.Ordinal);
        Assert.Contains("ChartProjectCloner.Clone(", method, StringComparison.Ordinal);
        Assert.Contains("_projectState.Beats.ToList()", method, StringComparison.Ordinal);

        // …and none of the live handles may reach the exporter call itself.
        var call = method[method.IndexOf("ExportToFileAsync(", StringComparison.Ordinal)..];
        Assert.DoesNotContain("_projectState.ActiveProject", call, StringComparison.Ordinal);
        Assert.DoesNotContain("_projectState.Beats,", call, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the exporter half: the alphaTex build must sit inside a
    ///     <c>Task.Run</c> that is given the cancellation token, so it neither
    ///     runs on the caller's thread nor starts at all once the export is
    ///     cancelled.
    /// </summary>
    [Fact]
    public void TheSheetPdfExporterBuildsItsAlphaTexOnAWorker()
    {
        var method = MethodBody(
            Source("DotCharter.Services", "Export", "Sheet", "SheetPdfExporter.cs"), "public async Task ExportAsync(");
        Assert.NotNull(method);

        // The lambda is an EXPRESSION, not a block, so there is no body to take:
        // assert instead that the export call and the token are inside the same
        // Task.Run STATEMENT — no `;` may separate them.
        var run = method.IndexOf("Task.Run(", StringComparison.Ordinal);
        Assert.True(run >= 0, "the alphaTex build must go through Task.Run");
        var statement = method[run..(method.IndexOf(';', run) + 1)];
        Assert.Contains("AlphaTexExporter.Export(", statement, StringComparison.Ordinal);
        Assert.Contains("cancellationToken", statement, StringComparison.Ordinal);
        // …and nothing may build it a second time on the caller's thread.
        Assert.Equal(1, Occurrences(method, "AlphaTexExporter.Export("));
    }

    /// <summary>
    ///     The MRU probe is the same freeze one phase EARLIER:
    ///     <c>ProjectMenuViewModel</c>'s constructor calls <c>RefreshRecentProjects</c>,
    ///     and the composition root builds it (with the shell) before <c>new MainWindow()</c>
    ///     — so <c>RecentProjectsList.Prune</c>'s <c>File.Exists</c> per entry
    ///     (ten of them) ran on the UI thread before the window existed. One
    ///     cloud-synced or network row blocks it for the share's timeout, which
    ///     is exactly the case <c>AddReceivedSongToLibrary</c>'s
    ///     <c>Directory.Exists</c> was moved off the thread for.
    ///     <para>
    ///         Source lint: which half runs on which thread is a property of the
    ///         code's shape, not of what a fake session returns.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheRecentProjectsPruneRunsOffTheUiThread_ButTheRowsArePublishedOnIt()
    {
        var source = Source("DotCharter.UI", "ViewModels", "Shell", "ProjectMenuViewModel.cs");

        var refresh = MethodBody(source, "internal void RefreshRecentProjects()");
        Assert.NotNull(refresh);
        // The synchronous half may not touch the disk at all.
        Assert.DoesNotContain("GetRecentProjects()", refresh!, StringComparison.Ordinal);
        Assert.Contains("GetRecentProjectsUnverified()", refresh, StringComparison.Ordinal);
        Assert.Contains("_recentProjectsGeneration++", refresh, StringComparison.Ordinal);

        var prune = MethodBody(source, "private async Task PruneRecentProjectsAsync()");
        Assert.NotNull(prune);
        Assert.Contains("await _projectSession.GetRecentProjectsAsync()", prune!, StringComparison.Ordinal);
        // …and the bound collection is written back through the dispatcher, not
        // on whatever thread the probe finished on.
        Assert.Contains("_ui.Post(", prune, StringComparison.Ordinal);
        Assert.Contains("var generation = _recentProjectsGeneration", prune, StringComparison.Ordinal);
        var posted = BalancedBlockAfter(prune, prune.IndexOf("_ui.Post(", StringComparison.Ordinal));
        Assert.NotNull(posted);
        Assert.Contains("if (generation == _recentProjectsGeneration)", posted, StringComparison.Ordinal);
        Assert.Contains("PublishRecentProjects(pruned)", posted, StringComparison.Ordinal);
        Assert.DoesNotContain("RecentProjects.Clear()", prune, StringComparison.Ordinal);
    }

    /// <summary>The service half: the pruning API has to be the one that goes to
    /// a worker, and the unverified one has to touch no filesystem.</summary>
    [Fact]
    public void TheSessionServicesUnverifiedMruReadDoesNoFileProbe()
    {
        var source = Source("DotCharter.Services", "Session", "ProjectSessionService.cs");

        // Expression-bodied, so slice to the terminating semicolon rather than
        // brace-matching.
        var unverified = ExpressionBody(source,
            "public IReadOnlyList<RecentProjectEntry> GetRecentProjectsUnverified()");
        Assert.NotNull(unverified);
        Assert.DoesNotContain("Prune", unverified!, StringComparison.Ordinal);

        var async = MethodBody(source,
            "public Task<IReadOnlyList<RecentProjectEntry>> GetRecentProjectsAsync()");
        Assert.NotNull(async);
        // The prune — and therefore every File.Exists — goes to the worker over
        // a snapshot; taking the snapshot on the caller's thread is what keeps
        // it from racing RecordRecentProject's write to the same list.
        Assert.Contains("Task.Run<IReadOnlyList<RecentProjectEntry>>(() => RecentProjectsList.Prune(snapshot))",
            async!, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The brace-balanced body of the method whose signature is
    /// <paramref name="signature" />, comments stripped so a doc comment cannot
    /// stand in for a call.</summary>
    private static string? MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        return start < 0 ? null : BalancedBlockAfter(source, start);
    }

    /// <summary>The expression body (<c>=&gt; … ;</c>) of an expression-bodied
    /// member, which <see cref="MethodBody" /> cannot brace-match.</summary>
    private static string? ExpressionBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = source.IndexOf(';', start + signature.Length);
        return end < 0 ? null : source[(start + signature.Length)..end];
    }

    /// <summary>The body of the LAST <c>Task.Run(</c> opened in
    /// <paramref name="method" /> — these methods have exactly one, and taking
    /// the innermost keeps a future nested lambda from widening it.</summary>
    private static string? InnermostTaskRunBody(string method)
    {
        var index = method.LastIndexOf("Task.Run(", StringComparison.Ordinal);
        return index < 0 ? null : BalancedBlockAfter(method, index);
    }

    private static string? BalancedBlockAfter(string source, int from)
    {
        var open = source.IndexOf('{', from);
        if (open < 0) return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        return null;
    }

    private static string Source(params string[] parts) =>
        StripComments(UiSourceTree.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts])));

    /// <summary>Blanks comments while keeping every character position, so brace
    /// matching and the index comparisons above still line up. Without it the
    /// method's own doc comment — which names <c>Task.Run</c> and
    /// <c>SetMessage</c> — would satisfy the assertions on its own.</summary>
    private static string StripComments(string source)
    {
        var chars = source.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n')
                    chars[i++] = ' ';
            }
            else if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n') chars[i] = ' ';
                    i++;
                }

                if (i < chars.Length) chars[i++] = ' ';
                if (i < chars.Length) chars[i++] = ' ';
            }
            else
            {
                i++;
            }
        }

        return new string(chars);
    }
}
