using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The oversized style dictionaries are CONTAINERS now: an
///     <c>X.axaml</c> of <c>&lt;StyleInclude&gt;</c>s over a sibling folder
///     <c>X/</c> holding the parts. This holds that arrangement to two claims
///     the compiler cannot check.
/// </summary>
/// <remarks>
///     <para>
///         <b>Neither failure has a runtime signal.</b> A part file nobody
///         merges compiles, ships and styles nothing at all; a key that stops
///         resolving leaves the property at its default and the control still
///         ARRANGES, so every bounds assertion in this repo sails straight
///         through an unstyled app. That is exactly the shape of the re-heat
///         bug that reached the owner — the layout-validity guard beside it
///         stayed green the whole time.
///     </para>
///     <para>
///         Everything here is DERIVED. The containers come from the convention
///         itself (an <c>.axaml</c> with a folder of the same name beside it),
///         the parts from the container's own include list, and the keys from
///         the parts' markup — so a new split, or a new part inside an existing
///         one, is covered the day it lands.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because resolving a resource needs the
///         real Avalonia app session: most of these dictionaries live in
///         <c>App.Styles</c>, and nothing below a live <c>Application</c> can
///         answer what a key resolves to. A container an ELEMENT includes (the
///         dialog grammar) is probed below an include of its own.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class SplitStyleDictionaryUiPerfSmokeTests
{
    /// <summary>Every container, by path relative to <c>DotCharter.UI</c>.</summary>
    public static TheoryData<string> Containers
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var c in ContainerFiles())
                data.Add(Path.GetRelativePath(UiRoot(), c).Replace('\\', '/'));
            return data;
        }
    }

    /// <summary>
    ///     The folder and the include list must AGREE, in both directions. A
    ///     part the container forgot is dead markup; an include naming a file
    ///     that moved is a section of the grammar that silently stopped
    ///     applying; and the same part included TWICE applies its section twice,
    ///     which changes what wins.
    /// </summary>
    [Theory]
    [MemberData(nameof(Containers))]
    public void EveryPartOnDisk_IsMergedByItsContainer(string relativePath)
    {
        var container = Path.Combine(UiRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var folder = FolderOf(container);

        var allOnDisk = UiSourceTree.GetFiles(folder, "*.axaml", SearchOption.AllDirectories)
            .Select(Path.GetFullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        // A `*.Wide.axaml` part is NOT the container's to merge: the window
        // breakpoint (2026-09-12) swaps it in and out at runtime through
        // `Services/UiBreakpointController`, over the base ladder, so a static
        // include here would apply the wide rungs to every window. It is still
        // a part on disk that must be reached by SOMETHING, so the controller's
        // source has to name it — that is the "silently stopped applying"
        // failure this lint exists for, on the other mechanism.
        var breakpointParts = allOnDisk
            .Where(p => p.EndsWith(".Wide.axaml", StringComparison.OrdinalIgnoreCase)).ToList();
        if (breakpointParts.Count > 0)
        {
            var controller = UiSourceTree.ReadAllText(Path.Combine(UiRoot(), "Services", "UiBreakpointController.cs"));
            foreach (var part in breakpointParts)
            {
                var (root, assembly) = ProjectOf(part);
                var relative = Path.GetRelativePath(root, part).Replace(Path.DirectorySeparatorChar, '/');
                Assert.Contains($"avares://{assembly}/{relative}", controller);
            }
        }
        var onDisk = allOnDisk.Except(breakpointParts).ToList();

        // Read the container's OWN include list rather than the resolved walk:
        // a dangling include resolves to nothing and would simply vanish, which
        // is the half of this that has no other signal. A container may also
        // merge grammars from elsewhere (the inspector scopes
        // `Styles/FireMenuChrome.axaml` this way) — those are not its parts.
        var declared = Regex.Matches(UiSourceTree.ReadAllText(container),
                "Source=\"avares://(?<a>DotCharter(?:\\.Tone\\.UI)?)/(?<p>[^\"]+)\"")
            .Select(m => Path.GetFullPath(Path.Combine(
                m.Groups["a"].Value == "DotCharter" ? UiRoot() : ToneUiRoot(),
                m.Groups["p"].Value.Replace('/', Path.DirectorySeparatorChar))))
            .Where(p => p.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(onDisk);
        Assert.Equal(declared.Count, declared.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(onDisk, declared.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    ///     Every <c>x:Key</c> any part declares must still resolve from a live
    ///     control. This is the half that would catch a merge mechanism that
    ///     LOOKS right and puts the dictionary somewhere the app never looks —
    ///     the split builds clean either way.
    /// </summary>
    [Theory]
    [MemberData(nameof(Containers))]
    public async Task EveryKeyThePartsDeclare_StillResolvesFromALiveControl(string relativePath)
    {
        var container = Path.Combine(UiRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var folder = FolderOf(container);

        // This container's OWN parts only. A grammar that scopes some OTHER
        // dictionary into itself (the inspector does) declares keys that are
        // deliberately not visible app-wide, and asking a bare Border for those
        // would assert the opposite of the truth.
        var keys = new List<(string Part, string Key)>();
        foreach (var part in StyleFileText.FilesOf(container).Skip(1)
                     .Where(p => p.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(part), "x:Key=\"(?<k>[^\"]+)\""))
                keys.Add((Path.GetFileName(part)!, m.Groups["k"].Value));
        }

        // A container whose parts are pure `<Style>` blocks declares no keys.
        // That is legitimate (Styles/Fire/), and its own resolution is proved by
        // FireComponentUiPerfSmokeTests, so there is simply nothing to do here.
        if (keys.Count == 0) return;

        // WHERE the app looks for these keys depends on where the container is
        // included. Most containers are app-wide (App.axaml merges them, directly
        // or through another grammar), so a bare control must see every key —
        // that is checked first, exactly as before. A container an ELEMENT
        // includes (the dialog grammar: DialogContainerView and the Diagnostics
        // window, #428) is visible only below that element, on purpose. Its keys
        // are then probed below an include of its own, the way those views merge
        // it — and something real must include it, or nothing ever looks there.
        var (projectRoot, assembly) = ProjectOf(container);
        var uri = $"avares://{assembly}/{Path.GetRelativePath(projectRoot, container).Replace('\\', '/')}";
        var mergedByApp = UiSourceTree.ReadAllText(Path.Combine(UiRoot(), "App.axaml"))
            .Contains($"Source=\"{uri}\"", StringComparison.Ordinal);
        var includers = new[] { UiRoot(), ToneUiRoot() }
            .SelectMany(root => UiSourceTree.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
            .Where(f => UiSourceTree.ReadAllText(f).Contains($"Source=\"{uri}\"", StringComparison.Ordinal))
            .ToList();
        Assert.True(includers.Count > 0,
            $"{relativePath} ({uri}) is merged by nothing in the app: its parts style nothing");

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var appWide = MissingFrom(new Border());
            if (appWide.Count == 0) return;

            // Not app-wide. Fine only for an element-scoped container that
            // resolves below its own include; an App.axaml merge that cannot be
            // seen app-wide is exactly the failure this exists for.
            var scoped = new Border();
            scoped.Styles.Add(new StyleInclude(new Uri($"avares://{assembly}/")) { Source = new Uri(uri) });
            var missing = mergedByApp ? appWide : MissingFrom(scoped);

            Assert.True(missing.Count == 0,
                $"{relativePath} declares {keys.Count} key(s) across its parts and {missing.Count} of " +
                "them cannot be resolved from a control in the running app. A dictionary merged into a " +
                "place nothing looks builds perfectly clean; the properties that named these keys keep " +
                "their defaults and every surface still lays out:\n  " + string.Join("\n  ", missing));
        }, CancellationToken.None);

        List<string> MissingFrom(Border probe)
        {
            var window = new Window { Width = 400, Height = 300, Content = probe };
            window.Show();
            try
            {
                return keys
                    .Where(k => !probe.TryFindResource(k.Key, out _))
                    .Select(k => $"{k.Part} \u2192 {k.Key}")
                    .ToList();
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }
    }

    /// <summary>The design system moved to DotCharter.Tone.UI (assembly and
    /// project of the same name); the app's own grammars stay in DotCharter.UI
    /// (assembly <c>DotCharter</c>). A part's avares:// URI names its own.</summary>
    private static string ToneUiRoot() => UiSourceTree.Mirror(UiRoot())!;

    private static (string Root, string Assembly) ProjectOf(string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(ToneUiRoot()) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            ? (ToneUiRoot(), "DotCharter.Tone.UI")
            : (UiRoot(), "DotCharter");

    private static string FolderOf(string container) => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(container)!, Path.GetFileNameWithoutExtension(container)));

    /// <summary>An <c>.axaml</c> with a folder of the same name beside it — the
    /// convention every split in this repo follows.</summary>
    private static IEnumerable<string> ContainerFiles() =>
        UiSourceTree.EnumerateFiles(UiRoot(), "*.axaml", SearchOption.AllDirectories)
            .Where(f => Directory.Exists(Path.Combine(
                Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f))))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static string UiRoot() => Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
}
