using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     The deterministic half of the alphaTab-exclusivity guard.
///     <para>
///         <see cref="GpExporterConcurrencyTests" /> proves the race BEHAVIOURALLY,
///         but its window is narrow — with the lock removed it caught only 1-2
///         mismatching pairs out of 250, so on its own it is a guard that might
///         not fire. This one cannot miss: it reads the source and asserts that
///         every route into alphaTab's process-global state goes through
///         <c>AlphaTabRuntime</c>.
///     </para>
///     <para>
///         Two distinct globals live behind that gate and each has already cost a
///         bug: the lazily-filled static engraving defaults (a torn
///         <c>List.Add</c> — see <c>AlphaTabRuntime.CreateSettings</c>) and the
///         static id counter <c>Score.ResetIds()</c> rewinds, whose ids are
///         written into the emitted <c>score.gpif</c>.
///     </para>
/// </summary>
public sealed class GpExporterExclusivityLintTests
{
    /// <summary>Projects that may legitimately touch alphaTab at all.</summary>
    private static readonly string[] AlphaTabConsumers = ["DotCharter.Services", "DotCharter.Tone", "DotCharter.UI"];

    /// <summary>The calls that reach alphaTab's shared statics. Each must appear
    /// only inside a file that also opens an <c>AlphaTabRuntime</c> region.</summary>
    private static readonly (string Call, string Why)[] GlobalStateCalls =
    [
        ("Score.ResetIds()", "rewinds the process-global id counter the emitted gpif embeds"),
        ("new Gp7Exporter()", "serializes a model numbered from that global counter"),
        ("new Settings()", "lazily fills alphaTab's static engraving defaults — a known torn List.Add"),
        (".ReadScore()", "allocates Bar/Beat ids from the process-global counter ResetIds() rewinds"),
        (".RenderScore(", "renders a model numbered from that counter and builds id-keyed bounds from it")
    ];

    /// <summary>
    ///     `AlphaTabRuntime` is allowed to make these calls — it IS the gate.
    /// </summary>
    private const string TheGateItself = "AlphaTabRuntime.cs";

