using System.Text.RegularExpressions;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     <c>RscpHeaderParser</c> reads its thirty header elements through
///     <see cref="RscpFirstElementIndex" />, which answers ONLY the names it was
///     built with (<c>RscpHeaderParser.HeaderElementNames</c>).
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the guard that makes that safe.</b> A name added to a
///         <c>header.First("x")</c> call but not to the array does not throw and
///         does not warn — the lookup simply returns null, i.e. the field reads
///         as ABSENT from every project. That is silent data loss on load, and
///         then on the next save, which is exactly the failure mode the
///         "a model field with no writer is deleted" rule exists for. It is a
///         hand-maintained PARALLEL LIST, so it gets a mechanical check.
///     </para>
///     <para>
///         The scan is over SOURCE rather than reflection because the names are
///         string literals at their call sites; there is nothing to reflect over.
///         Comments are stripped first, for the same reason the reachability
///         lints strip them: a name mentioned in prose is inert.
///     </para>
/// </remarks>
public sealed class RscpHeaderIndexCoverageTests
{
    private const string MissingSource =
        "RscpHeaderParser.cs was not found from the test binary — the repo layout moved.";

    private static string? FindParserSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "DotCharter.Services", "Parsing", "Rscp", "RscpHeaderParser.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public void EveryHeaderNameLookedUpInSource_IsInHeaderElementNames()
    {
        var path = FindParserSource();
        Assert.SkipWhen(path == null, MissingSource);

        var source = SourceText.StripComments(File.ReadAllText(path!));
        var used = Regex.Matches(source, @"header\.(?:First|Value)\(""([^""]+)""\)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Non-vacuous: the pattern must still match the real call sites.
        Assert.True(used.Count >= 25,
            $"Only {used.Count} header.First(\"...\") call sites matched in {path}. " +
            "The parser was rewritten and this lint is now scanning for a shape that is gone — " +
            "repoint it rather than deleting it.");

        var declared = RscpHeaderParser.HeaderElementNames.ToHashSet(StringComparer.Ordinal);
        var missing = used.Except(declared).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            $"RscpHeaderParser looks up {missing.Count} element name(s) that RscpFirstElementIndex " +
            $"was never asked to index: {string.Join(", ", missing)}. Each reads as ABSENT from every " +
            "project — add them to RscpHeaderParser.HeaderElementNames.");
    }

    [Fact]
    public void HeaderElementNames_HasNoDuplicatesAndNoDeadEntries()
    {
        var names = RscpHeaderParser.HeaderElementNames;
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());

        var path = FindParserSource();
        Assert.SkipWhen(path == null, MissingSource);

        var source = SourceText.StripComments(File.ReadAllText(path!));
        var used = Regex.Matches(source, @"header\.(?:First|Value)\(""([^""]+)""\)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // A name in the array that nothing looks up costs a slot in the early-exit
        // condition: the walk cannot stop until it has seen a name the file may
        // never carry, so a dead entry silently turns the fast path back into a
        // full-document scan.
        var dead = names.Where(n => !used.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(dead.Count == 0,
            $"HeaderElementNames carries {dead.Count} name(s) no lookup uses: {string.Join(", ", dead)}. " +
            "A dead entry keeps RscpFirstElementIndex.Build from ever hitting its early exit.");
    }
}
