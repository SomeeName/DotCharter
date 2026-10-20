namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A checkout must be byte-identical to what is committed. The code style is
///     enforced in the build (<c>.editorconfig</c>, IDE0055), and IDE0055 reads each
///     file's REAL line endings. Git for Windows' system config and GitHub's Windows
///     runners set <c>core.autocrlf=true</c>, which rewrote LF files to CRLF on
///     checkout, so CI and a fresh clone failed the build on files nobody had
///     touched. <c>.gitattributes</c> turns every conversion off; this keeps it off.
/// </summary>
public sealed class CheckoutLineEndingsTests
{
    [Fact]
    public void GitNeverConvertsLineEndingsOnCheckout()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), ".gitattributes");
        Assert.True(File.Exists(path), ".gitattributes is missing, so core.autocrlf decides every checkout's line endings again.");

        var rules = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        Assert.True(rules.Count > 0 && rules[0] == "* -text",
            $"the first rule of .gitattributes must be `* -text` (no line-ending conversion), found: {string.Join(" | ", rules)}");

        // A later rule that sets text or eol for some pattern turns conversion back on for it.
        var reEnabling = rules.Skip(1)
            .Where(r => r.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Any(a => a is "text" or "text=auto" || a.StartsWith("eol=", StringComparison.Ordinal)))
            .ToList();
        Assert.True(reEnabling.Count == 0,
            $"these .gitattributes rules turn line-ending conversion back on: {string.Join(" | ", reEnabling)}");
    }
}
