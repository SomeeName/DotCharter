using DotCharter.Services.Export.Validation;
using DotCharter.UI.ViewModels.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     "Jump to export finding" landed on the right TIME in the wrong
///     ARRANGEMENT — the same defect <c>JumpToIssue</c> already carried one
///     level down.
///     <para>
///         <c>CheckExportReadiness</c> runs <c>ChartExportValidator</c> over the
///         whole PROJECT, so the FEHLER tab's export list holds findings for
///         every arrangement at once, and each row prints the one it belongs to
///         ("Rhythm · 1:23.456", <c>ExportFindingFormatter.FormatLocation</c>).
///         The command only seeked. Clicking a Rhythm row while Lead was open
///         moved the Lead playhead to Rhythm's timestamp, where there is
///         generally nothing wrong at all — the charter is shown a clean bar and
///         told it is the problem.
///     </para>
///     <para>
///         The finding names its arrangement by NAME, and nothing stops a
///         project holding two called "Lead" (a duplicated arrangement keeps its
///         name until it is renamed). An ambiguous name resolves to NOTHING
///         rather than to a guess: seeking without switching is the old
///         behaviour, while switching to the wrong Lead would be a new way to be
///         wrong.
///     </para>
/// </summary>
public sealed class ExportFindingJumpArrangementTests
{
    private static ChartProject Project(params string[] names)
    {
        var project = new ChartProject();
        foreach (var name in names) project.Arrangements.Add(new Arrangement { Name = name });
        return project;
    }

    [Fact]
    public void TheNamedArrangementIsFound()
    {
        var project = Project("Lead", "Rhythm", "Bass");

        Assert.Same(project.Arrangements[1], ExportReadinessViewModel.ArrangementNamed(project, "Rhythm"));
    }

    /// <summary>Trimmed and case-insensitive, because the name is round-tripped
    /// through the finding as free text.</summary>
    [Fact]
    public void TheLookupIsCaseInsensitiveAndTrimmed()
    {
        var project = Project("Lead", "Rhythm");

        Assert.Same(project.Arrangements[1], ExportReadinessViewModel.ArrangementNamed(project, "  rhythm "));
    }

    [Fact]
    public void ANameNoArrangementCarriesResolvesToNothing()
    {
        Assert.Null(ExportReadinessViewModel.ArrangementNamed(Project("Lead"), "Rhythm"));
        Assert.Null(ExportReadinessViewModel.ArrangementNamed(Project("Lead"), ""));
        Assert.Null(ExportReadinessViewModel.ArrangementNamed(null, "Lead"));
    }

    /// <summary>
    ///     Two arrangements called "Lead" — the case the export list cannot tell
    ///     apart, since the finding carries only the name. Answering "the first
    ///     one" would switch the charter to an arrangement the finding may not
    ///     be about.
    /// </summary>
    [Fact]
    public void AnAmbiguousNameResolvesToNothingRatherThanTheFirstMatch() => Assert.Null(ExportReadinessViewModel.ArrangementNamed(Project("Lead", "Lead", "Bass"), "Lead"));

    /// <summary>The row has to CARRY the name for the command to resolve it —
    /// <c>Location</c> is a formatted display string and must not be parsed
    /// back.</summary>
    [Fact]
    public void TheRowCarriesItsArrangementName()
    {
        var item = new ExportValidationItem
        {
            Severity = ExportFindingSeverity.Error,
            Location = "Rhythm · 1:23.456",
            Message = "…",
            ArrangementName = "Rhythm",
            TimeMs = 83456
        };

        Assert.Equal("Rhythm", item.ArrangementName);
    }

    /// <summary>
    ///     The wiring: the jump switches BEFORE it seeks. Linted rather than
    ///     driven here (<c>ExportReadinessViewModelTests</c> drives the jump over a
    ///     real chart editor), comments stripped first — the same shape as
    ///     <c>InspectorJumpToIssueLevelTests</c>' own wiring check.
    /// </summary>
    [Fact]
    public void TheJumpSwitchesArrangementBeforeItSeeks()
    {
        var source = SourceText.StripCommentLines(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor", "Inspector", "ExportReadinessViewModel.cs"))));

        var start = source.IndexOf("public void JumpToExportFinding", StringComparison.Ordinal);
        Assert.True(start > 0, "JumpToExportFinding is still declared here");
        var body = source[start..];
        var end = body.IndexOf("private void RecomputeExportFindings", StringComparison.Ordinal);
        Assert.True(end > 0, "JumpToExportFinding is still followed by RecomputeExportFindings");
        body = body[..end];

        var switchAt = body.IndexOf("ArrangementNamed(", StringComparison.Ordinal);
        var seekAt = body.IndexOf("ExecuteSeek(", StringComparison.Ordinal);

        Assert.True(switchAt > 0, "the jump resolves the finding's arrangement");
        Assert.True(seekAt > 0, "the jump still seeks");
        Assert.True(switchAt < seekAt,
            "the arrangement switch has to happen BEFORE the seek, or the seek lands in the old chart");
    }
}
