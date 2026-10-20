using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Source lints for C.2: no observable-property setter may pay disk I/O on
///     the UI thread, and <c>appconfig.json</c> stays behind ONE writer.
///     <para>
///         Twelve <c>partial void On&lt;X&gt;Changed</c> handlers persisted the
///         app config inline. The atomic write + fsync + rename measured 4.40 ms
///         median / 9.68 ms p90 / 13.48 ms max over the owner's real 16,878-byte
///         appconfig.json, and <c>OnSheetZoomChanged</c> fires once per zoom
///         step — 47–94 ms of UI thread per second at 10–20 steps/s, against a
///         150 ms view-switch budget. The fix pushed the write into
///         <c>JsonConfigurationService</c>'s coalescing background writer, which
///         only helps for as long as nothing routes around it.
///     </para>
///     <para>
///         Source lints for the same reason as <c>StartupWorkDeferralTests</c>:
///         the behaviour lives in <c>CoalescedFileWriterTests</c> and
///         <c>JsonConfigurationServiceTests</c>, but "a new setter did its own
///         <c>AtomicFile.WriteAllText</c>" is invisible to those — it is a NEW
///         call site, and only reading the source finds it. Never a wall-clock
///         assertion.
///     </para>
/// </summary>
public sealed class ConfigWriteDeferralTests
{
    /// <summary>The synchronous file-write calls that must never appear inside a
    /// property-changed handler. The async overloads are deliberately absent —
    /// awaiting one does not block the UI thread.</summary>
    private static readonly string[] BlockingWrites =
    [
        "AtomicFile.WriteAllText(",
        "File.WriteAllText(",
        "File.WriteAllBytes(",
        "File.WriteAllLines(",
        "File.AppendAllText(",
        "File.Copy("
    ];

