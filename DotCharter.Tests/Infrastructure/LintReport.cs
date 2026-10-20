namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     One shape for a source lint's failure message, so a reader who has never
///     seen the lint knows what broke, where, and what to do:
///     <code>
///     RULE — what must hold, in one sentence
///       path/to/File.cs:42: what this line does wrong
///     Fix: what to change
///     Guide: docs/agents/…/guide.md
///     </code>
///     Name the offender by repo-relative path and line wherever the scan can
///     (<see cref="SourceText.LineAt" />). See <c>DotCharter.Tests/README.md</c>.
/// </summary>
internal static class LintReport
{
    public static string Of(string rule, IEnumerable<string> offenders, string fix, string? guide = null)
    {
        var lines = new List<string> { rule };
        lines.AddRange(offenders.Select(o => "  " + o));
        lines.Add("Fix: " + fix);
        if (guide is not null) lines.Add("Guide: " + guide);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The same shape for one offender (a single file the rule is about).</summary>
    public static string Of(string rule, string offender, string fix, string? guide = null) =>
        Of(rule, [offender], fix, guide);
}
