using Avalonia.Input;
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
    private sealed class Recorder(int notes = 1, int chords = 1, Func<string, bool>? canRun = null)
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
            Run: id => Invoked.Add("run:" + id),
            ChordOf: id => id == "edit.cut" ? new KeyChord(Key.X, KeyModifiers.Control) : KeyChord.None,
            CanRun: canRun ?? (_ => true),
            SelectedNotes: notes,
            SelectedChords: chords,
            DisabledReason: _ => "Unavailable for this selection.");
    }

    private static string[] Keys(IEnumerable<TimelineMenuItem> items) =>
        items.Select(i => i.LocKey ?? "(separator)").ToArray();

    private static IEnumerable<TimelineMenuItem> Flatten(IEnumerable<TimelineMenuItem> items) =>
        items.SelectMany(i => i.Children == null ? [i] : Flatten(i.Children).Prepend(i));

    [Fact]
    public void TheSoundMenu_CarriesEverySelectionEditInOrder()
    {
        var items = TimelineContextMenuBuilder.ForSound(new Recorder().Actions);

        Assert.Equal(
        [
            "Menu_Cut", "Menu_Copy", "Timeline_Ctx_Duplicate", "Timeline_Ctx_Delete", "(separator)",
            "Timeline_Ctx_SetFret", "Menu_GroupTechnique", "Menu_GroupChords", "Menu_GroupMoveResize", "(separator)",
            "Timeline_Ctx_AddHandShape", "Menu_HandShapesFromLetRing", "Menu_Generate_Arpeggio", "Timeline_Ctx_AddFhpAnchor", "(separator)",
            "Menu_Refret", "LinkIdentical_Title", "Timeline_Ctx_OpenInspector"
        ], Keys(items));
    }

    [Fact]
    public void TheTechniqueSubmenu_HasTheTenTogglesOfEditTechnique()
    {
        var technique = TimelineContextMenuBuilder.ForSound(new Recorder().Actions)
            .Single(i => i.LocKey == "Menu_GroupTechnique");

        Assert.Equal(
        [
            "Menu_Tech_Hopo", "Menu_Tech_Harmonic", "Menu_Tech_Mute", "Menu_Tech_LinkNext", "Menu_Tech_Vibrato",
            "Menu_Tech_Tremolo", "Menu_PickSlide", "Menu_Tech_Picking", "Menu_Tech_Accent", "Menu_Tech_Ignore",
            "Menu_Tech_PassOtherNotes"
        ], Keys(technique.Children!));
    }

    [Fact]
    public void TheChordSubmenu_OnNotesAndChords_HasEveryEditChordRow()
    {
        var chord = TimelineContextMenuBuilder.ForSound(new Recorder(notes: 2, chords: 1).Actions)
            .Single(i => i.LocKey == "Menu_GroupChords");

        Assert.Equal(
        [
            "Menu_ConvertToPowerChordSmall", "Menu_ConvertToPowerChordBig", "Menu_MergeNotesIntoChord",
            "Menu_SplitChordsIntoNotes", "Menu_ToggleOctaveFingering", "Menu_TogglePowerChordFingering",
            "Menu_MutedStrumsToChord", "Menu_ToggleGhostGems", "Menu_TappedHarmonic"
        ], Keys(chord.Children!));
    }

    [Fact]
    public void ARowThatCannotActOnTheSelection_StaysDisabledWithItsReason()
    {
        // One lone note: no merge (needs 2), no split / fingering rows (no chord).
        var lone = Flatten(TimelineContextMenuBuilder.ForSound(new Recorder(notes: 1, chords: 0).Actions))
            .Where(i => i.LocKey != null).ToDictionary(i => i.LocKey!);
        Assert.False(lone["Menu_MergeNotesIntoChord"].IsEnabled);
        Assert.Equal("Status_MergeChord_NoCandidates", lone["Menu_MergeNotesIntoChord"].DisabledReasonLocKey);
        Assert.False(lone["Menu_SplitChordsIntoNotes"].IsEnabled);
        Assert.Equal("Status_NoChordsSelected", lone["Menu_SplitChordsIntoNotes"].DisabledReasonLocKey);
        Assert.False(lone["Menu_ToggleOctaveFingering"].IsEnabled);
        Assert.True(lone["Menu_ConvertToPowerChordSmall"].IsEnabled);

        // A chord alone: note-only commands explain what to select.
        var chordOnly = Flatten(TimelineContextMenuBuilder.ForSound(new Recorder(notes: 0, chords: 1).Actions))
            .Where(i => i.LocKey != null).ToDictionary(i => i.LocKey!);
        Assert.False(chordOnly["Menu_ConvertToPowerChordSmall"].IsEnabled);
        Assert.Equal("Timeline_Ctx_SelectNotes", chordOnly["Menu_ConvertToPowerChordSmall"].DisabledReasonLocKey);
        Assert.True(chordOnly["Menu_SplitChordsIntoNotes"].IsEnabled);

        // Even a group with no enabled commands stays discoverable.
        var none = TimelineContextMenuBuilder.ForSound(new Recorder(canRun: id => !id.StartsWith("note.")).Actions);
        var technique = none.Single(i => i.LocKey == "Menu_GroupTechnique");
        Assert.All(technique.Children!, i => Assert.False(i.IsEnabled));
        var noRefret = TimelineContextMenuBuilder.ForSound(new Recorder(canRun: id => id != "tools.refret").Actions)
            .Single(i => i.LocKey == "Menu_Refret");
        Assert.False(noRefret.IsEnabled);
        Assert.Equal("Unavailable for this selection.", noRefret.DisabledReason);
    }

    [Fact]
    public void ACommandThatBecomesUnavailableWhileTheMenuIsOpen_DoesNotRun()
    {
        var canRun = true;
        var recorder = new Recorder(canRun: _ => canRun);
        var row = TimelineContextMenuBuilder.ForSound(recorder.Actions).Single(i => i.LocKey == "Menu_Refret");
        Assert.True(row.IsEnabled);
        canRun = false;
        row.Invoke!();
        Assert.Empty(recorder.Invoked);
    }

    [Fact]
    public void ARowShowsTheCurrentChordOfItsVerb()
    {
        var items = TimelineContextMenuBuilder.ForSound(new Recorder().Actions);
        Assert.Equal(new KeyChord(Key.X, KeyModifiers.Control), items.Single(i => i.LocKey == "Menu_Cut").Chord);
        Assert.Null(items.Single(i => i.LocKey == "Menu_Copy").Chord);
    }

    [Fact]
    public void TheEmptyHighwayMenu_CarriesTheFourVerbsInOrder_AndNoLongerTheDlcBuilderFixes()
    {
        var items = TimelineContextMenuBuilder.ForEmptyHighway(new Recorder().Actions);

        Assert.Equal(
        [
            "Timeline_Ctx_AddNoteHere", "Timeline_Ctx_Paste", "Timeline_Ctx_SelectAllInView",
            "(separator)", "Timeline_Ctx_AddEventAtTime"
        ], Keys(items));
    }

    [Fact]
    public void EveryLeafInvokesItsOwnAction()
    {
        var recorder = new Recorder(notes: 2);
        foreach (var item in Flatten(TimelineContextMenuBuilder.ForSound(recorder.Actions)
                     .Concat(TimelineContextMenuBuilder.ForEmptyHighway(recorder.Actions))))
        {
            if (item.IsSeparator || item.Children != null || item.LiteralHeader != null) continue;
            Assert.NotNull(item.Invoke);
            item.Invoke!();
        }

        Assert.Equal(
        [
            "run:edit.cut", "run:edit.copy", "duplicate", "delete",
            "run:note.toggleHopo", "run:note.toggleHarmonic", "run:note.toggleMute", "run:note.toggleLinkNext",
            "run:note.toggleVibrato", "run:note.toggleTremolo", "run:note.pickSlide", "run:note.togglePicking", "run:note.toggleAccent",
            "run:note.toggleIgnore", "run:note.togglePassOtherNotes",
            "run:tools.powerChordSmall", "run:tools.powerChordBig", "run:tools.mergeNotesIntoChord",
            "run:tools.splitChordsIntoNotes", "run:tools.toggleOctaveFingering",
            "run:tools.togglePowerChordFingering", "run:tools.mutedStrumsToChord", "run:tools.toggleGhostGems",
            "run:tools.tappedHarmonic",
            "run:edit.snapSelected", "run:edit.increaseLength", "run:edit.decreaseLength",
            "run:note.nudgeEarlier", "run:note.nudgeLater", "run:note.nudgeEarlier10Ms", "run:note.nudgeLater10Ms",
            "run:note.nudgeEarlier1Ms", "run:note.nudgeLater1Ms",
            "run:tools.generateHandShape", "run:tools.handShapesFromLetRing", "run:tools.generateArpeggio", "fhp",
            "run:tools.refret", "run:edit.linkIdentical", "inspector",
            "addnote", "paste", "selectview", "event"
        ], recorder.Invoked);
    }

    /// <summary>Every row that runs a palette id must run a command the menu bar also binds: the id
    /// exists in <c>CommandCatalog</c>, the builder emits it, and the verb's menu-bar command is bound.</summary>
    [Theory]
    [InlineData("edit.cut", "CutCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("edit.copy", "CopyCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.powerChordSmall", "ConvertToPowerChordSmallCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.powerChordBig", "ConvertToPowerChordBigCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.mergeNotesIntoChord", "MergeNotesIntoChordCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.splitChordsIntoNotes", "SplitChordsIntoNotesCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.toggleOctaveFingering", "ToggleOctaveFingeringCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.togglePowerChordFingering", "TogglePowerChordFingeringCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.mutedStrumsToChord", "MutedStrumsToChordCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.toggleGhostGems", "ToggleGhostGemsCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.tappedHarmonic", "TappedHarmonicCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.generateHandShape", "GenerateHandShapeCommand", "Views/Shell/Menus/ToolsMenu.axaml")]
    [InlineData("tools.handShapesFromLetRing", "HandShapesFromLetRingCommand", "Views/Shell/Menus/EditMenu.axaml")]
    [InlineData("tools.generateArpeggio", "GenerateArpeggioCommand", "Views/Shell/Menus/ToolsMenu.axaml")]
    [InlineData("tools.refret", "RefretCommand", "Views/Shell/Menus/ToolsMenu.axaml")]
    [InlineData("edit.linkIdentical", "ChartEditor.LinkedMeasures.LinkIdenticalCommand", "Views/Shell/Menus/ToolsMenu.axaml")]
    [InlineData("edit.delete", "DeleteSelectionCommand", "Views/Shell/Menus/EditMenu.axaml")]
    public void ARowRunsTheSameCommandTheMenuBarBinds(string paletteId, string menuCommand, string menuFile)
    {
        var catalog = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/ViewModels/CommandPalette/CommandCatalog.cs"));
        Assert.Contains($"\"{paletteId}\"", catalog);
        var menu = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/" + menuFile));
        Assert.Contains("Command=\"{Binding " + menuCommand, menu);

        var builder = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Input/TimelineContextMenu.cs"));
        Assert.Contains($"\"{paletteId}\"", builder);
    }

    [Fact]
    public void EveryTechniqueRowIsTheMenuBarsToggleCommand()
    {
        var menu = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Shell/Menus/EditMenu.axaml"));
        var catalog = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/ViewModels/CommandPalette/CommandCatalog.cs"));
        foreach (var (locKey, id) in TimelineContextMenuBuilder.TechniqueRows)
        {
            Assert.Contains($"\"{id}\"", catalog);
            var row = menu.IndexOf($"Loc {locKey}}}\" Command=\"{{Binding SelectionEdits.", StringComparison.Ordinal);
            Assert.True(row >= 0, $"Edit > Technique has no '{locKey}' row bound to SelectionEdits.");
        }
    }

    [Fact]
    public void ApplyDlcBuilderFixes_LeftTheRightClick_ButStaysReachableFromTheToolsMenu()
    {
        var all = Flatten(TimelineContextMenuBuilder.ForSound(new Recorder().Actions)
            .Concat(TimelineContextMenuBuilder.ForEmptyHighway(new Recorder().Actions))).Select(i => i.LocKey);
        Assert.DoesNotContain("Timeline_Ctx_ApplyDlcBuilderFixes", all);

        var tools = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Shell/Menus/ToolsMenu.axaml"));
        Assert.Contains("Command=\"{Binding ApplyDlcBuilderFixesCommand}\"", tools);
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
    public void EveryMenuLocKey_ExistsInAllThreeResxFiles()
    {
        var recorder = new Recorder();
        var keys = Flatten(TimelineContextMenuBuilder.ForSound(recorder.Actions)
                .Concat(TimelineContextMenuBuilder.ForEmptyHighway(recorder.Actions)))
            .SelectMany(i => new[] { i.LocKey, i.DisabledReasonLocKey })
            .OfType<string>()
            .Distinct()
            .ToList();
        Assert.NotEmpty(keys);

        foreach (var resx in new[]
                 {
                     "DotCharter.Localization/Resources.resx",
                     "DotCharter.Localization/Resources.de-DE.resx",
                     "DotCharter.Localization/Resources.ja-JP.resx"
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
