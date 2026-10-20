using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Every rig control that DRAWS artwork asks for it through
///     <c>RigArtworkStore.GetForRender(file, width, requester)</c>, never the
///     synchronous <c>Get</c> (#234).
/// </summary>
/// <remarks>
///     The compositor records <c>Render</c> for controls under a hidden overlay too,
///     so a synchronous decode in a render path runs on the UI thread whenever the
///     post-first-paint prewarm realizes a rig: settling the gear list put 13 WebP
///     decodes (~190 ms) into one render tick of the drain. <c>GetForRender</c>
///     queues a hidden control's decode on the pool. The one synchronous call left
///     is a MEASURE (a module with no faceplate sizes itself by its illustration's
///     aspect), listed below with its reason; the behaviour itself is pinned by
///     <c>Perf/RigFirstOpenPrewarmUiPerfSmokeTests</c>.
/// </remarks>
public sealed class RigArtworkRenderLintTests
{
    /// <summary>File name → how many synchronous <c>Get</c> calls it may make, and why.</summary>
    private static readonly Dictionary<string, (int Count, string Reason)> Allowed = new(StringComparer.Ordinal)
    {
        ["RigFaceplateView.cs"] = (1, "Aspect: a measure, not a render — sizes a face-less module by its illustration"),
    };

    private static readonly Regex SyncGet = new(@"RigArtworkStore\.Get\(", RegexOptions.Compiled);

    [Fact]
    public void RigRenderPaths_AskForArtworkThroughGetForRender()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI");
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            // The store itself: its own Get is the synchronous path GetForRender falls back to.
            if (name == "RigArtworkImage.cs") continue;
            var source = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            var count = SyncGet.Matches(source).Count;
            if (count == 0) continue;
            seen.Add(name);
            if (!Allowed.TryGetValue(name, out var allowed) || count > allowed.Count)
                offenders.Add($"{name}: {count} synchronous RigArtworkStore.Get call(s)");
        }

        Assert.True(offenders.Count == 0,
            "A rig render path decodes artwork synchronously — use RigArtworkStore.GetForRender(file, width, this):\n" +
            string.Join("\n", offenders));

        // A stale allowance would let the next synchronous call in for free.
        var stale = Allowed.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0, LintReport.Of(
            "These allowances name a file that no longer makes a synchronous RigArtworkStore.Get call, so they would let the next one in for free:",
            stale,
            "delete the entry from RigArtworkRenderLintTests.Allowed."));
    }
}
