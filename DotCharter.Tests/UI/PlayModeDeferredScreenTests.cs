using System.Xml.Linq;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>Play Mode's hidden screens are DEFERRED CONTENT, and this is what keeps
///     them that way.</b>
///     <para>
///         <c>MainWindow</c>'s <c>DataContextChanged</c> fills <c>PlayModeHost</c>
///         synchronously, before <c>Window.Show()</c> — it has to, or the app's
///         first frame is an empty screen. So every element <c>PlayModeView</c>
///         declares inline is constructed in front of the first frame. Measured on
///         the <c>StartupPhases:</c> line as the <c>bind=</c> phase and A/B'd
///         against a boot into the chart editor (which skips the fill entirely):
///         <b>6,039 ms versus 666 ms</b> headless, and the biggest single phase of
///         a published <c>r2r=yes</c> cold start.
///     </para>
///     <para>
///         The fix is a <c>ContentControl</c> host whose body is a
///         <c>ContentTemplate</c>: template content is not BUILT until a layout
///         pass reaches the presenter, and a hidden host never runs one. The
///         construction then happens on whichever comes first — the prewarm drain
///         after the first frame, or the user's own navigation.
///     </para>
///     <para>
///         Source lints, for the reason <c>StartupWorkDeferralTests</c> gives:
///         constructing the view needs a real Avalonia session, so the
///         BEHAVIOURAL half lives in
///         <c>Perf/PlayModeDeferredScreenUiPerfSmokeTests</c> and is outside the
///         default gate. Deliberately NOT wall-clock assertions — these assert the
///         structure that makes the cost impossible.
///     </para>
/// </summary>
public sealed class PlayModeDeferredScreenTests
{
    /// <summary>
    ///     Every host named in <c>DeferredScreenHostNames</c> exists, is a
    ///     <c>ContentControl</c>, owns the screen's <c>IsVisible</c> gate and
    ///     declares its body as a <c>ContentTemplate</c>. A host whose body drifted
    ///     back inline would compile, look identical and silently put its whole
    ///     subtree back in front of the first frame.
    /// </summary>
    [Fact]
    public void EveryDeferredHost_DeclaresItsScreenAsContentTemplate()
    {
        foreach (var host in PlayModeView.DeferredScreenHostNames)
        {
            var element = Named(host);
            Assert.Equal("ContentControl", element.Name.LocalName);

            var gate = Attr(element, "IsVisible");
            Assert.True(gate is not null && gate.StartsWith('{'),
                $"'{host}' must carry the screen's IsVisible gate — with the gate on the BODY instead, " +
                "the prewarm realizes an empty host and the screen never lays out.");

            var template = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "ContentControl.ContentTemplate");
            Assert.True(template is not null,
                $"'{host}' declares no ContentControl.ContentTemplate — its body is inline again and is " +
                "constructed before the first frame.");

            var dataTemplate = template!.Elements().Single();
            Assert.Equal("DataTemplate", dataTemplate.Name.LocalName);
            Assert.Equal("vm:PlayModeViewModel", dataTemplate.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "DataType")?.Value);

