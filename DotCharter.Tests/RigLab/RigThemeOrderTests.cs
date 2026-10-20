using System.Text.RegularExpressions;

namespace DotCharter.Tests.RigLab;

/// <summary>
///     The rig draws in two hosts, and it must draw the same in both: the
///     rig-only theme (<c>DotCharter.Tone.UI/Styles/RigTheme.axaml</c>, RigLab)
///     merges exactly the design-system files DotCharter's
///     <c>Theme.axaml</c> merges from that assembly, in the SAME order — a later
///     dictionary wins, so a reordering is a restyle.
/// </summary>
public sealed class RigThemeOrderTests
{
    private static readonly Regex ToneUiInclude = new(
        "<StyleInclude\\s+Source=\"avares://DotCharter\\.Tone\\.UI/(?<path>[^\"]+)\"", RegexOptions.Compiled);

    private static IReadOnlyList<string> ToneUiIncludes(params string[] relative) =>
        [.. ToneUiInclude.Matches(File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. relative])))
            .Select(m => m.Groups["path"].Value)];

    [Fact]
    public void RigTheme_MergesTheAppsDesignSystem_InTheAppsOrder()
    {
        var app = ToneUiIncludes("DotCharter.UI", "Styles", "Theme.axaml");
        var rig = ToneUiIncludes("DotCharter.Tone.UI", "Styles", "RigTheme.axaml");

        // Non-vacuity: both really merge the design system.
        Assert.Contains("Styles/Fire.axaml", app);
        Assert.True(app.Count >= 8, $"Theme.axaml names only {app.Count} Tone.UI includes");

        Assert.Equal(app, rig);
    }
}
