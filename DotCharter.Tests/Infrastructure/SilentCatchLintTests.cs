using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>catch (Exception ex) { Debug.WriteLine(…); }</c> is a swallow, not a log.
///     <para>
///         <c>Debug.WriteLine</c> carries <c>[Conditional("DEBUG")]</c>, so the
///         compiler removes the CALL from every Release build. Nothing is written
///         anywhere, and neither is anything thrown — so the whole catch block is
///         `{ }` in the binary the user runs. On a persistence path that is lost
///         work with no trace: the dock layout, the window layout and the live tone
///         rig each failed to save exactly that way, and the tone rig is the only
///         copy of the last few seconds of knob tweaking (ToneSessionService polls
///         rather than writing per knob).
///     </para>
///     <para>
///         Some genuinely are debug-only and stay — the terminate path cannot log
///         (the logger is being flushed and the container disposed), a cancel that
///         races a completed task has nothing to report. Each of those is an
///         <see cref="Allowed" /> entry carrying a REASON, and the lint fails on a
///         STALE entry too, so the list cannot quietly outlive the code it excuses.
///     </para>
/// </summary>
public sealed class SilentCatchLintTests
{
    /// <summary>A catch block found by the scan: where it is and what it would have
    /// printed (the literal head of the message, which is what an entry is keyed on).</summary>
    private readonly record struct SilentCatch(string File, string Signature);

    /// <summary>
    ///     Deliberate exemptions. <c>Signature</c> is the literal head of the
    ///     message — everything before the first interpolation hole — so it survives
    ///     a reword of the exception text but not a reword of the message itself,
    ///     which is when someone should look again.
    /// </summary>
    private sealed record Exemption(string File, string Signature, string Reason);

    private static readonly Exemption[] Allowed =
    [
        // App.Shutdown.cs — TerminateNow / RunCleanShutdown. Every step here is
        // either flushing the logger, disposing the container that OWNS it, or
        // killing the process; a logger call would reach a disposed sink at best
        // and re-enter the failing teardown at worst.
        new("DotCharter.UI/App.Shutdown.cs", "[Shutdown] Recovery clear failed: ",
            "terminate path: the logger is flushed two lines later and the container is going away"),
        new("DotCharter.UI/App.Shutdown.cs", "[Shutdown] Config flush failed: ",
            "terminate path: this IS the flush step; logging its failure needs the sink it just failed on"),
        new("DotCharter.UI/App.Shutdown.cs", "[Shutdown] Flush failed: ",
            "terminate path: the failing call is the logger's own Flush()"),
        new("DotCharter.UI/App.Shutdown.cs", "[Shutdown] Usage flush failed: ",
            "terminate path: runs after the logger flush above"),
        new("DotCharter.UI/App.Shutdown.cs", "[Shutdown] TerminateProcess failed: ",
            "the process is being killed; nothing survives to read a log entry"),
        new("DotCharter.UI/App.Shutdown.cs", "[Shutdown] Environment.Exit failed: ",
            "same: the last resort after TerminateProcess"),
        new("DotCharter.UI/App.Shutdown.cs", "[Shutdown] Audio dispose failed: ",
            "clean-shutdown path, after the logger flush"),
        new("DotCharter.UI/App.Shutdown.cs", "[Shutdown] Container dispose failed: ",
            "disposing the container that owns the logger"),

        // Cancellation of work nobody is waiting for. A CancellationTokenSource
        // that was already disposed by the completing task throws here, and that
        // race is the normal case, not a fault.
        // Per-frame render path. An IAppLogger call here runs up to 60x a second
        // during exactly the teardown storm it exists to survive; the state that
        // matters is carried by _lastSnapshotSuccess, which the driver reads.
        new("DotCharter.UI/Controls/Preview3DRenderDriver.cs", "Failed to update rendering snapshot: ",
            "60 Hz path: an unthrottled log would flood; _lastSnapshotSuccess is the signal that is read"),

        // Dock layout. Nothing here is the user's DATA - the layout is derived
        // state, re-derivable from the default, and every one of these failures is
        // visible on screen the instant it happens (the pane does not float, the
        // pane does not pin, the default layout appears).
        new("DotCharter.UI/Docking/DockLayoutController.cs", "[Dock] Closing orphaned float window failed: ",
            "a float window that would not close is on screen; nothing persisted is at stake"),
        new("DotCharter.UI/Docking/DockLayoutController.cs", "[Dock] Floating pane '",
            "the pane visibly stays docked; derived layout state only"),
        new("DotCharter.UI/Docking/DockLayoutController.cs", "[Dock] Pinning pane '",
            "the pane visibly stays unpinned; derived layout state only"),
        new("DotCharter.UI/Views/MainWindow.Docking.cs", "[Dock] Restoring the saved layout failed (",
            "falls back to the default layout, which the user sees; the saved state on disk is untouched"),

        // Prewarm, not persistence. Nothing is written and nothing is lost: a
        // deferred body that will not build just stays cold and pays its
        // construction on the first navigation, which is the behaviour this
        // optimisation replaced. The cost of a silent failure is latency, and the
        // startup-phase numbers are where that shows up.
        new("DotCharter.UI/Views/PlayMode/PlayModeView.axaml.cs", "Deferred build of ",
            "deferred-content prewarm: failure falls back to building on first navigation, nothing persisted"),
    ];

