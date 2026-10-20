using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Logging;

/// <summary>
///     A test that builds a real <c>AppLogger</c> must say where its file goes.
///     <para>
///         A default <c>AppConfig</c> logs to a FILE at the default path, and that
///         resolves to the user's real <c>%APPDATA%\DotCharter\logs</c>.
///         <c>LogLevelFilterTests</c> did exactly that until 2026-09-25: every gate
///         run wrote five one-line logs there and appended "seek" / "click" to the
///         newest real session log, and the folder's retention then rotated the
///         owner's actual session logs out. Nothing failed, because nothing asserts
///         on a file nobody reads.
///     </para>
///     <para>
///         The rule is per FILE, not per call: a file that constructs one must also
///         set <c>LogFilePath =</c> or <c>EnableFileLogging = false</c> somewhere.
///         Comments are stripped first, so a doc comment quoting either does not
///         count.
///     </para>
/// </summary>
public sealed class TestLoggerIsolationLintTests
{
    [Fact]
    public void EveryTestThatBuildsAnAppLogger_PointsItsFileAwayFromTheRealLogFolder()
    {
        var testsRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");

        var constructing = new List<string>();
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(testsRoot, path);
            if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith("LocalOnly_", StringComparison.Ordinal) ||
                Path.GetFileName(path) == nameof(TestLoggerIsolationLintTests) + ".cs")
            {
                continue;
            }

            var code = SourceText.StripComments(File.ReadAllText(path));
            if (!Regex.IsMatch(code, @"\bnew\s+AppLogger\s*\(")) continue;

            constructing.Add(rel);
            if (!Regex.IsMatch(code, @"\bLogFilePath\s*=") &&
                !Regex.IsMatch(code, @"\bEnableFileLogging\s*=\s*false\b"))
            {
                offenders.Add(rel);
            }
        }

        // Non-vacuity: the two logger test classes construct one today. If the
        // pattern stops matching, this lint would pass over nothing.
        Assert.True(constructing.Count >= 2,
            $"Expected at least 2 test files constructing an AppLogger, found {constructing.Count}: " +
            string.Join(", ", constructing));
        Assert.True(offenders.Count == 0,
            "These test files build a real AppLogger without setting LogFilePath or " +
            "EnableFileLogging = false, so it writes into the user's real log folder: " +
            string.Join(", ", offenders));
    }
}
