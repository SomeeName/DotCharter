using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     W1 (2026-09-16) — the SOURCE half of "no nested scroll regions in the Amp
///     &amp; FX rig". <c>Perf/RigScrollRegionsUiPerfSmokeTests</c> walks the real
///     visual tree and is the stronger of the two, but it can only see what the
///     headless fixture REALIZES: the tone designer's match list needs a
///     configured AI provider and a search that answered, the amp's capture
///     library needs a populated NAM library. Those subtrees ship to users all
///     the same, so they are read here, as XML, in the DEFAULT gate.
///     <para>
///         A scroll region is a <c>ScrollViewer</c> or a <c>ListBox</c> whose bar
///         visibility is not <c>Disabled</c> on both axes. Disabling both is the
///         fix for a list that must live inside a scroller: it grows and the
///         surface's own scroller carries it.
///     </para>
/// </summary>
public sealed class RigScrollRegionSourceTests
{
    /// <summary>The views the rig lays out, and whether the file's ROOT is
    /// already inside someone else's scroller — in which case ANY scroll region
    /// in it is nested, wherever it sits in the file.</summary>
    private static readonly (string Path, string? HostedInside)[] RigViews =
    [
        ("Views/PlayMode/AmpFxRigView.axaml", null),
        // The Tones view (2026-09-27): the card grid's scroller and the
        // designer's column side by side — never one inside the other.
        ("Views/PlayMode/RigTonesView.axaml", null),
        ("Views/PlayMode/RigModuleEditorView.axaml", null),
        // The designer is a COLUMN of the Tones view, not inside its grid's
        // scroller; its own thread scroller (ToneDesignScroll) carries
        // everything DotCharter's wrapper adds through ExtrasTemplate.
        ("Views/PlayMode/ToneDesignCorePanel.axaml", null),
        ("Views/PlayMode/ToneDesignPanel.axaml", "ToneDesignScroll (via ToneDesignCorePanel)"),
        ("Views/PlayMode/ToneSectionsPanel.axaml", "ToneDesignScroll (via ToneDesignPanel)"),
        ("Views/PlayMode/RigEditors/RigAmpFaceView.axaml", null),
        ("Views/PlayMode/RigEditors/RigCabinetEditorView.axaml", null),
        ("Views/PlayMode/RigEditors/RigDynamicsView.axaml", null),
        ("Views/PlayMode/RigEditors/RigExpressionView.axaml", null),
        ("Views/PlayMode/RigEditors/RigGraphicEqView.axaml", null),
        ("Views/PlayMode/RigEditors/RigParametricEqView.axaml", null),
        ("Views/PlayMode/RigEditors/RigPedalEditorView.axaml", null),
        ("Views/PlayMode/RigEditors/RigRoutingConsoleView.axaml", null),
        ("Views/PlayMode/RigEditors/RigVocalView.axaml", null),
    ];

    /// <summary>
    ///     THE ALLOW-LIST IS EMPTY, and it stays a list so the NEXT nested
    ///     scroller has somewhere visible to be refused.
    ///     <para>
    ///         W1 left two behind — the searchable capture library in
    ///         <c>RigAmpFaceView</c>'s second column and the one in
    ///         <c>RigPedalEditorView</c>'s 320 px aside, each an anonymous
    ///         <c>ScrollViewer</c> inside that family view's own
    ///         <c>RigEditorScroll</c> — and W4 removed both: the card's height
    ///         cap went with the scroller, so the library grows and the family
    ///         view's own scroller carries it.
    ///     </para>
    ///     <para>
    ///         <b>This list only ever shrinks.</b> Adding a name to turn a red
    ///         run green is how a lint stops finding the thing it exists for.
    ///     </para>
    /// </summary>
    private static readonly (string File, string Inner, string Why)[] KnownW4Nesting = [];

    private static bool Scrolls(string? v) => v is null or "Visible" or "Auto";

