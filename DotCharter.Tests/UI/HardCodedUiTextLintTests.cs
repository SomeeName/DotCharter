using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <para>
///         A control built in C# rather than markup takes its caption from an
///         assignment, and an assignment takes a string literal without
///         complaint. `{localization:Loc}` cannot help there and neither can
///         <c>LocalizationSweepTests</c>, which walks lookups that EXIST — a
///         string that was never looked up is invisible to it.
///     </para>
///     <para>
///         `Controls/Sheet/SheetMusicView.cs` had five of them (2026-08-29):
///         the strip said "Sheet music…", "Rendering sheet music…", "No
///         arrangement to display", "Sheet music unavailable" and "Sheet music
///         error: …" in English in every language, and the last one was a
///         concatenation, which is the shape a `.Text` search misses if it only
///         looks for a bare literal.
///     </para>
///     <para>
///         Three sites are allowed and each is allowed for a REASON that is not
///         "it is only a short string". Anything else is a caption, and a
///         caption is a resx key.
///     </para>
/// </summary>
public sealed class HardCodedUiTextLintTests
{
    /// <summary>
    ///     `Text = "…"` in an object initializer and `x.Text = "…"` as a
    ///     statement, with or without a concatenation after it. Interpolated
    ///     (<c>$"…"</c>) and verbatim (<c>@"…"</c>) forms are matched too — an
    ///     interpolated caption is still a caption.
    /// </summary>
    private static readonly Regex TextAssignment = new(
        @"(?<![A-Za-z0-9_])Text\s*=\s*[$@]*""[^""]", RegexOptions.Compiled);

    /// <summary>
    ///     <c>file:line-ish marker : reason</c>. Matched on the literal so a
    ///     move within the file does not break the entry, and the FILE is
    ///     asserted to still exist so a rename does.
    /// </summary>
    private static readonly (string File, string Literal, string Reason)[] Allowed =
    [
        (Path.Combine("DotCharter.Tone.UI", "ViewLocator.cs"), "\"Not Found: \"",
            "The DEVELOPER-facing placeholder a view that failed to construct renders as (root AGENTS.md names "
            + "it as one of the four silent XAML faults). It must stay greppable in English; a translated one "
            + "would hide the fault it exists to show."),
    ];

    [Fact]
    public void NoControlBuiltInCode_CarriesAnEnglishCaption()
    {
        var root = TestPaths.RepoRoot();
        var uiRoot = Path.Combine(root, "DotCharter.UI");
        Assert.True(Directory.Exists(uiRoot), "DotCharter.UI is missing — the lint is blind.");

        var offenders = new List<string>();
        var scanned = 0;
        var matched = 0;

        foreach (var file in UiSourceTree.EnumerateFiles(uiRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;
            var relative = Path.GetRelativePath(root, file);

            // Comments stripped — a doc comment quoting the fault is inert, and
            // this repo has already had two lints fail on their own prose.
            var text = SourceText.StripComments(UiSourceTree.ReadAllText(file));

            foreach (Match match in TextAssignment.Matches(text))
            {
                matched++;
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                var literal = LiteralAt(text, match.Index);

                if (Allowed.Any(a => a.File == relative && a.Literal == literal)) continue;

                offenders.Add($"{relative}:{line}  Text = {literal}");
            }
        }

        Assert.True(scanned > 300, $"only scanned {scanned} UI source files — the glob is broken");
        Assert.True(matched >= Allowed.Length,
            $"the pattern matched {matched} assignments but {Allowed.Length} are allow-listed — it stopped matching");

        foreach (var (file, literal, reason) in Allowed)
        {
            Assert.True(File.Exists(Path.Combine(root, file)),
                $"Allow-listed file '{file}' no longer exists — delete the entry ({reason}).");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"Allow-list entry '{file}' carries no reason.");
        }

        Assert.True(offenders.Count == 0,
            "A caption assigned in C# is still a caption: put it in all three resx files and read it through "
            + "LocalizationService.Instance[...] / .Format(...). `{localization:Loc}` cannot reach a control "
            + "built in code, and the sweep that walks lookups cannot see a string nobody looked up:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The literal that starts at or just after <paramref name="from" />, quotes included.</summary>
    private static string LiteralAt(string text, int from)
    {
        var open = text.IndexOf('"', from);
        if (open < 0) return "\"?\"";
        var i = open + 1;
        while (i < text.Length && text[i] != '"')
            i += text[i] == '\\' ? 2 : 1;
        return text[open..Math.Min(i + 1, text.Length)];
    }
}