    /// <summary>
    ///     REGION-aware, not file-aware (concurrency audit #8): this used to mark
    ///     a whole file as gated because it contained
    ///     <c>AlphaTabRuntime.CreateSettings</c> somewhere, so
    ///     <c>SheetMusicRenderer</c> — settings through the gate, model build and
    ///     render outside it — passed vacuously. Every call below must sit
    ///     INSIDE the argument span of an <c>AlphaTabRuntime.RunExclusive(</c>.
    /// </summary>
    [Fact]
    public void EveryAlphaTabGlobalStateCall_SitsInsideAnAlphaTabRuntimeRegion()
    {
        var offenders = new List<string>();
        var covered = 0;

        foreach (var file in AlphaTabConsumers
                     .Select(p => Path.Combine(TestPaths.RepoRoot(), p))
                     .Where(Directory.Exists)
                     .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                     .Where(f => !Path.GetFileName(f).Equals(TheGateItself, StringComparison.Ordinal)))
        {
            var code = SourceText.StripCommentLines(File.ReadAllText(file));
            var regions = ExclusiveRegions(code);

            foreach (var (call, why) in GlobalStateCalls)
            {
                var at = code.IndexOf(call, StringComparison.Ordinal);
                while (at >= 0)
                {
                    covered++;
                    if (!regions.Any(r => at > r.Start && at < r.End))
                    {
                        offenders.Add(
                            $"{Path.GetRelativePath(TestPaths.RepoRoot(), file)} calls `{call}` — which {why} — " +
                            "outside every AlphaTabRuntime.RunExclusive region in the file");
                    }

                    at = code.IndexOf(call, at + call.Length, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(covered >= 4, $"only {covered} alphaTab global-state calls found; the needles have rotted");
        Assert.True(offenders.Count == 0,
            "alphaTab's shared statics must only be touched inside an AlphaTabRuntime region:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    ///     Concurrency audit #7: <c>SheetMusicRenderer.EnsureAlphaSkiaLoaded</c>
    ///     was an <c>Interlocked.Exchange</c> CLAIM flag, so the second of two
    ///     cold renderers returned immediately and rendered with AlphaSkia
    ///     half-loaded and no fonts registered — a textless sheet or PDF. A
    ///     source lint, because the load itself needs the native AlphaSkia
    ///     library and a font registry the default gate deliberately does not
    ///     boot: the barrier is a <c>lock</c> whose ready flag is set LAST.
    /// </summary>
    [Fact]
    public void SheetRenderer_LoadsAlphaSkiaBehindACompletionBarrier()
    {
        var source = SourceText.StripCommentLines(File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Services", "Export", "Sheet", "SheetMusicRenderer.cs")));
        var start = source.IndexOf("private static void EnsureAlphaSkiaLoaded()", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "SheetMusicRenderer no longer declares `private static void EnsureAlphaSkiaLoaded()` — the lint lost its subject; repoint it.");
        var body = source[start..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];

        Assert.False(body.Contains("Interlocked.Exchange", StringComparison.Ordinal), LintReport.Of(
            "EnsureAlphaSkiaLoaded must be a completion barrier, not a claim flag: with Interlocked.Exchange the second " +
            "cold renderer returns before AlphaSkia is loaded and renders a textless sheet.",
            "DotCharter.Services/Export/Sheet/SheetMusicRenderer.cs: EnsureAlphaSkiaLoaded uses Interlocked.Exchange",
            "take `lock (AlphaSkiaGate)`, load, register the fonts, and set _alphaSkiaReady = true LAST.",
            "DotCharter.Tests/AGENTS.md (alphaTab is process-global)"));
        var gate = body.IndexOf("lock (AlphaSkiaGate)", StringComparison.Ordinal);
        var load = body.IndexOf("AlphaSkiaPlatform.LoadLibrary()", StringComparison.Ordinal);
        var fonts = body.IndexOf("RegisterArialFonts()", StringComparison.Ordinal);
        var ready = body.IndexOf("_alphaSkiaReady = true", StringComparison.Ordinal);
        Assert.True(gate >= 0 && gate < load && load < fonts && fonts < ready,
            "EnsureAlphaSkiaLoaded must take the gate, load, register fonts, and only THEN mark ready");
    }

    /// <summary>The argument span of every <c>AlphaTabRuntime.RunExclusive(</c>
    /// in <paramref name="code" />, by parenthesis matching.</summary>
    private static List<(int Start, int End)> ExclusiveRegions(string code)
    {
        var spans = new List<(int, int)>();
        const string needle = "AlphaTabRuntime.RunExclusive";
        var at = code.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            var open = code.IndexOf('(', at);
            if (open < 0) break;
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '(')
                {
                    depth++;
                }
                else if (code[i] == ')' && --depth == 0)
                {
                    spans.Add((open, i));
                    break;
                }
            }

            at = code.IndexOf(needle, open, StringComparison.Ordinal);
        }

        return spans;
    }

    /// <summary>
    ///     The gate is only a gate if it locks. A `RunExclusive` that forwards
    ///     straight to its body compiles, keeps every caller, and silently
    ///     restores the race — which is exactly how this bug would come back.
    /// </summary>
    [Fact]
    public void RunExclusive_ActuallyTakesTheSameLockAsCreateSettings()
    {
        var gate = SourceText.StripCommentLines(File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Services", "Infrastructure", TheGateItself)));

        foreach (var member in new[] { "RunExclusive", "CreateSettings" })
        {
            var start = gate.IndexOf(member, StringComparison.Ordinal);
            Assert.True(start >= 0, $"AlphaTabRuntime no longer declares {member}");
            var body = gate[start..];
            var end = body.IndexOf("\n    }", StringComparison.Ordinal);
            if (end > 0) body = body[..end];

            Assert.True(Regex.IsMatch(body, @"lock\s*\(\s*Gate\s*\)"),
                $"AlphaTabRuntime.{member} no longer takes `lock (Gate)` — alphaTab's shared statics " +
                "are unguarded again, and the export race it fixed reproduces at ~1 in 250 exports");
        }
    }

    /// <summary>
    ///     `GpExporter` must build AND serialize inside one region — splitting the
    ///     two would let a second exporter's `ResetIds()` renumber a model this
    ///     one is still assembling, which is the exact shape of the original bug.
    /// </summary>
    [Fact]
    public void GpExporter_ResetsIdsInsideItsExclusiveRegion_NotBeforeIt()
    {
        var source = SourceText.StripCommentLines(File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Services", "Export", "GuitarPro", "GpExporter.cs")));

        var region = source.IndexOf("AlphaTabRuntime.RunExclusive", StringComparison.Ordinal);
        Assert.True(region >= 0, "GpExporter no longer opens an AlphaTabRuntime.RunExclusive region");

        foreach (var call in new[] { "Score.ResetIds()", "new Gp7Exporter()" })
        {
            var at = source.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at > region,
                $"GpExporter calls `{call}` outside (or ahead of) its RunExclusive region — " +
                "build and serialize have to share one exclusive hold on alphaTab's globals");
        }
    }
}