    /// <summary>A <c>ScrollViewer</c> reads its OWN properties; every other
    /// control carries the attached ones. A <c>TextBox</c> scrolling its own text
    /// is not a scroll region of the screen.</summary>
    private static bool IsScrollRegion(XElement e)
    {
        string? h, v;
        switch (e.Name.LocalName)
        {
            case "ScrollViewer":
                h = e.Attribute("HorizontalScrollBarVisibility")?.Value;
                v = e.Attribute("VerticalScrollBarVisibility")?.Value;
                break;
            case "ListBox":
            case "TreeView":
                h = e.Attribute("ScrollViewer.HorizontalScrollBarVisibility")?.Value;
                v = e.Attribute("ScrollViewer.VerticalScrollBarVisibility")?.Value;
                break;
            default:
                return false;
        }

        return Scrolls(h) || Scrolls(v);
    }

    /// <summary>Whether <paramref name="e" /> is, or contains, an element named
    /// <paramref name="name" />.</summary>
    private static bool Carries(XElement e, string name) =>
        e.DescendantsAndSelf().Any(x => NameOf(x) == name);

    private static string NameOf(XElement e) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value
        ?? e.Name.LocalName;

    [Fact]
    public void NoScrollRegionInARigView_IsDeclaredInsideAnother()
    {
        var offenders = new List<string>();
        var regions = 0;

        foreach (var (rel, hostedInside) in RigViews)
        {
            var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", rel.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(File.Exists(path), $"{rel} is gone — repoint this list");
            var file = Path.GetFileName(path);

            foreach (var e in XDocument.Load(path).Descendants().Where(IsScrollRegion))
            {
                regions++;
                var outer = e.Ancestors().FirstOrDefault(IsScrollRegion);
                var enclosing = outer is not null ? NameOf(outer) : hostedInside;
                if (enclosing is null) continue;
                // Both exempt scrollers are ANONYMOUS, so they are keyed by the
                // named list they carry rather than by their own x:Name.
                if (KnownW4Nesting.Any(k => k.File == file && Carries(e, k.Inner))) continue;

                offenders.Add($"{file}: {NameOf(e)} inside {enclosing}");
            }
        }

        Assert.True(regions > 12, $"only {regions} scroll regions found — the parse stopped matching");
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} nested scroll region(s) in the rig's views:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    ///     …and every entry on the allow-list must still BE nested. An
    ///     allow-list whose entries have quietly been fixed stops being a record
    ///     of known debt and becomes a hole the next nested scroller falls
    ///     through.
    ///     <para>
    ///         The list is EMPTY as of W4, so this case's real job today is the
    ///         assertion at the end: it fails the moment someone adds an entry
    ///         and does not also record why the rig gained a nested region back.
    ///     </para>
    ///     <para>
    ///         Proven non-vacuous by putting <c>RigAmpFaceView</c>'s capture
    ///         scroller back: the entry it needs then satisfies this case and
    ///         <c>NoScrollRegionInARigView_IsDeclaredInsideAnother</c> goes red
    ///         without it.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryW4Exemption_IsStillANestedScrollRegion()
    {
        foreach (var (file, inner, why) in KnownW4Nesting)
        {
            var path = RigViews.Select(v => UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
                    v.Path.Replace('/', Path.DirectorySeparatorChar))))
                .Single(p => Path.GetFileName(p) == file);

            // The family view's ROOT scroller also carries the list, so the one
            // this is about is the NESTED one.
            var e = XDocument.Load(path).Descendants().SingleOrDefault(x =>
                IsScrollRegion(x) && Carries(x, inner) && x.Ancestors().Any(IsScrollRegion));
            Assert.True(e is not null,
                $"{file}: '{inner}' is no longer inside a nested scroll region — "
                + $"delete its KnownW4Nesting exemption ({why})");
        }

        Assert.True(KnownW4Nesting.Length == 0,
            $"the rig's nested-scroll-region allow-list has {KnownW4Nesting.Length} entr(y/ies) again. "
            + "It was emptied by W4 and it only ever shrinks: fix the nesting rather than listing it.");
    }
}
