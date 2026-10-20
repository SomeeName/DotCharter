using System.Text.RegularExpressions;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Nothing under <c>DotCharter.Rendering</c> re-derives whether a chord shows
///     as the frame-only repeat box (#487). The answer is the EXPORT's
///     (<c>Services/Export/Rocksmith/RepeatChords</c> over <c>RsChordDensity</c>),
///     handed to the renderers as data — <c>SnapshotUpdateContext.RepeatChords</c>
///     → <c>ChordFlags.Repeat</c>. Rendering references Core only and cannot call
///     that rule, so a second answer here would be a second ALGORITHM: that is
///     exactly what <c>ChordRenderer.HasPriorMatchingChord</c> (a port of
///     RsCharter's <c>Level.shouldChordShowNotes</c>) was, and the preview
///     disagreed with the game.
///     <para>
///         Three shapes of re-derivation are refused, each with a probe below on a
///         known-bad input: a chord-to-chord TEMPLATE comparison (how a repeat is
///         recognised), a named repeat/density decider, and any write of
///         <c>ChordFlags.Repeat</c> other than the snapshot's one lookup.
///     </para>
/// </summary>
public sealed partial class RepeatChordSourceLintTests
{
    [Fact]
    public void NoRenderingSource_ComparesTemplateIds()
    {
        var offenders = RenderingSources()
            .SelectMany(f => TemplateComparisons(f.Code).Select(m => $"{f.Path}: {m}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Rendering compares chord template ids — re-deriving repeat state? Use ChordFlags.Repeat: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void NoRenderingSource_NamesARepeatDecider()
    {
        var offenders = RenderingSources()
            .SelectMany(f => DeciderNames(f.Code).Select(m => $"{f.Path}: {m}"))
            .ToList();

        Assert.True(offenders.Count == 0, "a repeat/density decider in Rendering: " + string.Join("; ", offenders));
    }

    [Fact]
    public void TheRepeatFlag_IsWrittenOnlyFromTheContextsRepeatSet()
    {
        var writes = RenderingSources()
            .SelectMany(f => RepeatFlagWrites(f.Code).Select(line => (f.Path, Line: line)))
            .ToList();

        var only = Assert.Single(writes);
        Assert.EndsWith("FrameSnapshot.Notes.cs", only.Path, StringComparison.Ordinal);
        Assert.Contains("RepeatChords.Contains(", only.Line, StringComparison.Ordinal);
    }

    // ── probes: the matchers catch what they claim to ───────────────────────

    [Theory]
    [InlineData("return prev.TemplateId == templateId;")]
    [InlineData("if (chord.TemplateId != previous.TemplateId) continue;")]
    [InlineData("var same = templateId == snapshot.Chords[k].TemplateId;")]
    public void Probe_ATemplateComparisonIsCaught(string code) => Assert.NotEmpty(TemplateComparisons(code));

    [Theory]
    [InlineData("tplHs.TemplateId >= 0 && tplHs.TemplateId < context.Snapshot.TemplateCount")]
    [InlineData("TemplateId = c.TemplateId,")]
    public void Probe_ARangeCheckOrACopyIsNot(string code) => Assert.Empty(TemplateComparisons(code));

    [Theory]
    [InlineData("internal static bool HasPriorMatchingChord(FrameSnapshot s, int i)")]
    [InlineData("var compact = ChordDensity.Compute(level);")]
    [InlineData("// shouldChordShowNotes\nbool shouldChordShowNotes = true;")]
    public void Probe_ADeciderIsCaught(string code) => Assert.NotEmpty(DeciderNames(code));

    [Fact]
    public void Probe_ADeciderNamedOnlyInACommentIsNot() =>
        Assert.Empty(DeciderNames("// was RsCharter's shouldChordShowNotes\nvar x = 1;"));

    [Theory]
    [InlineData("cFlags |= ChordFlags.Repeat;")]
    [InlineData("chord.Flags = ChordFlags.InHandShape | ChordFlags.Repeat;")]
    public void Probe_ARepeatFlagWriteIsCaught(string code) => Assert.NotEmpty(RepeatFlagWrites(code));

    [Fact]
    public void Probe_AReadIsNotAWrite() =>
        Assert.Empty(RepeatFlagWrites("var isRepeat = (chord.Flags & ChordFlags.Repeat) != 0;"));

    // ── the matchers ─────────────────────────────────────────────────────────

    internal static IEnumerable<string> TemplateComparisons(string code) =>
        TemplateIdEquality().Matches(SourceText.StripComments(code)).Select(m => m.Value);

    internal static IEnumerable<string> DeciderNames(string code) =>
        DeciderName().Matches(SourceText.StripComments(code)).Select(m => m.Value);

    internal static IEnumerable<string> RepeatFlagWrites(string code) =>
        SourceText.StripComments(code).Split('\n')
            .Where(line => RepeatFlagWrite().IsMatch(line))
            .Select(line => line.Trim());

    private static IEnumerable<(string Path, string Code)> RenderingSources()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 50, $"only {files.Count} Rendering sources found; the scan is broken");
        return files.Select(f => (Path.GetRelativePath(root, f), File.ReadAllText(f)));
    }

    /// <summary><c>TemplateId ==</c>, <c>TemplateId !=</c>, or either with the
    /// <c>TemplateId</c> on the right.</summary>
    [GeneratedRegex(@"\bTemplateId\s*[!=]=|[!=]=\s*[\w.\[\]()]*\bTemplateId\b")]
    private static partial Regex TemplateIdEquality();

    [GeneratedRegex(@"\b\w*(PriorMatchingChord|ChordShowNotes|ChordDensity|HighDensity|CompactRepeat)\w*\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeciderName();

    /// <summary>An OR-assignment of the flag, or an assignment whose value names it.</summary>
    [GeneratedRegex(@"\|=\s*[^;]*ChordFlags\.Repeat\b|(?<![=!<>&|])=(?!=)[^;]*ChordFlags\.Repeat\b(?![^;]*\)\s*[!=]=)")]
    private static partial Regex RepeatFlagWrite();
}