    /// <summary>
    ///     No <c>partial void On&lt;X&gt;Changed</c> handler in the UI project may
    ///     write a file synchronously. These run on the UI thread between a click
    ///     (or a zoom step, or a slider tick) and the next frame; persistence
    ///     belongs behind <c>IConfigurationService</c>, whose writes are coalesced
    ///     onto a background thread.
    /// </summary>
    [Fact]
    public void NoObservablePropertySetterWritesAFileSynchronously()
    {
        var offenders = new List<string>();

        foreach (var (file, text) in UiSources())
        {
            if (!text.Contains("partial void On", StringComparison.Ordinal)) continue;

            var source = StripCommentsAndStrings(text);
            foreach (var match in Regex.Matches(source, @"partial\s+void\s+(On\w+Changed)\s*\(")
                         .Cast<Match>())
            {
                var body = BodyAfter(source, match.Index + match.Length);
                if (body == null) continue;

                foreach (var call in BlockingWrites)
                {
                    if (body.Contains(call, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value} calls {call})");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A property-changed handler runs on the UI thread; move the write off it " +
            "(IConfigurationService coalesces config saves onto a background writer):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     One writer for the app config. Every <c>SaveAppConfig</c> caller (~50
    ///     of them) gets the coalescing for free precisely because the path is
    ///     resolved in exactly one place; a second site resolving it would be a
    ///     second, un-coalesced writer — and would also race the atomic rename.
    /// </summary>
    [Fact]
    public void OnlyJsonConfigurationServiceResolvesTheAppConfigPath()
    {
        var offenders = Sources.Value
            .Where(s => s.Text.Contains("ResolveConfigFile(\"appconfig.json\")", StringComparison.Ordinal))
            .Select(s => Path.GetFileName(s.Path))
            .Where(n => !string.Equals(n, "JsonConfigurationService.cs", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "appconfig.json must be written through JsonConfigurationService alone; also found: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    ///     The deferral itself: <c>SaveAppConfig</c> may only ARM the write. If
    ///     the atomic write comes back into the locked, caller-thread half, every
    ///     setter pays fsync again and the behavioural tests around the debounce
    ///     window would still pass (they flush before asserting the file).
    /// </summary>
    [Fact]
    public void SaveAppConfigOnlyArmsTheWrite()
    {
        var source = StripCommentsAndStrings(UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Services", "Infrastructure", "Configuration",
            "JsonConfigurationService.cs")));

        var start = source.IndexOf("private void SaveLocked(AppConfig config)", StringComparison.Ordinal);
        Assert.True(start >= 0, "SaveLocked is the locked, caller-thread half of a save; it must still exist.");

        var body = BodyAfter(source, start);
        Assert.NotNull(body);
        Assert.DoesNotContain("AtomicFile.WriteAllText", body, StringComparison.Ordinal);
        // …and it must still publish the cache synchronously: callers LoadAppConfig()
        // straight after a save and have to see what they just wrote.
        Assert.Contains("_appConfigCache = config", body, StringComparison.Ordinal);
        Assert.Contains("_appConfigWriter.Queue", body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Both exit paths flush. <c>TerminateNow</c> <c>TerminateProcess</c>-es,
    ///     so no <c>Dispose</c> ever runs there; <c>RunCleanShutdown</c> is the
    ///     clean half and its order is pinned by
    ///     <c>CleanShutdownSequenceTests</c>. Losing either one costs the user
    ///     whatever they changed in the last debounce window, silently.
    /// </summary>
    [Fact]
    public void BothExitPathsFlushTheCoalescedConfigWriter()
    {
        var source = StripCommentsAndStrings(UiSourceTree.ReadAllText(
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.Shutdown.cs"))));

        var terminate = BodyAfter(source,
            source.IndexOf("public static void TerminateNow()", StringComparison.Ordinal));
        Assert.NotNull(terminate);
        Assert.Contains("GetService<IConfigurationService>())?.Flush()", terminate, StringComparison.Ordinal);

        var clean = BodyAfter(source,
            source.IndexOf("private void ShutdownServices()", StringComparison.Ordinal));
        Assert.NotNull(clean);
        Assert.Contains("GetService<IConfigurationService>()", clean, StringComparison.Ordinal);
        Assert.Contains("config?.Flush()", clean, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The chart synth's instrument picks (#66) save on their own queue, so both
    ///     exit paths flush it too, with a bound (#82) — and only once the
    ///     coordinator has started, so a close never constructs it to flush it.
    /// </summary>
    [Fact]
    public void BothExitPathsFlushTheChartSynthInstrumentSaves()
    {
        var source = StripCommentsAndStrings(UiSourceTree.ReadAllText(
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.Shutdown.cs"))));

        var terminate = BodyAfter(source,
            source.IndexOf("public static void TerminateNow()", StringComparison.Ordinal));
        Assert.NotNull(terminate);
        Assert.Contains("FlushChartSynthInstruments(_chartSynthInstruments,", terminate, StringComparison.Ordinal);

        var clean = BodyAfter(source,
            source.IndexOf("private void ShutdownServices()", StringComparison.Ordinal));
        Assert.NotNull(clean);
        Assert.Contains("FlushChartSynthInstruments(instruments,", clean, StringComparison.Ordinal);

        var start = StripCommentsAndStrings(UiSourceTree.ReadAllText(
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml.cs"))));
        Assert.Contains("_chartSynthInstruments = Services.GetRequiredService<", start, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>Every UI and Services source file, read ONCE for the whole
    /// class. Both lints below walk the same ~1,000 files; re-reading them per
    /// test is tens of megabytes of short-lived strings for no extra
    /// coverage.</summary>
    private static readonly Lazy<IReadOnlyList<(string Path, string Text)>> Sources =
        new(() => SourceFilesIn("DotCharter.UI").Concat(SourceFilesIn("DotCharter.Services")).Concat(SourceFilesIn("DotCharter.Tone"))
            .Select(f => (f, UiSourceTree.ReadAllText(f)))
            .ToList());

    private static IEnumerable<(string Path, string Text)> UiSources() =>
        Sources.Value.Where(s => s.Path.Contains(
            $"{Path.DirectorySeparatorChar}DotCharter.UI{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> SourceFilesIn(string project) =>
        UiSourceTree.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal));

    /// <summary>The brace-balanced body of the member whose declaration starts at
    /// <paramref name="from" />; null when there is no block after it (an
    /// expression-bodied member, or a partial declaration with no body).</summary>
    private static string? BodyAfter(string source, int from)
    {
        if (from < 0) return null;

        var open = source.IndexOf('{', from);
        if (open < 0) return null;
        // A `;` before the first `{` means this declaration has no body at all.
        var semicolon = source.IndexOf(';', from);
        if (semicolon >= 0 && semicolon < open) return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        return null;
    }

    /// <summary>
    ///     Blanks comments and string/char literals, keeping every character
    ///     position so brace matching and indices still line up. Without it a
    ///     `see cref` or a doc-comment example counts as a call — the trap
    ///     <c>CoopReachabilityTests</c> documents.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var chars = source.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            var c = chars[i];
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n') chars[i] = ' ';
                    i++;
                }

                if (i < chars.Length) chars[i++] = ' ';
                if (i < chars.Length) chars[i++] = ' ';
            }
            else if (c is '"' or '\'')
            {
                var quote = c;
                chars[i++] = ' ';
                while (i < chars.Length && chars[i] != quote && chars[i] != '\n')
                {
                    var escaped = chars[i] == '\\';
                    chars[i++] = ' ';
                    if (escaped && i < chars.Length) chars[i++] = ' ';
                }

                if (i < chars.Length && chars[i] == quote) chars[i++] = ' ';
            }
            else
            {
                i++;
            }
        }

        return new string(chars);
    }
}