    [Fact]
    public void NoPersistencePath_SwallowsIntoDebugWriteLine()
    {
        var found = ProductSources().SelectMany(ScanFile).ToList();

        var unexpected = found
            .Where(f => !Allowed.Any(a => a.File == f.File && a.Signature == f.Signature))
            .ToList();

        Assert.True(unexpected.Count == 0,
            "Debug.WriteLine is [Conditional(\"DEBUG\")] — in the Release build the user runs, these " +
            "catch blocks are empty. Log through IAppLogger, or add an Allowed entry with a reason " +
            "saying why nothing needs to be said:" + Environment.NewLine +
            string.Join(Environment.NewLine, unexpected.Select(u => $"  {u.File}: \"{u.Signature}\"")));
    }

    /// <summary>
    ///     A stale exemption is how an allow-list rots into a shield. If the code it
    ///     excuses is gone or has been fixed, the entry has to go with it — otherwise
    ///     the next person to write the same swallow inherits somebody else's reason.
    /// </summary>
    [Fact]
    public void EveryExemption_StillDescribesRealCode()
    {
        var found = ProductSources().SelectMany(ScanFile).ToHashSet();

        var stale = Allowed
            .Where(a => !found.Contains(new SilentCatch(a.File, a.Signature)))
            .Select(a => $"  {a.File}: \"{a.Signature}\" ({a.Reason})")
            .ToList();

        Assert.True(stale.Count == 0,
            "These exemptions no longer match any catch block — delete them rather than leaving a " +
            "reason lying around for the next swallow to inherit:" + Environment.NewLine +
            string.Join(Environment.NewLine, stale));

        // The scan itself must keep finding SOMETHING, or a change to the masking /
        // brace matching would make both tests above pass by finding nothing at all.
        Assert.True(found.Count >= Allowed.Length,
            $"The scanner found {found.Count} silent catches but {Allowed.Length} are allow-listed — " +
            "the scan has stopped matching. Fix the scanner, do not trim the list.");
    }

    // ---------- the scan ----------

    private static readonly Regex CatchHead = new(@"catch\s*(\([^)]*\))?\s*\{", RegexOptions.Compiled);

    private static IEnumerable<SilentCatch> ScanFile(string path)
    {
        var source = UiSourceTree.ReadAllText(path);
        // Comments and string CONTENTS become spaces of the same length, so every
        // index below is an index into `source` too — and a brace or a `catch`
        // inside a message can never be mistaken for code.
        var masked = Mask(source);
        var rel = Path.GetRelativePath(TestPaths.RepoRoot(), path).Replace('\\', '/');

        foreach (Match head in CatchHead.Matches(masked))
        {
            var open = head.Index + head.Length - 1;
            var close = MatchingBrace(masked, open);
            if (close < 0) continue;

            var bodyMasked = masked[(open + 1)..close];
            var body = source[(open + 1)..close];
            var trimmed = bodyMasked.Trim();
            if (!trimmed.StartsWith("Debug.WriteLine(", StringComparison.Ordinal) &&
                !trimmed.StartsWith("System.Diagnostics.Debug.WriteLine(", StringComparison.Ordinal))
            {
                continue;
            }

            // Exactly one statement — a second one means the catch does something
            // real as well and is not a silent swallow.
            if (bodyMasked.Count(c => c == ';') != 1) continue;

            yield return new SilentCatch(rel, LiteralHead(body));
        }
    }

    /// <summary>The literal text of the message up to its first interpolation hole
    /// (or its end) — the stable half of a <c>$"…{ex.Message}"</c>.</summary>
    private static string LiteralHead(string body)
    {
        var quote = body.IndexOf('"');
        if (quote < 0) return body.Trim();

        var sb = new StringBuilder();
        for (var i = quote + 1; i < body.Length; i++)
        {
            var c = body[i];
            if (c is '"' or '{') break;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int MatchingBrace(string masked, int open)
    {
        var depth = 0;
        for (var i = open; i < masked.Length; i++)
        {
            if (masked[i] == '{') depth++;
            else if (masked[i] == '}' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>
    ///     Same-length copy of <paramref name="source" /> with comments and the
    ///     CONTENTS of every string/char literal replaced by spaces. Handles
    ///     verbatim (<c>@"</c>), interpolated (<c>$"</c>) and raw (<c>"""</c>)
    ///     literals, because an interpolated message routinely contains braces and
    ///     a verbatim path routinely contains <c>//</c>.
    /// </summary>
    private static string Mask(string source)
    {
        var masked = new StringBuilder(source);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') Blank(masked, source, i++);
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                Blank(masked, source, i++);
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                    Blank(masked, source, i++);
                if (i < source.Length) Blank(masked, source, i++);
                if (i < source.Length) Blank(masked, source, i++);
            }
            else if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                i += 3;
                while (i + 2 < source.Length &&
                       !(source[i] == '"' && source[i + 1] == '"' && source[i + 2] == '"'))
                {
                    Blank(masked, source, i++);
                }

                i = Math.Min(source.Length, i + 3);
            }
            else if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { Blank(masked, source, i++); Blank(masked, source, i++); continue; }
                        i++;
                        break;
                    }

                    Blank(masked, source, i++);
                }
            }
            else if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\') Blank(masked, source, i++);
                    if (i < source.Length) Blank(masked, source, i++);
                }

                if (i < source.Length) i++;
            }
            else
            {
                i++;
            }
        }

        return masked.ToString();
    }

    /// <summary>Blanks one character, keeping newlines so line structure (and
    /// therefore the `//` scan above) still works.</summary>
    private static void Blank(StringBuilder masked, string source, int index)
    {
        if (index < source.Length && source[index] != '\n' && source[index] != '\r')
            masked[index] = ' ';
    }

    private static IEnumerable<string> ProductSources()
    {
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone" })
        {
            foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), project), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(TestPaths.RepoRoot(), file);
                if (rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }
}
