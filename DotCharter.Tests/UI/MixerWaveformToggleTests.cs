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
        var tracks = markup.IndexOf("StemMixer.Tracks", StringComparison.Ordinal);

        Assert.True(stems >= 0 && toggle > stems,
            "the toggle must sit inside the stems section, which is hidden when there are no stems");
        Assert.True(toggle < tracks, "the toggle belongs above the strips — it governs all of them");
    }

    /// <summary>
    ///     <c>StemMixerView</c> is NOT hosted, and this records it so the next
    ///     agent does not put a second feature there.
    ///     <para>
    ///         Reachability here is by NAMING CONVENTION, not by a reference:
    ///         <c>StemMixerViewModel</c> derives from <c>ViewModelBase</c> with a
    ///         comment saying it does so "the app ViewLocator can resolve
    ///         StemMixerView when this VM is a dock pane Context" — but nothing
    ///         gives it to a dock pane, so the locator is never asked. The VM is
    ///         reached only as <c>InspectorViewModel.StemMixer</c>, whose view is
    ///         the OTHER mixer. That is why a grep for the type finds nothing and
    ///         the view still cannot be opened.
    ///     </para>
    ///     <para>
    ///         If someone wires it into a window or a docked pane, this test goes
    ///         red — and that is the moment to decide whether the two mixers
    ///         should still both exist, rather than discovering it from a user
    ///         who cannot find a checkbox.
    ///     </para>
    /// </summary>
    [Fact]
    public void StemMixerView_IsStillHostedByNothingInTheApp()
    {
        var ui = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var hosts = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(ui, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ext is not (".cs" or ".axaml")) continue;
            if (file.EndsWith("StemMixerView.axaml", StringComparison.Ordinal)) continue;
            if (file.EndsWith("StemMixerView.axaml.cs", StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            // The TYPE, not the view-model that merely shares its prefix — and
            // not PROSE. Comments come out first: three of the four hits this
            // found on its first run were sentences ABOUT the orphan (including
            // one written in the same change), and AGENTS.md records the same
            // rule for the co-op reachability lints — "a see cref is exactly as
            // inert".
            if (ViewTypeReference.IsMatch(StripComments(UiSourceTree.ReadAllText(file))))
                hosts.Add(Path.GetRelativePath(TestPaths.RepoRoot(), file));
        }

        Assert.True(hosts.Count == 0,
            "StemMixerView is now referenced by production code: " + string.Join(", ", hosts) +
            ". It used to be hosted by nothing, which is how the waveform toggle shipped invisible. " +
            "If it is genuinely a screen now, give it the toggle too — or delete the duplicate mixer.");

        Assert.True(File.Exists(RepoPath(OrphanView)), "the orphan view is gone — retire this test with it");
    }

    /// <summary>Drops C# and XML comments, so prose about the orphan does not
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
