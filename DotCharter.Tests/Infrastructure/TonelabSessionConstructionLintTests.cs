using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A real <c>TonelabSession</c> discovers every <c>tonelab-&lt;pid&gt;</c> pipe on the machine and connects to the
///     newest, so a test constructs the real session only with a discovery of its own (<c>() =&gt; [...]</c>), apart
///     from the session's unit tests under <c>Audio/Tonelab/</c>. Every other test takes
///     <see cref="FakeTonelabSession" />; the headless application and the shared product graph register it.
/// </summary>
public sealed partial class TonelabSessionConstructionLintTests
{
    [GeneratedRegex(@"new\s+(?:DotCharter\.Tone\.Audio\.Tonelab\.)?TonelabSession\s*\(")]
    private static partial Regex AnyConstruction();

    /// <summary>A construction whose first argument is not a discovery lambda: the one that lists the machine's pipes.</summary>
    [GeneratedRegex(@"new\s+(?:DotCharter\.Tone\.Audio\.Tonelab\.)?TonelabSession\s*\((?!\s*\(\)\s*=>)")]
    private static partial Regex Construction();

    [Fact]
    public void OnlyTheSessionsOwnTests_ConstructTheRealSession()
    {
        var testRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");
        var sessionTests = Path.Combine(testRoot, "Audio", "Tonelab") + Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        var constructing = 0;
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcluded(file)) continue;

            scanned++;
            var code = SourceText.StripComments(File.ReadAllText(file));
            if (AnyConstruction().IsMatch(code)) constructing++;
            if (Construction().IsMatch(code) && !file.StartsWith(sessionTests, StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(testRoot, file));
        }

        Assert.True(scanned > 500, $"only scanned {scanned} test files, the glob is broken");
        Assert.True(constructing >= 5, $"only {constructing} test files construct a session, the pattern is passing vacuously");
        Assert.True(offenders.Count == 0,
            "These test files construct the real TonelabSession, which probes the machine's tonelab pipes. " +
            "Take FakeTonelabSession instead: " + string.Join(", ", offenders));
    }

    private static bool IsExcluded(string file)
    {
        var name = Path.GetFileName(file);
        return file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
               || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
               || name.StartsWith("LocalOnly_", StringComparison.Ordinal)
               // This lint quotes the constructor in its detector case.
               || name == nameof(TonelabSessionConstructionLintTests) + ".cs";
    }

    [Fact]
    public void TheDetector_SeesBothSpellings()
    {
        Assert.Matches(Construction(), "var s = new TonelabSession(logger, [], null);");
        Assert.Matches(Construction(), "var s = new DotCharter.Tone.Audio.Tonelab.TonelabSession (logger, [], null);");
        Assert.DoesNotMatch(Construction(), "var s = new TonelabSession(() => [], null, null, null);");
        Assert.DoesNotMatch(Construction(), "var s = new FakeTonelabSession();");
    }
}
