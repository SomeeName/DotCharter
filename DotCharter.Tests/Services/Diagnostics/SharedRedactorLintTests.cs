using System.Text;
using System.Text.RegularExpressions;
using DotCharter.Services.Diagnostics;

namespace DotCharter.Tests.Services.Diagnostics;

/// <summary>
///     <para>
///         There is exactly ONE redaction pass in the app —
///         <see cref="SensitiveTextRedactor" /> — and this fails the gate if a
///         second one appears. Modelled on
///         <c>Multiplayer/SharedTruncateLintTests</c>, and for the same reason:
///         the fault it guards compiles perfectly and breaks no test.
///     </para>
///     <para>
///         Until 2026-09-04 there WERE two. <c>DiagnosticsRedactor</c> guarded
///         the diagnostics bundle and <c>CrashReportRedactor</c> the crash
///         bundle, and they had drifted in both directions — the first knew
///         <c>github_pat_…</c>/<c>AKIA…</c> and had no user-name or home-path
///         pass at all, the second had the user-name pass and neither token
///         shape. The bundle the app invites the user to ATTACH TO A BUG REPORT
///         was the one missing the identity pass, and its key/value regex was
///         anchored at the start of a line so no secret inside a Serilog line
///         was ever caught. Two redactors is how that happens.
///     </para>
/// </summary>
public sealed class SharedRedactorLintTests
{
    /// <summary>The single sanctioned implementation.</summary>
    private const string CanonicalFile = "SensitiveTextRedactor.cs";

    /// <summary>
    ///     Anything that declares a redaction entry point. A second redactor
    ///     is a `Redact(` method or a type whose name ends in `Redactor`,
    ///     wherever it lives in the service layer.
    /// </summary>
    private static readonly Regex RedactMethod = new(
        @"\bstatic\s+string\??\s+Redact\s*\(", RegexOptions.Compiled);

    private static readonly Regex RedactorType = new(
        @"\b(?:class|record|struct|interface)\s+\w*Redactor\b", RegexOptions.Compiled);

    [Fact]
    public void NoSecondRedactorExistsUnderTheServiceLayer()
    {
        var root = RepoPath("DotCharter.Services");
        Assert.True(Directory.Exists(root), "DotCharter.Services moved. Repoint this lint.");

        var canonical = 0;
        var offenders = new List<string>();

        foreach (var file in new[] { root, RepoPath("DotCharter.Tone") }
                     .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories)))
        {
            var name = Path.GetFileName(file);
            var src = StripComments(File.ReadAllText(file));
            var hit = RedactMethod.IsMatch(src) || RedactorType.IsMatch(src);
            if (!hit) continue;

            if (string.Equals(name, CanonicalFile, StringComparison.Ordinal)) canonical++;
            else offenders.Add(name);
        }

        // Non-vacuity: the scan must actually SEE the one that is allowed.
        Assert.True(canonical == 1,
            $"expected exactly one {CanonicalFile} declaring Redact, found {canonical} — the scan is broken, not the code");

        Assert.True(offenders.Count == 0,
            "Call SensitiveTextRedactor.Redact instead of writing a second redactor. Found in: "
            + string.Join(", ", offenders.Distinct()));
    }

    /// <summary>
    ///     …and the two artifacts a user is invited to attach both call it. A
    ///     lint on the TYPE alone would pass while a caller quietly stopped
    ///     redacting — which is the shape of the bug this replaced.
    /// </summary>
    [Theory]
    [InlineData("DotCharter.Services/Diagnostics/DiagnosticsBundleExporter.cs")]
    [InlineData("DotCharter.Services/Infrastructure/CrashReporting/CrashReportBuilder.cs")]
    public void EveryUserFacingBundleRunsTheRedactor(string relativePath)
    {
        var file = RepoPath(relativePath);
        Assert.True(File.Exists(file), $"File moved: {relativePath}. Repoint this lint.");

        var src = StripComments(File.ReadAllText(file));
        Assert.True(src.Contains("SensitiveTextRedactor.Redact(", StringComparison.Ordinal), LintReport.Of(
            "A file a user is invited to attach to a bug report must run its text through the shared redactor.",
            relativePath + ": no SensitiveTextRedactor.Redact( call outside comments",
            "pass every log or report string through SensitiveTextRedactor.Redact before it is written.",
            "docs/agents/services/state-diagnostics/checkpoints-and-snapshot.md (SharedRedactorLintTests)"));
    }

    /// <summary>Strips <c>//</c> comments so a `see cref` mention is not a hit.</summary>
    private static string StripComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(idx >= 0 ? line[..idx] : line).Append('\n');
        }

        return sb.ToString();
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
