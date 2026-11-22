using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The shape of the Edit menu (#369, #371, #491): Select, Move &amp; resize, Technique and Chord are
///     submenus, the top level stays scannable, and every technique toggle the palette lists is a menu row too.
/// </summary>
public sealed class EditMenuStructureTests
{
    private static readonly XNamespace Ns = "https://github.com/avaloniaui";

    private static XElement Edit() =>
        XDocument.Load(Path.Combine(MainMenuSource.MenusDirectory, "EditMenu.axaml")).Root!;

    private static string Key(XElement item) =>
        Regex.Match((string?)item.Attribute("Header") ?? "", @"Loc (\w+)").Groups[1].Value;

    private static XElement Group(string key) =>
        Edit().Elements(Ns + "MenuItem").Single(e => Key(e) == key);

    [Fact]
    public void The_top_level_lists_the_groups_in_order_within_the_scannable_limit()
    {
        var keys = Edit().Elements(Ns + "MenuItem").Select(Key).ToList();

        Assert.Equal(
            ["Menu_Undo", "Menu_Redo", "History_Menu", "Checkpoints_Menu", "Menu_Cut", "Menu_Copy", "Menu_Paste",
                "Menu_PasteSpecial", "Menu_Select", "Menu_DeleteSelected", "Menu_GroupMoveResize",
                "Menu_GroupTechnique", "Menu_GroupChords", "Menu_Bookmarks"], keys);
        Assert.True(keys.Count <= 14);
    }

    [Fact]
    public void Select_all_select_similar_and_the_pattern_builder_sit_side_by_side_with_the_saved_patterns()
    {
        var keys = Group("Menu_Select").Elements(Ns + "MenuItem").Select(Key).ToList();

        Assert.Equal(["Menu_SelectAll", "Menu_SelectLikePattern", "Menu_PatternBuilder", "Menu_SavedPatterns"], keys);
        var saved = Group("Menu_Select").Elements(Ns + "MenuItem").Single(e => Key(e) == "Menu_SavedPatterns");
        Assert.Contains("SelectionEdits.SavedPatterns", (string?)saved.Attribute("ItemsSource"));
    }

    [Fact]
    public void Move_and_resize_holds_double_and_half_time_since_the_timing_group_folded_into_it()
    {
        var keys = Group("Menu_GroupMoveResize").Elements(Ns + "MenuItem").Select(Key).ToList();

        Assert.Equal(
            ["Menu_SnapSelected", "Menu_SnapAll", "Menu_IncreaseLength", "Menu_DecreaseLength", "Menu_DoubleTime",
                "Menu_HalfTime", "Menu_NudgeInTime"], keys);
        Assert.DoesNotContain(Edit().Descendants(Ns + "MenuItem"), e => Key(e) == "Menu_GroupTiming");
    }

    [Fact]
    public void Chord_holds_the_nine_rows_that_came_from_the_tools_menu()
    {
        var keys = Group("Menu_GroupChords").Elements(Ns + "MenuItem").Select(Key).ToList();

        Assert.Equal(
            ["Menu_PickSlide", "Menu_ConvertToPowerChordSmall", "Menu_ConvertToPowerChordBig", "Menu_MergeNotesIntoChord",
                "Menu_SplitChordsIntoNotes", "Menu_ToggleOctaveFingering", "Menu_TogglePowerChordFingering",
                "Menu_MutedStrumsToChord", "Menu_ToggleGhostGems", "Menu_TappedHarmonic",
                "Menu_GripsFromHeldNotes", "Menu_HandShapesFromLetRing"], keys);
    }

    [Fact]
    public void Every_technique_toggle_the_palette_lists_is_a_row_and_a_group_row_of_the_technique_menu()
    {
        var palette = File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs"));
        var toggles = Regex.Matches(palette, @"""note\.(?<n>toggle(?!Bookmark)\w+?)(?<g>Group)?""")
            .Select(m => (Name: m.Groups["n"].Value, Group: m.Groups["g"].Success))
            .Distinct().ToList();
        Assert.True(toggles.Count >= 20, $"only {toggles.Count} technique toggles parsed from the palette");

        var technique = Group("Menu_GroupTechnique");
        var plain = technique.Elements(Ns + "MenuItem").Where(e => Key(e).StartsWith("Menu_Tech_", StringComparison.Ordinal))
            .Select(e => (string?)e.Attribute("Command")).ToList();
        var grouped = technique.Elements(Ns + "MenuItem").Single(e => Key(e) == "Menu_TechniqueGroup")
            .Elements(Ns + "MenuItem").Select(e => (string?)e.Attribute("Command")).ToList();

        foreach (var (name, isGroup) in toggles)
        {
            // The plain mute command name belongs to a dead command of the pedalboard, so the technique is named apart.
            var verb = name == "toggleMute" && !isGroup ? "ToggleMuteTechnique" : $"{char.ToUpperInvariant(name[0])}{name[1..]}";
            var command = $"{{Binding SelectionEdits.{verb}{(isGroup ? "Group" : "")}Command}}";
            Assert.Contains(command, isGroup ? grouped : plain);
        }
    }

    [Fact]
    public void Every_technique_and_timing_row_is_enabled_only_while_there_is_a_target()
    {
        var rows = Group("Menu_GroupTechnique").Descendants(Ns + "MenuItem")
            .Concat(Group("Menu_GroupMoveResize").Elements(Ns + "MenuItem")
                .Where(e => Key(e) is "Menu_DoubleTime" or "Menu_HalfTime"))
            .Where(e => e.Attribute("Command") is not null)
            .ToList();

        Assert.True(rows.Count >= 22);
        Assert.All(rows, r => Assert.Equal("{Binding SelectionEdits.HasSelection}", (string?)r.Attribute("IsEnabled")));
    }
}