            // Exactly one screen per host: the body is the screen root and
            // nothing else, so the host's gate and the screen are one to one.
            Assert.Single(dataTemplate.Elements());
        }
    }

    /// <summary>
    ///     The screen root inside a deferred body carries NO gate of its own. Two
    ///     gates read as harmless and are not: <c>HiddenSubtreePrewarmer.Realize</c>
    ///     measures a hidden root's VISIBLE children, so a hidden body under a
    ///     hidden host is skipped — the drain would build the content and then lay
    ///     out none of it, and the first navigation would pay the whole first
    ///     layout the prewarm exists to remove.
    /// </summary>
    [Fact]
    public void ADeferredScreensBody_CarriesNoSecondGate()
    {
        foreach (var host in PlayModeView.DeferredScreenHostNames)
        {
            var body = Named(host)
                .Elements().Single(e => e.Name.LocalName == "ContentControl.ContentTemplate")
                .Elements().Single()
                .Elements().Single();

            Assert.True(Attr(body, "IsVisible") is null,
                $"the body of '{host}' ({Name(body) ?? body.Name.LocalName}) gates itself as well as its " +
                "host — the prewarm then realizes nothing.");

            // A lifted screen view (#294) is the body; its own root is the screen,
            // and a gate THERE hides the screen from the prewarm just the same.
            if (body.Name.NamespaceName != PlayModeMarkup.ScreensNamespace) continue;
            var root = body.Elements().Single(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));
            Assert.True(Attr(root, "IsVisible") is null,
                $"the root of {body.Name.LocalName} ({Name(root) ?? root.Name.LocalName}), the body of '{host}', " +
                "gates itself as well as its host — the prewarm then realizes nothing.");
        }
    }

    /// <summary>
    ///     A deferred body may not declare an <c>x:Name</c> the code-behind uses,
    ///     nor an event-handler attribute. A <c>ContentTemplate</c> gets its own
    ///     namescope and generates no fields, so either one is a build break. That
    ///     is why Song Select and Results, which this view drives (the search keys,
    ///     the reveal), are reached through <c>PlayModeView.SongSelect</c> /
    ///     <c>Results</c> — lookups that build the body on demand (#648) — and not
    ///     through a named field. The compiler catches it, but only after the
    ///     markup has been moved; this says why up front.
    /// </summary>
    [Fact]
    public void NoDeferredBody_NeedsAGeneratedFieldOrAHandler()
    {
        var code = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "PlayMode", "PlayModeView.axaml.cs")));
        // The generated-field uses, not the doc comments that merely name them.
        var uses = code.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                        !l.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .ToList();

        foreach (var host in PlayModeView.DeferredScreenHostNames)
        {
            var body = Named(host).Descendants().ToList();

            foreach (var name in body.Select(Name).Where(n => n is not null))
            {
                Assert.DoesNotContain(uses,
                    l => l.Contains($"{name}.", StringComparison.Ordinal) ||
                         l.Contains($"({name})", StringComparison.Ordinal) ||
                         l.Contains($" {name} ", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    ///     <c>HiddenSurfaces()</c> builds the deferred bodies BEFORE it lists
    ///     anything. Without that, the named panes inside them — the Progress
    ///     hub's four views, the settings modal's seven, the LAN modal's two — do
    ///     not exist yet, every lookup returns null, and thirteen surfaces drop
    ///     out of the prewarm list with no error and no log line.
    /// </summary>
    [Fact]
    public void HiddenSurfaces_BuildsTheDeferredBodiesBeforeItListsThem()
    {
        var code = ViewSource();
        var method = code[code.IndexOf("public IReadOnlyList<Control> HiddenSurfaces()", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("\n    /// <summary>", StringComparison.Ordinal)];

        var build = method.IndexOf("BuildDeferredScreenBodies()", StringComparison.Ordinal);
        var list = method.IndexOf("HiddenChildrenOf", StringComparison.Ordinal);

        Assert.True(build >= 0, "HiddenSurfaces() must still materialize the deferred bodies.");
        Assert.True(list > build,
            "BuildDeferredScreenBodies() has to run before the surfaces are listed — a body that does not " +
            "exist yet cannot be found by name.");
    }

    /// <summary>
    ///     The deferral never writes <c>IsVisible</c>. Every gate here is owned by
    ///     a binding, and this view holds the surfaces that grab keyboard focus the
    ///     moment their scrim turns visible — the same rule
    ///     <c>HiddenSubtreePrewarmer</c> is source-linted for.
    /// </summary>
    [Fact]
    public void TheDeferredBuild_NeverWritesIsVisible()
    {
        var code = ViewSource();
        var method = code[code.IndexOf("private void BuildDeferredScreenBodies()", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("\n    /// <summary>", StringComparison.Ordinal)];

        Assert.DoesNotContain("IsVisible =", method, StringComparison.Ordinal);
        Assert.DoesNotContain(".Show(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Focus(", method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>The boot surfaces may never be deferred.</b> The launch screen and
    ///     the animated backdrop are what the first frame SHOWS
    ///     (<c>AppConfig.StartupScreen</c> selects Play Mode in the shell view
    ///     model's constructor), and deferring one of them would trade the bind
    ///     phase for an empty window.
    /// </summary>
    [Theory]
    [InlineData("StartScreenRoot")]
    [InlineData("MenuBackdropClip")]
    public void TheBootSurfaces_StayInline(string name)
    {
        Assert.DoesNotContain($"{name}Host", PlayModeView.DeferredScreenHostNames);

        var element = Named(name);
        Assert.DoesNotContain(element.Ancestors(),
            a => a.Name.LocalName == "ContentControl.ContentTemplate");
    }

    /// <summary>
    ///     <b>The ratchet.</b> The markup OUTSIDE every deferred host is what is
    ///     constructed before the first frame, so its element count may not
    ///     grow, or the bind phase creeps back one screen at a time. The ceiling
    ///     is the measured state when the deferral landed (2,720 elements, 1,822
    ///     deferred, so 898 inline). It was a RATIO floor (67%) until
    ///     2026-09-19, when deleting the deferred practice planner lowered the
    ///     ratio without adding a single inline element — a ratio punishes a
    ///     deletion, and the inline count is the thing the first frame pays.
    ///     Ratcheted to 170 when Song Select and Results moved behind hosts (#648);
    ///     171 for the one screen-reader live region (#859), which has to exist
    ///     before the first frame so the run's first state is announced.
    /// </summary>
    /// <remarks>
    ///     Counted in ELEMENTS for the same reason <c>PrewarmCoverageTests</c>
    ///     counts them: a wall-clock assertion in the default gate flakes the
    ///     moment suites run in parallel. <b>Ratchet the ceiling DOWN as more
    ///     screens move behind a host (or inline markup is deleted); never up.</b>
    /// </remarks>
    private const int InlineElementCeiling = 171;

    [Fact]
    public void MostOfPlayModeViewsMarkup_IsBehindADeferredHost()
    {
        var all = Doc.Root!.Descendants().Count(IsElement);
        var deferred = PlayModeView.DeferredScreenHostNames
            .Sum(h => Named(h).Descendants().Count(IsElement));

        var inline = all - deferred;
        Assert.True(inline <= InlineElementCeiling,
            $"{inline} of {all} elements are outside every deferred host — the ceiling is " +
            $"{InlineElementCeiling}, so a screen has been added inline or a host was unwrapped. " +
            "Every element outside one is constructed before the app's first frame.");
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Property-element nodes (<c>&lt;Grid.RowDefinitions&gt;</c>) are
    /// markup, not controls; the counts above are about controls.</summary>
    private static bool IsElement(XElement e) => !e.Name.LocalName.Contains('.', StringComparison.Ordinal);

    private static XDocument? _doc;

    /// <summary>PlayModeView.axaml with its lifted screen views inlined where they are
    /// declared (#294), so a deferred host's body is the view AND its markup.</summary>
    private static XDocument Doc => _doc ??= PlayModeMarkup.Document();

    private static string? _source;

    private static string ViewSource() => _source ??= UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "PlayMode", "PlayModeView.axaml.cs")));

    private static string? Name(XElement e) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;

    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static XElement Named(string name)
    {
        var found = Doc.Descendants().Where(e => Name(e) == name).ToList();
        Assert.True(found.Count == 1,
            $"expected exactly one x:Name=\"{name}\" in the Play Mode markup, found {found.Count}");
        return found[0];
    }
}
