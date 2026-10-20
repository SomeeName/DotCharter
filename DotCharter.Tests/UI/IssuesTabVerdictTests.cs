using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The FEHLER tab's footer says two things about the whole project, and both
///     are claims a charter acts on: whether the audio lint has been run, and
///     whether the package would build.
///     <para>
///         <c>Chart Editor.dc.html</c> draws the second as a flat green
///         <c>NO BLOCKERS</c>. That is only ever true AFTER the export validator
///         has run, and this file is what stops the green from being painted
///         before it: the verdict is three gated TextBlocks over the two real
///         flags (<c>HasExportCheckRun</c>, <c>HasExportFindings</c>), and every
///         one of them prints the validator's OWN sentence rather than a
///         hard-coded verdict of its own.
///     </para>
/// </summary>
public sealed class IssuesTabVerdictTests
{
    private static string Markup() =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector",
            "IssuesInspectorView.axaml")));

    [Fact]
    public void TheExportVerdictHasThreeStates_AndEveryOneIsGatedOnARealFlag()
    {
        var markup = Regex.Replace(Markup(), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var verdicts = Regex.Matches(markup, @"<TextBlock Classes=""FooterVerdict[^""]*""[\s\S]*?/>")
            .Select(m => m.Value)
            .ToList();

        Assert.Equal(3, verdicts.Count);

        // Not-checked-yet is the dim one, and it is the ONLY one shown before
        // the check has run.
        Assert.Single(verdicts, v => v.Contains(@"IsVisible=""{Binding !ExportReadiness.HasExportCheckRun}""", StringComparison.Ordinal)
                                     && !v.Contains("FooterVerdict Ok", StringComparison.Ordinal)
                                     && !v.Contains("FooterVerdict Bad", StringComparison.Ordinal));

        var ok = Assert.Single(verdicts, v => v.Contains("FooterVerdict Ok", StringComparison.Ordinal));
        Assert.Contains(@"IsVisible=""{Binding !ExportReadiness.HasExportFindings}""", ok, StringComparison.Ordinal);

        var bad = Assert.Single(verdicts, v => v.Contains("FooterVerdict Bad", StringComparison.Ordinal));
        Assert.Contains(@"IsVisible=""{Binding ExportReadiness.HasExportFindings}""", bad, StringComparison.Ordinal);

        // …and the two coloured ones live inside the HasExportCheckRun panel, so
        // neither can appear before the validator has actually run.
        var gated = Regex.Match(markup,
            @"<Panel IsVisible=""\{Binding ExportReadiness.HasExportCheckRun\}"">([\s\S]*?)</Panel>");
        Assert.True(gated.Success, "the checked-only verdicts are no longer inside a HasExportCheckRun panel");
        Assert.Contains("FooterVerdict Ok", gated.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("FooterVerdict Bad", gated.Groups[1].Value, StringComparison.Ordinal);

        // Every one of them prints the view model's sentence. A literal verdict
        // in the markup is a claim nothing computed.
        foreach (var v in verdicts)
            Assert.Contains(@"Text=""{Binding ExportReadiness.ExportReadinessSummary}""", v, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An issue row's LOCATION is derived from the issue's own time against
    ///     the project's beat map. A single-value binding on <c>TimeMs</c> alone
    ///     could only ever print milliseconds; the bar number needs the grid, so
    ///     the row must keep the <c>MultiBinding</c> that carries both.
    /// </summary>
    [Fact]
    public void TheIssueRowsLocationIsDerivedFromTheRealBeatMap()
    {
        var markup = Markup();

        Assert.Contains("ChartPositionLabelConverter.Instance", markup, StringComparison.Ordinal);
        Assert.Contains(@"<Binding Path=""TimeMs"" />", markup, StringComparison.Ordinal);
        Assert.Contains("ProjectState.Beats", markup, StringComparison.Ordinal);
    }
}
