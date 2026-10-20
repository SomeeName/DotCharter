using System.Xml.Linq;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>What <c>new MainWindow()</c> and the DataContext bind no longer build
///     (#194).</b>
///     <para>
///         A published R2R cold start spent ~500 ms in <c>windowCtor=</c> and
///         ~285 ms in <c>bind=</c>, and a <c>dotnet-trace</c> of it put nearly all
///         of both in Avalonia STYLING elements as they joined the window's
///         logical tree — i.e. the cost is the number of elements built, wherever
///         they are built. Four views the default boot (Play Mode, full-bleed) does
///         not show were ~1,100 of them: the Amp &amp; FX rig, twice (the shell's
///         overlay and Play Mode's, ~450 elements each with its sub-views), the
///         menu bar, the editor toolbar and the status rail. Each is now the
///         <c>ContentTemplate</c> body of a <c>ContentControl</c> host, which
///         Avalonia builds only when a layout pass reaches the presenter — after
///         first paint through the prewarm drain, or on the user's first click.
///     </para>
///     <para>
///         Source lints, because constructing the shell needs a real Avalonia
///         session; <c>Perf/StartupWindowDeferralUiPerfSmokeTests</c> is the
///         behavioural half. Structure, never milliseconds.
///     </para>
/// </summary>
public sealed class StartupWindowDeferralTests
{
    /// <summary>The host, the file it lives in and the ONLY view its body holds.</summary>
    public static TheoryData<string, string, string> Hosts => new()
    {
        { "MainWindow.axaml", "ShellRigScreenHost", "AmpFxRigView" },
        { "MainWindow.axaml", "MainMenuBarHost", "MainMenuView" },
        { "MainWindow.axaml", "EditorToolbarHost", "ToolbarView" },
        { "MainWindow.axaml", "StatusBarHost", "StatusBar" },
        { "PlayMode/PlayModeView.axaml", PlayModeView.RigScreenHostName, "AmpFxRigView" }
    };

    /// <summary>
    ///     Each host is a <c>ContentControl</c> whose body is a
    ///     <c>ContentTemplate</c> holding the view and nothing else, and the body
    ///     carries no gate of its own (the prewarmer measures a hidden root's
    ///     VISIBLE children — a gated body under a host would be built and then laid
    ///     out never). A body that drifted back inline compiles, looks identical and
    ///     puts its whole subtree back in front of the first frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void TheViewIsTheContentTemplateBodyOfItsHost(string file, string host, string view)
    {
        var element = Named(file, host);
        Assert.Equal("ContentControl", element.Name.LocalName);
        Assert.True(Attr(element, "Content") is { } content && content.StartsWith("{Binding", StringComparison.Ordinal),
            $"'{host}' has no bound Content — its template would build against nothing.");

        var template = element.Elements().SingleOrDefault(e => e.Name.LocalName == "ContentControl.ContentTemplate");
        Assert.True(template is not null,
            $"'{host}' declares no ContentTemplate: {view} is inline again and is built inside the startup phases.");
        var body = Assert.Single(Assert.Single(template!.Elements(), e => e.Name.LocalName == "DataTemplate").Elements());
        Assert.Equal(view, body.Name.LocalName);
        Assert.True(Attr(body, "IsVisible") is null,
            $"the {view} inside '{host}' gates itself — the gate belongs on the host or above it.");
    }

