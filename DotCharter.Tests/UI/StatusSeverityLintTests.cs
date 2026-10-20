using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The status rail's one line carries a <c>StatusSeverity</c> since the
///     failure paths were made distinguishable from success — and a severity
///     nobody sets is the same faint string it replaced. These lints walk the
///     REAL product source so a new writer cannot quietly regress the rail:
///     <list type="bullet">
///         <item>a status write inside a <c>catch</c> block must carry
///         <c>StatusSeverity.Error</c> (a catch is, by construction, the
///         operation NOT happening);</item>
///         <item>a write whose localisation key says <c>…Failed</c> /
///         <c>…Error</c> must too, wherever it sits — the key already promised
///         the user a failure.</item>
///     </list>
///     Modelled on <see cref="PowerToolReachabilityTests" />: comments stripped first,
///     both <c>DotCharter.UI</c> and <c>DotCharter.Services</c> scanned, and a
///     keeper that the sweep still finds writers at all.
/// </summary>
public sealed class StatusSeverityLintTests
{
    /// <summary>The two writer shapes: <c>x.SetMessage(</c> and
    /// <c>x.MainDisplayMessage =</c> (not <c>==</c>). Only <c>ProjectState</c>
    /// declares a <c>SetMessage</c> in the product, so no receiver check is
    /// needed.</summary>
    private static readonly Regex StatusWrite = new(
        @"\bSetMessage\s*\(|\bMainDisplayMessage\s*=(?!=)",
        RegexOptions.Compiled);

    private static readonly Regex CatchKeyword = new(@"\bcatch\b", RegexOptions.Compiled);

    private static readonly Regex FailureKey = new(
        @"""[A-Za-z0-9_]*(?:Failed|Error)""", RegexOptions.Compiled);

    [Fact]
    public void EveryStatusWriteInsideACatchBlock_IsMarkedError()
    {
        var offenders = new List<string>();
        var writesSeen = 0;

        foreach (var file in ProductSources())
        {
            var code = SourceText.StripCommentLines(UiSourceTree.ReadAllText(file));
            foreach (var (clause, body) in CatchBodies(code))
            {
                // A cancel is the user's own doing, not a failure: a catch that
                // names a *Canceled* exception may write Warning instead.
                var isCancel = clause.Contains("Canceled", StringComparison.Ordinal);
                foreach (var statement in StatusWriteStatements(body))
                {
                    writesSeen++;
                    var ok = statement.Contains("StatusSeverity.Error", StringComparison.Ordinal) ||
                             (isCancel && statement.Contains("StatusSeverity.Warning", StringComparison.Ordinal));
                    if (!ok) offenders.Add($"{Relative(file)}: {Collapse(statement)}");
                }
            }
        }

        // Keeper: the product really does write the rail from catch blocks. If
        // this ever reaches zero the regexes stopped matching, not the code.
        Assert.True(writesSeen >= 20,
            $"only {writesSeen} status writes found inside catch blocks — the lint's matcher has gone blind");

        Assert.True(offenders.Count == 0,
            "A status write inside a catch block must say so — SetMessage(text, StatusSeverity.Error) — " +
            "or the failed save reads exactly like a finished one on the rail:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryStatusWriteOfAFailureKey_IsMarkedError()
    {
        var offenders = new List<string>();
        var writesSeen = 0;

        foreach (var file in ProductSources())
        {
            var code = SourceText.StripCommentLines(UiSourceTree.ReadAllText(file));
            foreach (var statement in StatusWriteStatements(code))
            {
                if (!FailureKey.IsMatch(statement)) continue;
                writesSeen++;
                // Error, or Warning where the operation still half-landed (a
                // project created without its audio) — never the plain default.
                var ok = statement.Contains("StatusSeverity.Error", StringComparison.Ordinal) ||
                         statement.Contains("StatusSeverity.Warning", StringComparison.Ordinal);
                if (!ok) offenders.Add($"{Relative(file)}: {Collapse(statement)}");
            }
        }

        Assert.True(writesSeen >= 20,
            $"only {writesSeen} status writes of a *Failed / *Error key found — the lint's matcher has gone blind");

        Assert.True(offenders.Count == 0,
            "A status write whose key promises a failure (…Failed / …Error) must carry StatusSeverity.Error " +
            "(or Warning for a partial result):\n  " +
            string.Join("\n  ", offenders));
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>Every <c>{ … }</c> body that follows a <c>catch</c> keyword,
    /// skipping the optional <c>(Type e)</c> and <c>when (…)</c> clauses.
    /// Nested catches are yielded on their own AND inside their parent — a
    /// write is checked wherever it sits.</summary>
    private static IEnumerable<(string Clause, string Body)> CatchBodies(string code)
    {
        foreach (Match m in CatchKeyword.Matches(code))
        {
            var start = m.Index + m.Length;
            var i = start;
            // Skip whitespace, `(…)` and `when (…)` up to the opening brace.
            while (i < code.Length && code[i] != '{')
            {
                if (code[i] == '(')
                {
                    i = SkipBalanced(code, i, '(', ')');
                }
                else if (code[i] == ';') { i = -1; break; } // not a block catch
                else
                {
                    i++;
                }
            }
            if (i < 0 || i >= code.Length) continue;
            var end = SkipBalanced(code, i, '{', '}');
            yield return (code[start..i], code.Substring(i + 1, Math.Max(0, end - i - 2)));
        }
    }

    /// <summary>Each status write and the rest of its statement, up to the `;`
    /// that closes it at brace/paren depth zero.</summary>
    private static IEnumerable<string> StatusWriteStatements(string code)
    {
        foreach (Match m in StatusWrite.Matches(code))
        {
            var start = m.Index;
            var depth = 0;
            var i = start;
            for (; i < code.Length; i++)
            {
                var c = code[i];
                if (c == '"') { i = SkipString(code, i) - 1; continue; }
                if (c is '(' or '{' or '[') depth++;
                else if (c is ')' or '}' or ']') depth--;
                else if (c == ';' && depth <= 0) break;
            }
            yield return code.Substring(start, Math.Min(code.Length, i + 1) - start);
        }
    }

    private static int SkipBalanced(string code, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"') { i = SkipString(code, i) - 1; continue; }
            if (c == open) depth++;
            else if (c == close && --depth == 0) return i + 1;
        }
        return code.Length;
    }

    /// <summary>Skips a regular, verbatim or interpolated string literal
    /// starting at <paramref name="quoteIndex" />; returns the index after it.
    /// Good enough for the product's literals (no raw strings on these paths).</summary>
    private static int SkipString(string code, int quoteIndex)
    {
        var verbatim = quoteIndex > 0 && (code[quoteIndex - 1] == '@' ||
                                          (quoteIndex > 1 && code[quoteIndex - 2] == '@' && code[quoteIndex - 1] == '$'));
        for (var i = quoteIndex + 1; i < code.Length; i++)
        {
            if (code[i] == '\\' && !verbatim) { i++; continue; }
            if (code[i] == '"')
            {
                if (verbatim && i + 1 < code.Length && code[i + 1] == '"') { i++; continue; }
                return i + 1;
            }
        }
        return code.Length;
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static IEnumerable<string> ProductSources()
    {
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone" })
        {
            var root = Path.Combine(TestPaths.RepoRoot(), project);
            foreach (var file in UiSourceTree.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string Relative(string file) => Path.GetRelativePath(TestPaths.RepoRoot(), file);
}
