using System.Text;
using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Diagnostics;

/// <summary>
///     The Session Health panel's "Recent log" used to <c>ReadToEnd()</c> a file
///     the rolling sink lets grow to <c>AppConfig.LogFileMaxBytes</c> (10 MB) and
///     then <c>Split('\n')</c> the result, to keep 200 lines — ~30-40 MB of
///     transient LOH on every refresh. The bounded read already existed one call
///     site away, inside <c>DiagnosticsBundleExporter</c>; it is now the shared
///     <see cref="LogTailReader" /> both go through.
///     <para>
///         Asserted channel is ALLOCATED BYTES (per THREAD, so a parallel suite
///         cannot leak into the budget — the same fix
///         <c>FrameSnapshotPerfGateTests</c> needed) and line content. Never
///         wall-clock.
///     </para>
/// </summary>
public sealed class LogTailReaderTests : IDisposable
{
    private const int Lines = 80_000; // ~6.6 MB — the sink rolls at 10 MB
    private const int TailLines = 200;
    private const int MaxBytes = 512 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dotcharter-logtail-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void TheTailIsTheLastLinesOfTheFile()
    {
        var path = WriteLog(out var total);

        var tail = LogTailReader.ReadTailLines(path, TailLines, MaxBytes);

        var lines = tail.Split('\n');
        Assert.Equal(TailLines, lines.Length);
        Assert.Equal($"line {total - 1:D6} " + Filler, lines[^1]);
        Assert.Equal($"line {total - TailLines:D6} " + Filler, lines[0]);
    }

    [Fact]
    public void ReadingTheTailCostsTheWINDOW_NotTheFile()
    {
        var path = WriteLog(out _);
        var fileBytes = new FileInfo(path).Length;
        Assert.True(fileBytes > 4 * 1024 * 1024, $"the fixture must be large to be meaningful (was {fileBytes}).");

        // Warm the paths so JIT/statics do not land in the measurement.
        _ = LogTailReader.ReadTailLines(path, TailLines, MaxBytes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = LogTailReader.ReadTailLines(path, TailLines, MaxBytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // ReadToEnd()+Split on this fixture allocates well over 20 MB (a UTF-16
        // copy of the whole file, then a fragment per line). The window is
        // 512 KB, so 4 MB is a wide budget that still fails hard the moment the
        // read stops being bounded.
        Assert.True(allocated < 4 * 1024 * 1024,
            $"reading a {fileBytes / 1024 / 1024} MB log's tail allocated {allocated / 1024} KB — the read is not bounded");
    }

    [Fact]
    public void ATruncatedReadDropsTheLineTheSeekCutInHalf()
    {
        var path = WriteLog(out _);

        // A tiny window lands mid-line; a half line at the top would read as a
        // corrupt log entry in the panel.
        var tail = LogTailReader.ReadTailLines(path, TailLines, 200);

        foreach (var line in tail.Split('\n'))
            Assert.StartsWith("line ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ASmallFileIsReturnedWhole()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "small.log");
        File.WriteAllText(path, "alpha\nbeta\ngamma\n", Encoding.UTF8);

        var tail = LogTailReader.ReadTailLines(path, TailLines, MaxBytes);

        Assert.Equal("alpha\nbeta\ngamma", tail);
    }

    [Fact]
    public void ALiveHeldLogCanStillBeRead()
    {
        // Serilog holds the active file open for writing; the reader must not
        // need exclusive access.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "live.log");
        using var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        held.Write(Encoding.UTF8.GetBytes("held line\n"));
        held.Flush();

        Assert.Equal("held line", LogTailReader.ReadTailLines(path, TailLines, MaxBytes));
    }

    [Fact]
    public void TheDiagnosticsPanelResolvesTheActiveLogFileOncePerRefresh()
    {
        // Source lint: constructing DiagnosticsViewModel needs the whole service
        // graph, and the claim is structural. Resolving twice re-enumerates the
        // log directory and re-stats every rolled sibling for the same answer.
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "ViewModels", "Diagnostics", "DiagnosticsViewModel.cs")));

        var start = source.IndexOf("private void Refresh()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Refresh() is what rebuilds the dashboard; it must still exist.");
        var end = source.IndexOf("private async Task ExportBundleAsync()", start, StringComparison.Ordinal);
        Assert.True(end > start, "ExportBundleAsync must still follow Refresh.");

        var refresh = source[start..end];
        Assert.Equal(1, CountOf(refresh, "ResolveActiveLogFile("));
        Assert.Contains("RefreshLog(activeLog)", refresh, StringComparison.Ordinal);
        Assert.Contains("RefreshPaths(activeLog)", refresh, StringComparison.Ordinal);

        // …and the tail itself goes through the shared bounded reader.
        Assert.Contains("LogTailReader.ReadTailLines(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("reader.ReadToEnd()", source, StringComparison.Ordinal);
    }

    private const string Filler = "0123456789012345678901234567890123456789012345678901234567890123456789";

    private string WriteLog(out int total)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "dotcharter_0.log");
        var builder = new StringBuilder(Lines * (Filler.Length + 12));
        for (var i = 0; i < Lines; i++) builder.Append($"line {i:D6} ").Append(Filler).Append('\n');
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        total = Lines;
        return path;
    }

    private static int CountOf(string haystack, string needle)
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
}