    /// <summary>
    ///     …and no file declares one of those views INLINE anywhere else. The rig
    ///     was the shell's single largest subtree and it is declared in two files;
    ///     a third copy added inline would quietly undo half of this.
    /// </summary>
    [Fact]
    public void NoDeferredViewIsDeclaredInlineInTheShellOrPlayMode()
    {
        foreach (var file in new[] { "MainWindow.axaml", "PlayMode/PlayModeView.axaml" })
        {
            foreach (var inline in Doc(file).Descendants()
                         .Where(e => e.Name.LocalName is "AmpFxRigView" or "MainMenuView" or "ToolbarView" or "StatusBar")
                         .Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "DataTemplate")))
            {
                Assert.Fail($"{file} declares a {inline.Name.LocalName} outside a DataTemplate — it is built " +
                            "before the first frame again.");
            }
        }
    }

    /// <summary>
    ///     The prewarm still REACHES every body, so the first switch stays inside
    ///     the 150 ms budget. A body no layout pass has reached does not exist, so
    ///     the owners that list named surfaces INSIDE one build it first (after
    ///     asking for the rig screen, or the host's content is null and there is
    ///     nothing to build), and the shell's chrome drain lists the HOSTS.
    /// </summary>
    [Fact]
    public void ThePrewarmBuildsEveryBodyBeforeItListsOrWalksIt()
    {
        var shell = MainWindowCodeBehind();
        var surfaces = BodyOf(shell, "private IEnumerable<Control> HiddenShellSurfaces()");
        AssertInOrder(surfaces, "ToneRig.EnsureRigScreen();", "BuildDeferredBody(_window.ShellRigScreenHost)",
            "HiddenNamedDescendants(rig");
        // The title bar is WindowChromeView's (#654), in that view's namescope, so
        // it is reached through the view rather than by name.
        foreach (var host in new[] { "\"EditorToolbarHost\"", "_window.WindowChrome.TitleBarRow", "\"StatusBarHost\"" })
            Assert.Contains(host, surfaces, StringComparison.Ordinal);

        Assert.Contains("DeferredChrome<MainMenuView>(_window.WindowChrome.MainMenuBarHost)",
            BodyOf(shell, "private void ScheduleMenuFlyoutPrewarm(Action onDone)"), StringComparison.Ordinal);
        var popovers = BodyOf(shell, "private void ScheduleTransportFlyoutPrewarm(Action onDone)");
        Assert.Contains("DeferredChrome<ToolbarView>(_window.FindControl<ContentControl>(\"EditorToolbarHost\"))", popovers, StringComparison.Ordinal);
        Assert.Contains("DeferredChrome<StatusBar>(_window.FindControl<ContentControl>(\"StatusBarHost\"))", popovers, StringComparison.Ordinal);
        Assert.Contains("HiddenSubtreePrewarmer.BuildDeferredBody(host);",
            BodyOf(shell, "private static T? DeferredChrome<T>(ContentControl? host)"), StringComparison.Ordinal);

        var playMode = BodyOf(Source("DotCharter.UI", "Views", "PlayMode", "PlayModeView.axaml.cs"),
            "public IReadOnlyList<Control> HiddenSurfaces()");
        AssertInOrder(playMode, "ToneRig.EnsureRigScreen();", "BuildDeferredBody(this.FindControl<ContentControl>(RigScreenHostName))",
            "HiddenNamedDescendants(this, NestedScreenBodyNames)");
    }

    // ---- helpers -----------------------------------------------------------

    private static void AssertInOrder(string src, params string[] pieces)
    {
        var at = 0;
        foreach (var piece in pieces)
        {
            var next = src.IndexOf(piece, at, StringComparison.Ordinal);
            Assert.True(next >= 0, $"expected '{piece}' (in this order: {string.Join(" → ", pieces)}).");
            at = next + piece.Length;
        }
    }

    private static readonly Dictionary<string, XDocument> Docs = [];

    private static XDocument Doc(string file)
    {
        lock (Docs)
        {
            if (!Docs.TryGetValue(file, out var doc))
            {
                // The window as one document, its chrome view spliced in (#654):
                // the menu bar's host lives in WindowChromeView.axaml now.
                Docs[file] = doc = file == "MainWindow.axaml"
                    ? MainWindowSource.Document()
                    : XDocument.Load(UiSourceTree.Resolve(Path.Combine(
                        [TestPaths.RepoRoot(), "DotCharter.UI", "Views", .. file.Split('/')])));
            }

            return doc;
        }
    }

    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static XElement Named(string file, string name)
    {
        var found = Doc(file).Descendants().Where(e => Attr(e, "Name") == name).ToList();
        Assert.True(found.Count == 1, $"expected exactly one x:Name=\"{name}\" in {file}, found {found.Count}");
        return found[0];
    }

    private static string Source(params string[] parts) =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine([TestPaths.RepoRoot(), .. parts])));

    /// <summary>Every <c>MainWindow*.cs</c> under <c>Views/</c>, RECURSIVELY — the
    /// prewarm lives in <c>Views/Shell/MainWindowPrewarm.cs</c>.</summary>
    private static string MainWindowCodeBehind() =>
        string.Join('\n', UiSourceTree
            .GetFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "MainWindow*.cs",
                SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));

    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{signature}' in the source.");
        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
