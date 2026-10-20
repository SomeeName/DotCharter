using System.Text.RegularExpressions;

namespace DotCharter.Tests.Localization;

/// <summary>
///     A service's progress line is read by the user — the tone designer's and the tone
///     matcher's status, stem separation, lyric auto-sync, the model installers. Twenty-seven
///     of them were English string literals whatever the language ("Separating… 42%",
///     "Listening to the vocal…"). Every one goes through the resx now; this fails on a
///     literal handed to a <c>status</c> / <c>progress</c> reporter anywhere in Services.
/// </summary>
public sealed class ServiceProgressLocalizationTests
{
    [Fact]
    public void NoServiceReportsProgress_InALiteral()
    {
        // DotCharter.Tone holds the audio host and the tone rig that used to
        // live in Services; their progress lines answer to the same rule.
        var files = new[] { "DotCharter.Services", "DotCharter.Tone" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var reports = 0;
        var literal = new List<string>();
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            reports += Regex.Matches(source, @"\b(status|progress)\?\.Report\(").Count;
            literal.AddRange(Regex.Matches(source, @"\b(status|progress)\?\.Report\(\s*\$?""[^""]*""")
                .Select(m => $"{Path.GetFileName(file)}: {m.Value}"));
        }

        Assert.True(reports >= 25, $"only {reports} progress reports found — the pattern no longer fits Services");
        Assert.True(literal.Count == 0, "progress lines written as literals:\n  " + string.Join("\n  ", literal));
    }
}
