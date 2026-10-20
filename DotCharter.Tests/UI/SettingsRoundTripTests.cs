using System.Text.RegularExpressions;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>Every setting the settings page counts must ROUND-TRIP.</b> Change it,
///     save, reopen — the control has to come back showing what you set.
///     <para>
///         Three things can break that, and all three build clean, keep their own
///         passing unit tests, and read as shipped:
///     </para>
///     <list type="number">
///         <item>
///             A view model property that is loaded from <c>AppConfig</c> but
///             never written back. The row accepts the edit, the footer counts
///             it, Save reports success, and the value is gone next launch.
///         </item>
///         <item>
///             A property written to <c>AppConfig</c> but never read back. The
///             control shows its DEFAULT while the config says something else —
///             a silent lie, and worse than not offering the setting at all,
///             because the next Save persists the default over the real value.
///         </item>
///         <item>
///             A property that is neither: a dead <c>[ObservableProperty]</c>, or
///             a STATUS field the tracker mistook for a setting. Both shipped.
///             <c>UseWebPedalboard</c> was the first (no config field, no row, no
///             consumer — the WebView pedalboard was dropped on airspace grounds
///             and its flag stayed); <c>BackupStatusText</c> and
///             <c>AsioControlPanelError</c> were the second, so exporting a
///             backup — or a driver refusing to open its ASIO panel — left the
///             footer claiming an unsaved change that no edit had made.
///         </item>
///     </list>
///     <para>
///         The property set is each tab's own tracked list, so this asks exactly the question the footer answers: "of the
///         things this page calls settings, which ones does it actually persist?"
///         The evidence is read from SOURCE because the alternative — building
///         the real view models — needs an eighteen-service graph, a Vulkan
///         renderer and a theme service.
///     </para>
///     <para>
///         Nothing here is hand-listed except the JUSTIFICATIONS. The load and
///         save paths are DERIVED: a method that calls
///         <c>LoadAppConfig()</c> is a load path, a method that calls
///         <c>SaveAppConfig(...)</c> is a save path, plus one level of the
///         private helpers a save path calls (that is what carries
///         <c>BuildLogCategories</c> and <c>GetSelectedLanguageCulture</c>).
///         Comments are stripped first — a property named only in a comment or a
///         <c>see cref</c> is exactly as unpersisted as one named nowhere.
///     </para>
/// </summary>
public sealed class SettingsRoundTripTests
{
    /// <summary>A settings view model and the file it lives in.</summary>
    private sealed record Surface(Type Type, string RelativePath);

    private static readonly Surface[] Surfaces =
    [
        new(typeof(AppearanceSettingsViewModel), @"DotCharter.Presentation\Screens\Settings\AppearanceSettingsViewModel.cs"),
        new(typeof(AudioSettingsViewModel), @"DotCharter.Presentation\Screens\Settings\AudioSettingsViewModel.cs"),
        new(typeof(GeneralSettingsViewModel), @"DotCharter.Presentation\Screens\Settings\GeneralSettingsViewModel.cs"),
        new(typeof(EditorSettingsViewModel), @"DotCharter.Presentation\Screens\Settings\EditorSettingsViewModel.cs"),
        new(typeof(PlaySettingsViewModel), @"DotCharter.Presentation\Screens\Settings\PlaySettingsViewModel.cs"),
        new(typeof(FilesSettingsViewModel), @"DotCharter.Presentation\Screens\Settings\FilesSettingsViewModel.cs")
        // The Updates tab is a screen of its own (Presentation/Screens/Settings);
        // its two settings round-trip in UpdatesSettingsViewModelTests.
        // The AI, TONE3000 and Stem models tabs are screens of their own
        // (Presentation/Screens/Settings); their settings round-trip in
        // AiSettingsViewModelTests, Tone3000SettingsViewModelTests and
        // StemBackendViewModelTests.
    ];

    /// <summary>
    ///     Settings that DO round-trip but not through a statement naming them,
    ///     each with the route. The list only ever shrinks: a new entry is a
    ///     claim somebody has to argue for, and an entry whose property stopped
    ///     being tracked is deleted by
    ///     <see cref="EveryJustification_StillNamesATrackedProperty" /> before it
    ///     can hide the next one.
    /// </summary>
    // Empty since the Graphics tab moved to GraphicsSettingsViewModel: its
    // anchor-lead slider was the one entry. Keep it empty.
    private static readonly Dictionary<string, string> JustifiedSaves = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> JustifiedLoads = new(StringComparer.Ordinal);

