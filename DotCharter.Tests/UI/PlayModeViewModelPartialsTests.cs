using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>PlayModeViewModel</c> was 5,504 lines / 251 KB in one class — 69
///     commands, 199 <c>[ObservableProperty]</c> declarations, no partials, no
///     regions, the largest file in the repo — and 0.41 split it into five
///     responsibility-named partials, the shape
///     <c>JuceAudioPlaybackService</c> (six) and <c>ChartMaintenanceService</c>
///     (four) already use.
///     <para>
///         A partials split is compile-time only, so <b>nothing about the app
///         would fail if it silently rotted back</b>: a new field dropped into a
///         partial, or a partial quietly deleted and its members folded home,
///         both build perfectly clean. That is exactly the shape of change this
///         file exists to catch. It is a SOURCE LINT for the same reason
///         <c>PenEscapeReachabilityTests</c> and <c>StartupWorkDeferralTests</c>
///         are: constructing the view model needs the whole app booted, and the
///         claim being made is about the FILES.
///     </para>
/// </summary>
public sealed class PlayModeViewModelPartialsTests
{
    /// <summary>The siblings, each named after the responsibility it owns. Listen-back
    /// and the results board are collaborators (<c>RunListenBackViewModel</c>,
    /// <c>RunResultsViewModel</c>), not partials; <c>.Results</c> keeps the run exit.</summary>
    public static TheoryData<string> Partials =>
    [
        "Screens",
        "Results"
    ];

    /// <summary>
    ///     A field declaration at class level: four-space indent, an access
    ///     modifier, a type, a name, an optional initializer, a semicolon. An
    ///     auto-property (<c>{ get; }</c>) cannot match — the type group forbids
    ///     <c>;</c> — and an expression member is excluded explicitly.
    /// </summary>
    private static readonly Regex FieldDeclaration = new(
        @"^    (private|internal|public|protected)(?<mods>(\s+(static|readonly|volatile|const))*)\s+" +
        @"(?<type>[^=;()]+?)\s+(?<name>[A-Za-z_]\w*)\s*(=[^;]*)?;\s*$",
        RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(Partials))]
    public void Each_named_partial_exists_and_extends_the_same_class(string partial)
    {
        var source = PartialSource(partial);

        Assert.Contains("public sealed partial class PlayModeViewModel", source, StringComparison.Ordinal);
        Assert.Contains("namespace DotCharter.UI.ViewModels.PlayMode;", source, StringComparison.Ordinal);
        // A partial that has shrunk to nothing is a partial somebody folded home.
        // `.Results` is ~140 lines: entering the screen and the run-exit verbs.
        Assert.True(source.Split('\n').Length > 100,
            $"PlayModeViewModel.{partial}.cs has shrunk to almost nothing — was its content folded back?");
    }

    /// <summary>
    ///     <b>The field rule.</b> Injected dependencies and plain private fields
    ///     live in the ROOT, in one block, so "where does this come from" stays a
    ///     look rather than a search — the same contract
    ///     <c>JuceAudioPlaybackService</c> states. Backing fields carrying
    ///     <c>[ObservableProperty]</c> are exempt: the generator turns each into a
    ///     public property, so it declares that screen's bound surface rather than
    ///     shared state, and parking 199 of them in the root would put every
    ///     screen's public API in a file that owns none of it. <c>const</c>s are
    ///     exempt too — a compile-time constant belongs with the algorithm that
    ///     reads it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Partials))]
    public void No_partial_declares_state_of_its_own(string partial)
    {
        var lines = Joined(PartialSource(partial).Replace("\r\n", "\n").Split('\n'));
        var offenders = new List<string>();

        for (var i = 0; i < lines.Count; i++)
        {
            var match = FieldDeclaration.Match(lines[i]);
            if (!match.Success || lines[i].Contains("=>", StringComparison.Ordinal)) continue;
            if (match.Groups["mods"].Value.Contains("const", StringComparison.Ordinal)) continue;
            if (CarriesObservableProperty(lines, i)) continue;

            offenders.Add(lines[i].Trim());
        }

        Assert.True(offenders.Count == 0,
            $"PlayModeViewModel.{partial}.cs declares state that belongs in the root file:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The other half of the same rule: the root is where the constructor and
    ///     the injected collaborators live, so it must still hold both.
    /// </summary>
    [Fact]
    public void The_root_still_owns_the_constructor_and_the_injected_dependencies()
    {
        var root = UiSourceTree.ReadAllText(Path.Combine(PlayModeDirectory(), "PlayModeViewModel.cs"));

        Assert.Contains("public PlayModeViewModel(", root, StringComparison.Ordinal);
        foreach (var dependency in new[]
                 {
                     "IPlayerProfileService _profiles",
                     "IGameplayStatsService _stats",
                     "IConfigurationService _configService",
                     "IExportTargetWriter _files",
                     "IAudioPlaybackService _audio"
                 })
        {
            Assert.Contains(dependency, root, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Proves the lint reads real declarations rather than matching nothing:
    ///     the ROOT file — which is where every plain field now lives — must be
    ///     full of exactly what the partials are forbidden to contain. Without
    ///     this, deleting the field block would make
    ///     <see cref="No_partial_declares_state_of_its_own" /> pass forever.
    /// </summary>
    [Fact]
    public void The_field_detector_finds_the_fields_the_root_actually_has()
    {
        var lines = Joined(File
            .ReadAllText(Path.Combine(PlayModeDirectory(), "PlayModeViewModel.cs"))
            .Replace("\r\n", "\n").Split('\n'));

        var found = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var match = FieldDeclaration.Match(lines[i]);
            if (!match.Success || lines[i].Contains("=>", StringComparison.Ordinal)) continue;
            if (match.Groups["mods"].Value.Contains("const", StringComparison.Ordinal)) continue;
            if (CarriesObservableProperty(lines, i)) continue;
            found++;
        }

        Assert.True(found >= 25,
            $"The root should carry every plain field of the class; the detector only found {found}.");
    }

    /// <summary>Walks back over doc comments and blank lines to the attribute
    /// list directly above a declaration.</summary>
    private static bool CarriesObservableProperty(IReadOnlyList<string> lines, int index)
    {
        if (lines[index].Contains("ObservableProperty", StringComparison.Ordinal)) return true;

        for (var j = index - 1; j >= 0; j--)
        {
            var line = lines[j].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!line.StartsWith('[')) return false;
            if (line.Contains("ObservableProperty", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    ///     Folds a declaration that wraps onto a second line (a long generic type
    ///     followed by its name) back into one, so the regex sees the whole thing.
    ///     Without this a field could hide from the lint simply by being long.
    /// </summary>
    private static List<string> Joined(IEnumerable<string> source)
    {
        var lines = source.ToList();
        var result = new List<string>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd();
            var isOpener = Regex.IsMatch(line, @"^    (private|internal|public|protected)\b") &&
                           !line.Contains(';', StringComparison.Ordinal) &&
                           !line.Contains('{', StringComparison.Ordinal) &&
                           !line.Contains('(', StringComparison.Ordinal) &&
                           !line.Contains("=>", StringComparison.Ordinal);
            if (isOpener && i + 1 < lines.Count)
            {
                result.Add(line + " " + lines[i + 1].Trim());
                i++;
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    private static string PartialSource(string partial) =>
        UiSourceTree.ReadAllText(Path.Combine(PlayModeDirectory(), $"PlayModeViewModel.{partial}.cs"));

    private static string PlayModeDirectory() =>
        UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode"));
}
