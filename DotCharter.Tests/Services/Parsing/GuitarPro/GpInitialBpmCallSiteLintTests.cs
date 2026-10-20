namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The opening tempo of an imported score has exactly ONE answer —
///     <c>GpBeatMapBuilder.InitialBpm</c>, which orders the tempo points the same
///     way <c>Build</c> does.
///     <para>
///         The behavioural tests in <c>GpBeatMapBuilderTests</c> pin what that
///         helper returns; they cannot pin that the DIALOGS still call it. Four
///         call sites each spelled the question <c>TempoPoints.FirstOrDefault()</c>
///         — document order — and a fifth is one copy-paste away, so the drift is
///         linted at the source. This is the same shape as
///         <c>ChildProcessEncodingTests</c>: a rule about which expression may
///         appear, which no unit test of the callee can express.
///     </para>
/// </summary>
public sealed class GpInitialBpmCallSiteLintTests
{
    private const string DocumentOrder = "TempoPoints.FirstOrDefault";

    private static readonly string[] Dialogs =
    [
        Path.Combine("DotCharter.Services", "Import", "GuitarPro", "GpImportApplier.cs")
    ];

    [Fact]
    public void NoImportDialogReadsTheOpeningTempoInDocumentOrder()
    {
        var offenders = new List<string>();
        foreach (var relative in Dialogs)
        {
            var path = Path.Combine(TestPaths.RepoRoot(), relative);
            Assert.True(File.Exists(path), $"{relative} moved — repoint this lint.");
            var source = File.ReadAllText(path);

            offenders.AddRange(System.Text.RegularExpressions.Regex.Matches(source, System.Text.RegularExpressions.Regex.Escape(DocumentOrder))
                .Select(m => $"{relative.Replace('\\', '/')}:{SourceText.LineAt(source, m.Index)}: {DocumentOrder}"));
        }

        Assert.True(offenders.Count == 0, LintReport.Of(
            "An import path reads a score's opening tempo in DOCUMENT order instead of asking GpBeatMapBuilder.InitialBpm, " +
            "which orders the tempo points the way the beat map does.",
            offenders,
            "call GpBeatMapBuilder.InitialBpm(…) for the opening tempo.",
            "docs/agents/services/import-tabs-audio.md (GpInitialBpmCallSiteLintTests)"));
    }

    /// <summary>The positive half: each dialog actually ASKS the helper. Without
    /// this, deleting the tempo handling entirely would turn the lint green.</summary>
    [Fact]
    public void EveryImportDialogAsksTheSharedHelper()
    {
        foreach (var relative in Dialogs)
        {
            var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative));

            Assert.Contains("GpBeatMapBuilder.InitialBpm(", source, StringComparison.Ordinal);
        }
    }
}
