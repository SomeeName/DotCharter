using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     The timeline context menu (0.59), opened by Shift+right-click since
///     #467 — a plain right-click is the instant charting verb again (see
///     <c>HighwayRightClickTests</c>). The menu model is pure data
///     (<see cref="TimelineContextMenuBuilder" />), so its content — items,
///     order, which action each invokes, and that every LocKey really exists in
///     BOTH resx files — is pinned here without a visual tree; the presenter
///     (<c>ChartRenderer.ShowContextMenu</c>) only realizes it.
/// </summary>
public sealed class TimelineContextMenuBuilderTests
{
    private sealed class Recorder
    {
        public readonly List<string> Invoked = [];

        public TimelineContextMenuActions Actions => new(
            DeleteSelection: () => Invoked.Add("delete"),
            DuplicateSelection: () => Invoked.Add("duplicate"),
            OpenInInspector: () => Invoked.Add("inspector"),
            SetFret: f => Invoked.Add($"fret:{f}"),
            AddHandShape: () => Invoked.Add("handshape"),
            AddFhpAnchor: () => Invoked.Add("fhp"),
            AddNoteHere: () => Invoked.Add("addnote"),
            Paste: () => Invoked.Add("paste"),
            SelectAllInView: () => Invoked.Add("selectview"),
            AddEventAtTime: () => Invoked.Add("event"),
            ApplyDlcBuilderFixes: () => Invoked.Add("dlcbuilder"));
    }

    [Fact]
    public void TheSoundMenu_CarriesTheSevenVerbsInOrder()
    {
        var items = TimelineContextMenuBuilder.ForSound(new Recorder().Actions);

        Assert.Equal(
        [
            "Timeline_Ctx_Delete", "Timeline_Ctx_Duplicate", "Timeline_Ctx_OpenInspector",
            "Timeline_Ctx_SetFret", "(separator)",
            "Timeline_Ctx_AddHandShape", "Timeline_Ctx_AddFhpAnchor"
        ], items.Select(i => i.LocKey ?? "(separator)").ToArray());
        Assert.True(items[4].IsSeparator);
    }

    [Fact]
    public void TheEmptyHighwayMenu_CarriesTheFourVerbsInOrder()
    {
        var items = TimelineContextMenuBuilder.ForEmptyHighway(new Recorder().Actions);

        Assert.Equal(
        [
            "Timeline_Ctx_AddNoteHere", "Timeline_Ctx_Paste", "Timeline_Ctx_SelectAllInView",
            "(separator)", "Timeline_Ctx_AddEventAtTime", "(separator)", "Timeline_Ctx_ApplyDlcBuilderFixes"
        ], items.Select(i => i.LocKey ?? "(separator)").ToArray());
    }

    [Fact]
    public void EveryLeafInvokesItsOwnAction()
    {
        var recorder = new Recorder();
        foreach (var item in TimelineContextMenuBuilder.ForSound(recorder.Actions)
                     .Concat(TimelineContextMenuBuilder.ForEmptyHighway(recorder.Actions)))
        {
            if (item.IsSeparator || item.Children != null) continue;
            Assert.NotNull(item.Invoke);
            item.Invoke!();
        }

        Assert.Equal(
            ["delete", "duplicate", "inspector", "handshape", "fhp", "addnote", "paste", "selectview", "event", "dlcbuilder"],
            recorder.Invoked);
    }

    /// <summary>The Set-fret submenu spans the same 0..24 clamp every fret verb
    /// uses, each child invoking with ITS number (fret 0 is the capo and must
    /// be offered).</summary>
    [Fact]
    public void TheSetFretSubmenu_Spans0To24_AndEachChildSetsItsOwnFret()
    {
        var recorder = new Recorder();
        var setFret = TimelineContextMenuBuilder.ForSound(recorder.Actions)
            .Single(i => i.LocKey == "Timeline_Ctx_SetFret");

        Assert.NotNull(setFret.Children);
        Assert.Equal(25, setFret.Children!.Count);
        for (var f = 0; f <= 24; f++)
        {
            Assert.Equal(f.ToString(), setFret.Children[f].LiteralHeader);
            setFret.Children[f].Invoke!();
        }

        Assert.Equal(Enumerable.Range(0, 25).Select(f => $"fret:{f}"), recorder.Invoked);
    }

    /// <summary>
    ///     Every LocKey the builder can emit exists in BOTH resx files — the
    ///     menu is built in code, so the axaml localization sweep never sees
    ///     these keys and a missing one renders as the raw key string at
    ///     runtime with no warning.
    /// </summary>
    [Fact]
    public void EveryMenuLocKey_ExistsInBothResxFiles()
    {
        var recorder = new Recorder();
        var keys = TimelineContextMenuBuilder.ForSound(recorder.Actions)
            .Concat(TimelineContextMenuBuilder.ForEmptyHighway(recorder.Actions))
            .Where(i => i.LocKey != null)
            .Select(i => i.LocKey!)
            .Distinct()
            .ToList();
        Assert.NotEmpty(keys);

        foreach (var resx in new[]
                 {
                     "DotCharter.Localization/Resources.resx",
                     "DotCharter.Localization/Resources.de-DE.resx"
                 })
        {
            var content = UiSourceTree.ReadAllText(RepoPath(resx));
            foreach (var key in keys)
            {
                Assert.True(content.Contains($"\"{key}\"", StringComparison.Ordinal),
                    $"Loc key '{key}' is missing from {resx} — a code-built menu item renders the raw key.");
            }
        }
    }

    /// <summary>
    ///     Source lint: the tool routes a right-press through the pure
    ///     <see cref="HighwayRightClick" /> verbs and arms the menu ONLY when
    ///     they decline it (Shift). <c>HighwayInteractionTool</c> takes a
    ///     <c>MainWindowViewModel</c>, so the call sites are pinned the same way
    ///     <c>PointerGestureBoundaryTests</c> pins <c>EndGesture</c>.
    /// </summary>
    [Fact]
    public void APlainRightClickEditsNotes_AndOnlyADeclinedVerbArmsTheMenu()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Input/Tools/HighwayInteractionTool.cs"));

        var press = source.IndexOf("public void OnPointerPressed", StringComparison.Ordinal);
        var moved = source.IndexOf("public void OnPointerMoved", StringComparison.Ordinal);
        Assert.True(press >= 0 && moved > press, "HighwayInteractionTool pointer handlers moved — repoint this lint.");
        var pressBody = source[press..moved];

        var rightBranch = pressBody.IndexOf("IsRightButtonPressed", StringComparison.Ordinal);
        Assert.True(rightBranch >= 0);
        var right = pressBody[rightBranch..];

        var resolve = right.IndexOf("HighwayRightClick.Resolve(", StringComparison.Ordinal);
        var apply = right.IndexOf("if (!HighwayRightClick.Apply(", StringComparison.Ordinal);
        var arm = right.IndexOf("_pendingMenu = true", StringComparison.Ordinal);
        Assert.True(resolve >= 0, "the right-press no longer asks HighwayRightClick what to do (#467).");
        Assert.True(apply > resolve, "the right-press must APPLY the instant verb before anything else (#467).");
        Assert.True(arm > apply,
            "the menu must be armed only in the branch where HighwayRightClick.Apply declined (#467).");
        Assert.Contains("ShowContextMenu", source);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
