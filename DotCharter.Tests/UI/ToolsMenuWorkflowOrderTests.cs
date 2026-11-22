using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Tools menu in the order a chart is made (#491): Generate, Fix &amp; clean up, AI, Repeats,
///     Tones, then the Showlights editor and the two review rows. Regrouping moved 43 rows at once
///     (the Chords group to Edit, the two FHP rows into one), so every command the menu reached
///     before must still be reached from a menu, under the same palette id.
/// </summary>
public sealed class ToolsMenuWorkflowOrderTests
{
    private static readonly XNamespace Ns = "https://github.com/avaloniaui";

    /// <summary>
    ///     Every command bound under Tools BEFORE the regroup, as its markup named it, with the
    ///     palette id that runs the same command. Frozen: this is what the old menu reached, so it is
    ///     never edited to follow the menu.
    /// </summary>
    private static readonly (string Command, string? PaletteId)[] Before =
    [
        ("GenerateFhpCommand", "tools.generateFhp"),
        ("GenerateFhpForSelectionCommand", "tools.generateFhpForSelection"),
        ("GenerateHandShapeCommand", "tools.generateHandShape"),
        ("GenerateArpeggioCommand", "tools.generateArpeggio"),
        ("GenerateHandShapesForArrangementCommand", "tools.generateHandShapesArrangement"),
        ("AcceptSuggestedHandShapesCommand", "tools.acceptSuggestedHandShapes"),
        ("AcceptSuggestedArpeggiosCommand", "tools.acceptSuggestedArpeggios"),
        ("ApplyHandShapeTemplateToChordsCommand", "tools.applyHandShapeTemplate"),
        ("ConvertToPowerChordSmallCommand", "tools.powerChordSmall"),
        ("ConvertToPowerChordBigCommand", "tools.powerChordBig"),
        ("MergeNotesIntoChordCommand", "tools.mergeNotesIntoChord"),
        ("SplitChordsIntoNotesCommand", "tools.splitChordsIntoNotes"),
        ("ToggleOctaveFingeringCommand", "tools.toggleOctaveFingering"),
        ("TogglePowerChordFingeringCommand", "tools.togglePowerChordFingering"),
        ("MutedStrumsToChordCommand", "tools.mutedStrumsToChord"),
        ("ToggleGhostGemsCommand", "tools.toggleGhostGems"),
        ("TappedHarmonicCommand", "tools.tappedHarmonic"),
        ("ImproveChartCommand", "tools.improveChart"),
        ("AutoFixChartCommand", "tools.autoFixChart"),
        ("RemoveDoubleStopChordNamesCommand", "tools.removeDoubleStopNames"),
        ("RefretCommand", "tools.refret"),
        // The palette ids call AiChartViewModel's methods (no view binds them); ARemovedAiRowsPaletteId_* checks the ids.
        ("ShellDialogs.AiChart.FixFingeringOnlyCommand", null),
        ("ShellDialogs.AiChart.FixFhpAndFingeringCommand", null),
        ("ShellDialogs.AiChart.FixFhpOnlyCommand", null),
        ("ShellDialogs.AiChart.FixChordNamesCommand", null),
        ("ShellDialogs.AiChart.FixChordNamesMusicalCommand", null),
        ("ShellDialogs.AiChart.OpenAiSettingsCommand", "tools.aiSettings"),
        ("GenerateStructureCommand", "tools.generateStructure"),
        ("PropagatePhraseCommand", "tools.propagatePhrase"),
        // The palette's edit.linkIdentical calls the method, not this command, so no id is matched here.
        ("ChartEditor.LinkedMeasures.LinkIdenticalCommand", null),
        ("ShellDialogs.OpenLinkGroupsCommand", "tools.linkedMeasures"),
        ("ShellDialogs.OpenLevelGenerationCommand", "tools.generateLevels"),
        ("SquashLevelsCommand", "tools.squashLevels"),
        ("ShellDialogs.OpenTunerCommand", "tools.tuner"),
        ("ShellDialogs.OpenToneDesignerCommand", "tools.toneDesigner"),
        ("ToneRig.OpenPedalboardCommand", "tools.pedalboard"),
        ("ToneRig.GenerateRocksmithTonesCommand", "tools.generateRocksmithTones"),
        ("StemSeparation.SeparateStemsCommand", "tools.separateStems"),
        ("ShellDialogs.OpenShowlightsCommand", "tools.showlights"),
        ("Export.CaptureHighwayScreenshotCommand", "view.screenshot"),
        ("ShellDialogs.OpenQualityReportCommand", "tools.qualityReport"),
        ("ShellDialogs.OpenChartDiffCommand", "tools.chartDiff"),
        ("ShellDialogs.OpenBatchLintCommand", "tools.batchLint"),
    ];

    public static TheoryData<string, string?> ReachedBefore()
    {
        var data = new TheoryData<string, string?>();
        foreach (var (command, paletteId) in Before) data.Add(command, paletteId);
        return data;
    }

    /// <summary>
    ///     A command the regroup folded into another row: the two FHP generators run through the one
    ///     scope row, which picks between them.
    /// </summary>
    private static readonly Dictionary<string, string> FoldedInto = new(StringComparer.Ordinal)
    {
        ["GenerateFhpCommand"] = "FhpGeneration.GenerateFhpForScopeCommand",
        ["GenerateFhpForSelectionCommand"] = "FhpGeneration.GenerateFhpForScopeCommand",
        // #497: Improve chart and Auto-fix arrangements became the two groups of the one Fix chart dialog; the
        // palette ids stay and open it with their group ticked.
        ["ImproveChartCommand"] = "ShellDialogs.FixChartCommand",
        ["AutoFixChartCommand"] = "ShellDialogs.FixChartCommand",
        // #497: the AI fix rows are jobs of the AI chat panel; Tools keeps the one row that opens it, and the
        // palette ids start the job there. AI settings is the panel's own button (and Settings > AI).
        ["ShellDialogs.AiChart.FixFingeringOnlyCommand"] = "ShowAiChatCommand",
        ["ShellDialogs.AiChart.FixFhpAndFingeringCommand"] = "ShowAiChatCommand",
        ["ShellDialogs.AiChart.FixFhpOnlyCommand"] = "ShowAiChatCommand",
        ["ShellDialogs.AiChart.FixChordNamesCommand"] = "ShowAiChatCommand",
        ["ShellDialogs.AiChart.FixChordNamesMusicalCommand"] = "ShowAiChatCommand",
        ["ShellDialogs.AiChart.OpenAiSettingsCommand"] = "ShowAiChatCommand",
    };

    [Theory]
    [MemberData(nameof(ReachedBefore))]
    public void EveryCommandToolsReachedBefore_IsStillReachedFromAMenu_UnderTheSamePaletteId(string command, string? paletteId)
    {
        var reached = MenuCommands(MainMenuSource.Document().Root!);
        Assert.True(reached.Count > 100, $"only {reached.Count} command rows read from the whole bar");

        var row = FoldedInto.GetValueOrDefault(command, command);
        Assert.True(reached.Contains(row), $"no menu row runs {row} any more — {command} lost its door");

        if (paletteId is null) return;
        var catalog = File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs"));
        var tail = command.Split('.')[^1];
        Assert.Matches($@"""{Regex.Escape(paletteId)}"",[^;]*\b{Regex.Escape(tail)}\b", catalog);
    }

    [Fact]
    public void TheListOfWhatToolsReachedBefore_IsTheWholeOldMenu() =>
        // 43 command bindings in ToolsMenu.axaml before #491; a shorter list would prove less.
        Assert.Equal(43, Before.Select(r => r.Command).Distinct(StringComparer.Ordinal).Count());

    [Fact]
    public void TheOneFhpRow_RunsBothGenerators_ThroughTheScopeItsLabelNames()
    {
        var generate = Group("Menu_GroupGenerate");
        var row = generate.Elements(Ns + "MenuItem").First();
        Assert.Equal("{Binding FhpGeneration.GenerateFhpForScopeCommand}", (string?)row.Attribute("Command"));
        Assert.Equal("{Binding EditingAvailability.GenerateFhpLabel}", (string?)row.Attribute("Header"));

        var maintenance = DispatchProxy.Create<IChartMaintenanceService, CallLog>();
        var selection = new SelectionService();
        var fhp = new FhpGenerationViewModel(maintenance,
            new EditingAvailability(selection, new ProjectState(), FakeLocalization.KeysOnly()));

        fhp.GenerateFhpForScopeCommand.Execute(null);
        selection.SelectedNotes.Add(new GuitarNote());
        fhp.GenerateFhpForScopeCommand.Execute(null);

        Assert.Equal(
            [nameof(IChartMaintenanceService.GenerateFhp), nameof(IChartMaintenanceService.GenerateFhpForSelection)],
            ((CallLog)(object)maintenance).Calls);
    }

    /// <summary>Records which members of an interface were called, in order.</summary>
    public class CallLog : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            return null;
        }
    }

    [Fact]
    public void Tools_ListsItsGroupsInTheOrderAChartIsMade()
    {
        var keys = Tools().Elements(Ns + "MenuItem").Select(Key).ToList();

        // Separate stems moved to File ▸ Audio and Screenshot highway to File ▸ Export (#494).
        Assert.Equal(
            ["Menu_GroupGenerate", "Menu_GroupFixChart", "Menu_PanelAiChat", "Menu_GroupRepeats", "Menu_GroupTones",
                "Menu_ShowlightsEditor", "Menu_ChartDiff", "Menu_BatchLint"], keys);
    }

    [Fact]
    public void NothingUnderTools_IsDeeperThanOneSubmenu()
    {
        var nested = Tools().Elements(Ns + "MenuItem")
            .SelectMany(group => group.Elements(Ns + "MenuItem"))
            .Where(row => row.Elements(Ns + "MenuItem").Any())
            .Select(Key)
            .ToList();

        Assert.True(nested.Count == 0, "a Tools row opens a third level: " + string.Join(", ", nested));
    }

    public static TheoryData<string, string[]> Groups() => new()
    {
        {
            "Menu_GroupGenerate",
            [
                "FhpGeneration.GenerateFhpForScopeCommand", "GenerateHandShapeCommand", "GenerateArpeggioCommand",
                "GenerateHandShapesForArrangementCommand", "AcceptSuggestedHandShapesCommand",
                "AcceptSuggestedArpeggiosCommand", "ApplyHandShapeTemplateToChordsCommand", "GenerateStructureCommand",
                "ShellDialogs.OpenLevelGenerationCommand",
            ]
        },
        {
            "Menu_GroupFixChart",
            [
                "ShellDialogs.OpenQualityReportCommand", "ShellDialogs.FixChartCommand",
                "ApplyDlcBuilderFixesCommand", "RefretCommand", "RemoveDoubleStopChordNamesCommand", "SquashLevelsCommand",
            ]
        },
        {
            "Menu_GroupRepeats",
            [
                "PropagatePhraseCommand", "ChartEditor.LinkedMeasures.LinkIdenticalCommand",
                "ShellDialogs.OpenLinkGroupsCommand",
            ]
        },
        {
            "Menu_GroupTones",
            [
                "ShellDialogs.OpenToneDesignerCommand", "ToneRig.OpenPedalboardCommand",
                "ToneRig.GenerateRocksmithTonesCommand", "ShellDialogs.OpenTunerCommand",
            ]
        },
    };

    [Fact]
    public void Tools_HoldsOneAiRow_ThatOpensTheChat_AndNoAiFixRow()
    {
        var row = Tools().Elements(Ns + "MenuItem").Single(e => Key(e) == "Menu_PanelAiChat");
        Assert.Equal("ShowAiChatCommand", Command(row));
        Assert.Empty(row.Elements(Ns + "MenuItem"));

        var commands = MenuCommands(MainMenuSource.Document().Root!);
        Assert.DoesNotContain(commands, c => c.StartsWith("ShellDialogs.AiChart.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tools.aiFixFingeringOnly", "FixFingeringOnlyAsync", "FingeringOnly", "Eof")]
    [InlineData("tools.aiFixFhpFingering", "FixFhpAndFingeringAsync", "FhpAndFingering", "Eof")]
    [InlineData("tools.aiFixFhpOnly", "FixFhpOnlyAsync", "FhpOnly", "Eof")]
    [InlineData("tools.aiFixChordNames", "FixChordNamesAsync", "ChordNames", "Eof")]
    [InlineData("tools.aiFixChordNamesMusical", "FixChordNamesMusicalAsync", "ChordNames", "Musical")]
    public void ARemovedAiRowsPaletteId_StillResolves_AndStartsItsJobInTheChat(string id, string method, string job, string style)
    {
        var root = TestPaths.RepoRoot();
        var catalog = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs"));
        Assert.Matches($@"""{Regex.Escape(id)}"",[^;]*\bai\.{Regex.Escape(method)}\b", catalog);

        // the verb is one line: the chat's StartJobAsync with the job the old row ran (and its naming style).
        var verbs = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "ViewModels", "Shell", "AiChartViewModel.cs"));
        var call = style == "Eof" && job != "ChordNames"
            ? $"_chat.StartJobAsync(AiChartJob.{job});"
            : $"_chat.StartJobAsync(AiChartJob.{job}, ChordNamingStyle.{style});";
        Assert.Contains(call, verbs, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSettings_IsAButtonOfTheChatPanel_AndThePaletteIdStays()
    {
        var root = TestPaths.RepoRoot();
        var pane = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "Views", "Editor", "AiChatPaneView.axaml"));
        Assert.Contains("OpenAiSettingsCommand", pane, StringComparison.Ordinal);
        var catalog = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs"));
        Assert.Contains("\"tools.aiSettings\"", catalog, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void EachGroup_HoldsItsRowsInOrder(string groupKey, string[] commands) =>
        Assert.Equal(commands, Group(groupKey).Elements(Ns + "MenuItem").Select(Command));

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void TheRegroupsStrings_AreInEveryLanguage_AndTheFoldedGroupNamesAreGone(string file)
    {
        var data = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

        // The scope row's two labels are named in C#, not in markup, so the menu's own key lint cannot see them.
        foreach (var key in new[] { EditingAvailability.KeyGenerateFhpSelection, EditingAvailability.KeyGenerateFhpArrangement })
            Assert.False(string.IsNullOrWhiteSpace(data.GetValueOrDefault(key)), $"{file} has no text for {key}");
        Assert.NotEqual(data[EditingAvailability.KeyGenerateFhpSelection], data[EditingAvailability.KeyGenerateFhpArrangement]);

        string[] folded =
        [
            "Menu_GroupFhp", "Menu_GroupHandShapes", "Menu_GroupStructureLevels", "Menu_GroupReview",
            "Menu_GroupToneSound", "Menu_GroupTiming",
        ];
        foreach (var gone in folded) Assert.False(data.ContainsKey(gone), $"{file} still carries the folded group name {gone}");
    }

    private static XElement Tools() =>
        XDocument.Load(Path.Combine(MainMenuSource.MenusDirectory, "ToolsMenu.axaml")).Root!;

    private static XElement Group(string key) => Tools().Elements(Ns + "MenuItem").Single(e => Key(e) == key);

    private static string Key(XElement item) =>
        Regex.Match((string?)item.Attribute("Header") ?? "", @"Loc (\w+)").Groups[1].Value;

    private static string Command(XElement item) =>
        Regex.Match((string?)item.Attribute("Command") ?? "", @"\{Binding ([\w.]+)").Groups[1].Value;

    private static HashSet<string> MenuCommands(XElement root) =>
        root.Descendants(Ns + "MenuItem").Select(Command).Where(c => c.Length > 0).ToHashSet(StringComparer.Ordinal);
}
