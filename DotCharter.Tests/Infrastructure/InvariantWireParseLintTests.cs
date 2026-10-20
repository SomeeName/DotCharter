using System.Globalization;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <para>
///         Every numeric <c>Parse</c> / <c>TryParse</c> on the FILE and WIRE
///         layers passes an explicit culture. The layers are
///         <c>DotCharter.Services/Parsing/</c>,
///         <c>DotCharter.Services/Export/</c> and the two Core primitives every
///         chart position is made of
///         (<c>DotCharter.Core/Domain/Primitives/</c>) — a `.rscp`, a `.gp`, an
///         `.xml`, an `.lrc` and an `.eof` are machine formats, so the only
///         correct reading of one is <see cref="CultureInfo.InvariantCulture" />.
///     </para>
///     <para>
///         The overload without a provider reads
///         <see cref="CultureInfo.CurrentCulture" />, and the part of it that
///         bites is not the decimal separator — <c>int.TryParse</c> never
///         accepts one — it is
///         <see cref="NumberFormatInfo.NegativeSign" />. Under sv-SE, fi-FI,
///         nb-NO, lt-LT and sv-FI that sign is U+2212 MINUS SIGN, so
///         <c>"-2"</c> — the low string of every Drop-D tuning DotCharter has
///         ever written — does not parse at all. The call sites do not throw:
///         they fall back to 0, to <c>-1</c>, to "E standard". A whole
///         arrangement re-tunes itself and nothing is logged.
///     </para>
///     <para>
///         98 sites across 17 files were missing it when this lint was written
///         (2026-08-29); the audit that preceded it checked only FLOAT parsing
///         and reported the layer clean. The write half is not linted here —
///         a regex cannot tell a numeric <c>.ToString()</c> from a string one —
///         it is covered behaviourally by
///         <c>Localization/InvariantWireFormatTests</c>, which writes a whole
///         `.rscp` under five cultures and compares the documents.
///     </para>
/// </summary>
public sealed class InvariantWireParseLintTests
{
    private static readonly string[] ScannedRoots =
    [
        Path.Combine("DotCharter.Services", "Parsing"),
        Path.Combine("DotCharter.Services", "Export"),
        Path.Combine("DotCharter.Core", "Domain", "Primitives")
    ];

    /// <summary>
    ///     The numeric types whose <c>Parse</c> reads the ambient culture.
    ///     <c>bool</c>, <c>Guid</c>, <c>Enum</c> and <c>TimeSpan</c> are absent
    ///     deliberately — none of them consults a
    ///     <see cref="NumberFormatInfo" />. <c>DateTime</c> is absent for the
    ///     opposite reason: it needs a culture AND a format, so a bare provider
    ///     would not be the fix, and none of these folders parses one.
    /// </summary>
    private static readonly Regex NumericParse = new(
        @"\b(?:int|long|short|byte|sbyte|uint|ulong|ushort|nint|nuint|float|double|decimal|"
        + @"Int16|Int32|Int64|UInt16|UInt32|UInt64|Byte|SByte|Single|Double|Decimal)"
        + @"\.(?:Try)?Parse\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     Sites that read the ambient culture ON PURPOSE. Each entry is
    ///     <c>file : reason</c> and the test asserts the FILE still exists, so a
    ///     rename breaks the exclusion instead of silently widening it. Empty
    ///     today — and an empty allow-list is the honest state for a machine
    ///     format. Do not add a row to turn a red build green: if a parser in
    ///     these folders really wants the user's culture, it is reading text a
    ///     HUMAN typed, and that belongs in a view model, not a format reader.
    /// </summary>
    private static readonly (string File, string Reason)[] Allowed = [];

    [Fact]
    public void EveryNumericParse_OnTheFileAndWireLayers_PassesACulture()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var scannedFiles = 0;
        var checkedCalls = 0;

        foreach (var relative in ScannedRoots)
        {
            var directory = Path.Combine(root, relative);
            Assert.True(Directory.Exists(directory), $"Scanned root '{relative}' does not exist — the lint is blind.");

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                scannedFiles++;

                var relativeFile = Path.GetRelativePath(root, file);
                if (Allowed.Any(a => a.File == relativeFile.Replace('\\', '/'))) continue;

                var text = SourceText.StripComments(File.ReadAllText(file));
                foreach (Match match in NumericParse.Matches(text))
                {
                    checkedCalls++;
                    var open = text.IndexOf('(', match.Index);
                    var close = MatchingParen(text, open);
                    if (close < 0) continue;

                    var arguments = text[(open + 1)..close];
                    if (arguments.Contains("Culture", StringComparison.Ordinal) ||
                        arguments.Contains("FormatProvider", StringComparison.Ordinal) ||
                        arguments.Contains("NumberFormatInfo", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    offenders.Add($"{relativeFile}:{LineOf(text, match.Index)}  {Squash(match.Value + arguments)}");
                }
            }
        }

        Assert.True(scannedFiles > 110, $"only scanned {scannedFiles} files — the glob is broken (138 on 2026-08-29)");
        Assert.True(checkedCalls > 100, $"only found {checkedCalls} numeric parse calls — the pattern stopped matching (126 on 2026-08-29)");

        foreach (var (file, reason) in Allowed)
        {
            Assert.True(File.Exists(Path.Combine(root, file)),
                $"Allow-listed file '{file}' no longer exists — delete the entry ({reason}).");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"Allow-list entry '{file}' carries no reason.");
        }

        Assert.True(offenders.Count == 0,
            "A machine format must be read with CultureInfo.InvariantCulture. Under sv-SE / fi-FI / nb-NO the "
            + "negative sign is U+2212, so these calls silently fail on every negative value and fall back to a "
            + "default (a Drop-D tuning reads as E standard). Add the provider — .NET 7+ has a "
            + "(string, IFormatProvider, out T) overload on every one of these types:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Squash(string call)
    {
        var flat = Regex.Replace(call, @"\s+", " ").Trim();
        return flat.Length <= 110 ? flat + ")" : flat[..110] + "…)";
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
