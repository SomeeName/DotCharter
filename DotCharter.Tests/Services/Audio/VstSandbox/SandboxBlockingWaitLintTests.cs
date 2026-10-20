using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// The VST sandbox code used to block on every pipe round trip to its child
/// (<c>.GetAwaiter().GetResult()</c>, eleven sites, up to 180 s each), and the
/// instrument-load worker parked a pool thread in <c>_vstGate.Wait()</c>
/// behind it. The paths are async end to end now; this keeps a blocking wait
/// from creeping back into the files that drive the child.
/// <para>
/// A source lint because the invariant is structural and the child cannot be
/// faked inside the service's own spawn path. Comments are stripped first —
/// the files explain what they replaced. <see cref="SandboxNonBlockingCallTests"/>
/// is the behavioural half.
/// </para>
/// </summary>
public sealed class SandboxBlockingWaitLintTests
{
    private static readonly string[] Files =
    [
        "DotCharter.Tone/Audio/JuceAudioPlaybackService.Sandbox.cs",
        "DotCharter.Tone/Audio/JuceAudioPlaybackService.VstChain.cs",
        "DotCharter.Tone/Audio/Vst/Sandbox/*.cs",
    ];

    /// <summary>(file name, enclosing method signature, reason).</summary>
    private static readonly (string File, string Method, string Reason)[] Allowed =
    [
        ("VstSandboxCalls.cs", "public static bool StopForDispose(",
            "JuceAudioPlaybackService.Dispose is synchronous and must not return with the child " +
            "still attached; the wait is bounded (10 s, then Kill) and every await under " +
            "VstSandboxHost.StopAsync is ConfigureAwait(false), so it cannot deadlock a UI context."),
    ];

    // Task waits only. A kernel-event WaitOne with a millisecond slice (the
    // managed VstSandboxSharedMemory's 20 ms response poll, which only tests
    // drive — the product bridge is native) is not a task block.
    private static readonly Regex Blocking = new(
        @"\.GetAwaiter\(\)\s*\.GetResult\(\)|\.Wait\(|\.Result\b|\.WaitAll\(|\.WaitAny\(",
        RegexOptions.Compiled);

    [Fact]
    public void NoSandboxPathBlocksOnATask()
    {
        var offenders = new List<string>();
        var allowedHits = new HashSet<string>();
        var scanned = 0;
        foreach (var path in SourceFiles())
        {
            scanned++;
            var code = StripComments(File.ReadAllText(path));
            var name = Path.GetFileName(path);
            foreach (Match m in Blocking.Matches(code))
            {
                var allowed = Allowed.FirstOrDefault(a => a.File == name && InsideMethod(code, a.Method, m.Index));
                if (allowed.File != null) { allowedHits.Add(allowed.Method); continue; }
                var line = code[..m.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{name}:{line}: {m.Value}");
            }
        }

        Assert.True(scanned >= 5, $"Scanned only {scanned} file(s) — a path moved and this lint went vacuous.");
        Assert.True(offenders.Count == 0,
            "A sandbox path blocks on a task again. Await it through VstSandboxCalls " +
            "(bounded, never throws) instead:\n  " + string.Join("\n  ", offenders));
        var stale = Allowed.Where(a => !allowedHits.Contains(a.Method)).Select(a => $"{a.File} {a.Method}").ToList();
        Assert.True(stale.Count == 0,
            "These exemptions no longer match a blocking wait — delete them:\n  " + string.Join("\n  ", stale));
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = TestPaths.RepoRoot();
        foreach (var pattern in Files)
        {
            var rel = pattern.Replace('/', Path.DirectorySeparatorChar);
            var dir = Path.Combine(root, Path.GetDirectoryName(rel)!);
            foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(rel)))
                yield return f;
        }
    }

    private static bool InsideMethod(string code, string signature, int index)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0 || index < start) return false;
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return index < i;
        }
        return false;
    }

    /// <summary>Blanks // and /* */ comments, keeping string literals and line numbers.</summary>
    private static string StripComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (c is '"' or '\'')
            {
                var verbatim = i > 0 && src[i - 1] == '@';
                sb.Append(c);
                for (i++; i < src.Length; i++)
                {
                    sb.Append(src[i]);
                    if (!verbatim && src[i] == '\\' && i + 1 < src.Length) { sb.Append(src[++i]); continue; }
                    if (src[i] == c || src[i] == '\n') break;
                }
            }
            else if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                if (i < src.Length) sb.Append('\n');
            }
            else if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                for (i += 2; i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'); i++)
                    if (src[i] == '\n') sb.Append('\n');
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