    public static TheoryData<string> SurfaceNames => [.. Surfaces.Select(s => s.Type.Name)];

    /// <summary>
    ///     Fault 2: a setting the page never reads back shows its DEFAULT over a
    ///     config that says otherwise — and the next Save writes that default
    ///     over the user's real value.
    /// </summary>
    [Theory]
    [MemberData(nameof(SurfaceNames))]
    public void EveryTrackedSetting_IsLoadedFromConfig(string surfaceName)
    {
        var surface = Surfaces.Single(s => s.Type.Name == surfaceName);
        var source = SourceOf(surface);
        var loadPaths = PathsThatCall(source, "LoadAppConfig");
        Assert.False(string.IsNullOrEmpty(loadPaths),
            $"{surfaceName} has no method that calls LoadAppConfig() — this whole check would be vacuous");

        var missing = TrackedNamesOf(surface)
            .Where(n => !JustifiedLoads.ContainsKey(surface.Type.Name + "." + n))
            .Where(n => !Regex.IsMatch(loadPaths, @"(?<![\w.])" + Regex.Escape(n) + @"\s*=(?!=)"))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{surfaceName} counts {string.Join(", ", missing)} as settings the footer reports, but nothing " +
            "on a LoadAppConfig() path assigns them. A setting the page never reads back displays its " +
            "DEFAULT while the config says otherwise — and the next Save persists that default over the " +
            "real value. Either load it, drop it from the tab's Tracked list, or add it to " +
            $"{nameof(JustifiedLoads)} with the route it really takes.");
    }

    /// <summary>
    ///     Faults 1 and 3: a setting the page never writes back is an edit the
    ///     user loses at exit, and a "setting" no save path even mentions is
    ///     usually not a setting at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(SurfaceNames))]
    public void EveryTrackedSetting_IsWrittenBackToConfig(string surfaceName)
    {
        var surface = Surfaces.Single(s => s.Type.Name == surfaceName);
        var source = SourceOf(surface);
        var savePaths = PathsThatCall(source, "SaveAppConfig", withHelpers: true);
        Assert.False(string.IsNullOrEmpty(savePaths),
            $"{surfaceName} has no method that calls SaveAppConfig() — this whole check would be vacuous");

        var missing = TrackedNamesOf(surface)
            .Where(n => !JustifiedSaves.ContainsKey(surface.Type.Name + "." + n))
            // A MEMBER access (ProjectState.Foo) is not this property, which is
            // the whole point: the live-applied settings really do persist from
            // somewhere else and have to say so in JustifiedSaves.
            .Where(n => !Regex.IsMatch(savePaths, @"(?<![\w.])" + Regex.Escape(n) + @"(?![\w])"))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{surfaceName} counts {string.Join(", ", missing)} as settings the footer reports, but no " +
            "SaveAppConfig() path names them. Either the user's edit is silently dropped at exit, or it is " +
            "not a setting and the footer is counting status. Persist it, drop it from " +
            $"the tab's Tracked list, or add it to {nameof(JustifiedSaves)} with the route it takes.");
    }

    /// <summary>
    ///     The allowlists must not rot. An entry whose property no longer exists
    ///     — or is no longer tracked — permits nothing and quietly makes room for
    ///     the next unpersisted setting to look justified.
    /// </summary>
    [Fact]
    public void EveryJustification_StillNamesATrackedProperty()
    {
        foreach (var key in JustifiedSaves.Keys.Concat(JustifiedLoads.Keys))
        {
            var split = key.LastIndexOf('.');
            var typeName = key[..split];
            var propertyName = key[(split + 1)..];

            var surface = Surfaces.SingleOrDefault(s => s.Type.Name == typeName);
            Assert.True(surface is not null, $"'{key}' names no settings surface this test knows about");
            Assert.Contains(propertyName, TrackedNamesOf(surface!));
        }
    }

    /// <summary>
    ///     …and the surfaces themselves must still contribute settings. Every
    ///     assertion above is over an empty set the moment a tab's tracked list
    ///     stops being found.
    /// </summary>
    [Theory]
    [MemberData(nameof(SurfaceNames))]
    public void EverySurface_ContributesSettings(string surfaceName)
    {
        var surface = Surfaces.Single(s => s.Type.Name == surfaceName);
        Assert.NotEmpty(TrackedNamesOf(surface));
        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot(), surface.RelativePath)),
            $"{surfaceName}'s source moved — this test would read nothing and pass on everything");
    }

    // ── the walk ───────────────────────────────────────────────────────────

    // Audio counts through its own explicit list; every other tab names its
    // settings in a static `Tracked`.
    private static List<string> TrackedNamesOf(Surface surface)
    {
        if (surface.Type == typeof(AudioSettingsViewModel)) return [.. AudioSettingsViewModel.TrackedSettings];
        var tracked = surface.Type.GetField("Tracked",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.True(tracked is not null, $"{surface.Type.Name} declares no static Tracked list");
        return [.. (string[])tracked!.GetValue(null)!];
    }

    private static string SourceOf(Surface surface) =>
        SourceText.StripComments(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), surface.RelativePath)));

    /// <summary>
    ///     The bodies of every method that calls <paramref name="marker" />,
    ///     optionally plus one level of the same-file helpers those bodies call
    ///     (a save that folds eleven toggles into one flags field does the fold
    ///     in a helper, and that is still persisting them).
    /// </summary>
    private static string PathsThatCall(string source, string marker, bool withHelpers = false)
    {
        var methods = MethodBodies(source);
        // No lookbehind on the marker: the call is `_configService.LoadAppConfig()`,
        // i.e. it is ALWAYS preceded by a dot.
        var matched = methods
            .Where(kv => Regex.IsMatch(kv.Value, @"(?<![\w])" + marker + @"\s*\("))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (matched.Count == 0) return string.Empty;

        var combined = string.Join("\n", matched.Values);
        if (!withHelpers) return combined;

        foreach (Match call in Regex.Matches(combined, @"(?<![\w.])(\w+)\s*\("))
        {
            var name = call.Groups[1].Value;
            if (methods.TryGetValue(name, out var body) && !matched.ContainsKey(name))
                combined += "\n" + body;
        }

        return combined;
    }

    /// <summary>
    ///     A method header: an access modifier at the start of a line, then the
    ///     return type, then the name and its opening parenthesis.
    ///     <para>
    ///         Every quantifier here is bounded and none may cross a newline or a
    ///         parenthesis, which is deliberate. The first version ended
    ///         <c>[\w\s&lt;&gt;\?\[\],\.]*?\b(\w+)\s*\([^;{)]*\)\s*(?=\{)</c> —
    ///         a lazy character class overlapping the group right after it, run
    ///         over a 62 KB source. That is textbook catastrophic backtracking
    ///         and .NET's backtracking engine spends STACK on it, so the failure
    ///         mode would be the testhost dying with no failing test and no
    ///         message: indistinguishable from this box's memory-pressure kill,
    ///         and therefore not something to leave in place. The parameter list
    ///         and the body are matched by COUNTING instead, which is linear.
    ///     </para>
    /// </summary>
    private static readonly Regex MethodHeader = new(
        @"(?m)^[ \t]*(?:public|private|internal|protected)[^\r\n(]{0,160}?\b(\w+)\s*\(",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary>Every method body in the file, keyed by name (overloads and
    /// partials are concatenated — this only ever asks "does this name appear
    /// somewhere in the path", never where).</summary>
    private static Dictionary<string, string> MethodBodies(string source)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match header in MethodHeader.Matches(source))
        {
            // The parameter list, by counting — it wraps across a dozen lines on
            // the constructors that do the loading.
            var i = SkipBalanced(source, header.Index + header.Length - 1, '(', ')');
            if (i < 0) continue;
            while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
            if (i >= source.Length || source[i] != '{') continue; // expression-bodied, or a declaration

            var end = SkipBalanced(source, i, '{', '}');
            if (end < 0) continue;

            var name = header.Groups[1].Value;
            var body = source[i..end];
            bodies[name] = bodies.TryGetValue(name, out var existing) ? existing + "\n" + body : body;
        }

        return bodies;
    }

    /// <summary>The index just past the <paramref name="close" /> matching the
    /// <paramref name="open" /> at <paramref name="start" />, or −1.</summary>
    private static int SkipBalanced(string source, int start, char open, char close)
    {
        if (start >= source.Length || source[start] != open) return -1;
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == open) depth++;
            else if (source[i] == close && --depth == 0) return i + 1;
        }

        return -1;
    }
}
