using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The "waveform follows mix" toggle has to live in the view the APP SHOWS.
///     <para>
///         It did not. The feature shipped into <c>StemMixerView.axaml</c>, which
///         nothing in the application hosts — the only references to that type
///         are in the test project. The owner opened the mixer, found no
///         checkbox, and asked where it was. Every test passed the whole time,
///         including a construction-and-layout smoke test, because the view
///         builds perfectly well; it is simply never shown.
///     </para>
///     <para>
///         This is the same shape AGENTS.md records for the two pedalboards and
///         for the registered-but-unresolved services: <b>a thing that compiles,
///         tests green and is reachable by nobody reads as shipped.</b> The
///         mixer the Inspector actually renders is
///         <c>Views/Editor/Inspector/MixerInspectorView.axaml</c>.
///     </para>
/// </summary>
public sealed class MixerWaveformToggleTests
{
    /// <summary><c>StemMixerView</c> but never <c>StemMixerViewModel</c>.</summary>
    private static readonly Regex ViewTypeReference = new(@"StemMixerView(?!Model)", RegexOptions.Compiled);

    private const string HostedView = "DotCharter.UI/Views/Editor/Inspector/MixerInspectorView.axaml";
    private const string OrphanView = "DotCharter.UI/Views/Editor/StemMixerView.axaml";

    /// <summary>The toggle is in the mixer the Inspector renders.</summary>
    [Fact]
    public void TheToggleIsInTheMixerTheAppActuallyShows()
    {
        var markup = UiSourceTree.ReadAllText(RepoPath(HostedView));

        Assert.Contains("StemMixer.WaveformFollowsMix", markup, StringComparison.Ordinal);
        Assert.Contains("Mixer_WaveformFollowsMix", markup, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and it is inside the stems section, so it disappears with the stems
    ///     rather than offering to follow a mix that does not exist.
    /// </summary>
    [Fact]
    public void TheToggleSitsUnderTheStemsDisclosure()
    {
        var markup = UiSourceTree.ReadAllText(RepoPath(HostedView));

        var stems = markup.IndexOf("x:Name=\"MixerStemsBody\"", StringComparison.Ordinal);
        var toggle = markup.IndexOf("StemMixer.WaveformFollowsMix", StringComparison.Ordinal);
        var routing = markup.IndexOf("x:Name=\"StemRoutingCard\"", StringComparison.Ordinal);
        var nextSection = markup.IndexOf("x:Name=\"MixerEqBody\"", StringComparison.Ordinal);

        Assert.True(stems >= 0 && toggle > stems,
            "the toggle must sit inside the stems section, which is hidden when there are no stems");
        // Design D: it governs every strip, so it is on the section's Routing card, not on one strip.
        Assert.True(routing > stems && toggle > routing && toggle < nextSection, "the toggle belongs on the Stems section's Routing card");
    }

    /// <summary>
    ///     The unhosted duplicate <c>StemMixerView</c> was deleted (#497): the one
    ///     mixer is the Inspector's Mixer tab. This keeps it that way. The view
    ///     files and its three strings stay gone, nothing in the app names the view
    ///     type again, and the View &gt; Stem mixer row still lands on the Mixer tab.
    /// </summary>
    [Fact]
    public void TheDuplicateStemMixerView_StaysDeleted_AndTheMenuRowReachesTheMixerTab()
    {
        Assert.False(File.Exists(RepoPath(OrphanView)), "the duplicate StemMixerView.axaml is back");
        Assert.False(File.Exists(RepoPath(OrphanView + ".cs")), "the duplicate StemMixerView code-behind is back");

        var ui = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var hosts = new List<string>();
        foreach (var file in UiSourceTree.EnumerateFiles(ui, "*.*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) is not (".cs" or ".axaml")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            if (ViewTypeReference.IsMatch(StripComments(UiSourceTree.ReadAllText(file))))
                hosts.Add(Path.GetRelativePath(TestPaths.RepoRoot(), file));
        }

        Assert.True(hosts.Count == 0, "StemMixerView is referenced again: " + string.Join(", ", hosts));

        foreach (var culture in new[] { "", ".de-DE", ".ja-JP" })
        {
            var resx = File.ReadAllText(RepoPath($"DotCharter.Localization/Resources{culture}.resx"));
            foreach (var key in RetiredKeys)
                Assert.DoesNotContain($"name=\"{key}\"", resx, StringComparison.Ordinal);
        }

        // The window answers IsStemMixerVisible with the mixer tab; the Inspector tab menu row opens the same tab.
        var window = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/MainWindow.axaml.cs"));
        Assert.Matches(@"IsStemMixerVisible\)\s*vm\.ShowInspectorTab\(InspectorViewModel\.MixerTabIndex\)", window);
        var menu = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Shell/Menus/ViewMenu.axaml"));
        Assert.Contains("Inspector_Tab_Mixer", menu, StringComparison.Ordinal);
        Assert.Contains("TabMenu.ShowRowCommand", menu, StringComparison.Ordinal);
    }

    private static readonly string[] RetiredKeys =
        ["Empty_StemMixer_Hint", "Panel_StemMixer_Title", "Panel_StemMixer_Volume"];

    /// <summary>Drops C# and XML comments, so prose about the old view does not
    /// read as a use of it.</summary>
    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^
]*", "");
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
