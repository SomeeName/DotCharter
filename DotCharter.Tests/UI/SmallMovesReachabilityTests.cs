using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The #494 small moves only MOVE commands: each one is still a menu row, still in the palette under the
///     same id, and the file, tools, tempo, help and edit menus have the shapes the issue names.
/// </summary>
public sealed class SmallMovesReachabilityTests
{
    private static readonly XNamespace Ns = "https://github.com/avaloniaui";

    private static XElement Menu(string file) =>
        XDocument.Load(Path.Combine(MainMenuSource.MenusDirectory, file)).Root!;

    private static string Key(XElement item) =>
        Regex.Match((string?)item.Attribute("Header") ?? "", @"Loc (\w+)").Groups[1].Value;

    private static string Command(XElement item) =>
        Regex.Match((string?)item.Attribute("Command") ?? "", @"\{Binding ([\w.]+)\}").Groups[1].Value;

    private static IEnumerable<XElement> Rows(XElement menu) => menu.Elements(Ns + "MenuItem");

    private static XElement Group(XElement menu, string key) => Rows(menu).Single(e => Key(e) == key);

    private static string Catalog() => File.ReadAllText(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs"));

    private static void AssertPaletteRow(string id, string commandTail) =>
        Assert.Matches($@"""{Regex.Escape(id)}"",[^;]*\b{Regex.Escape(commandTail)}\b", Catalog());

    [Fact]
    public void FileAudio_holds_attach_youtube_and_separate_stems_and_they_left_their_old_places()
    {
        var file = Menu("FileMenu.axaml");
        var audio = Group(file, "Menu_GroupAudio");

        Assert.Equal(["Project.AttachAudioCommand", "Imports.AttachYoutubeAudioCommand", "StemSeparation.SeparateStemsCommand"],
            Rows(audio).Select(Command));
        Assert.DoesNotContain(Rows(file), r => Key(r) == "Menu_AttachAudio");
        Assert.DoesNotContain(Group(file, "Menu_Import").Descendants(Ns + "MenuItem"),
            r => Command(r) == "Imports.AttachYoutubeAudioCommand");
        Assert.DoesNotContain(Menu("ToolsMenu.axaml").Descendants(Ns + "MenuItem"),
            r => Command(r) == "StemSeparation.SeparateStemsCommand");
        AssertPaletteRow("tools.separateStems", "SeparateStemsCommand");
        AssertPaletteRow("file.attachYoutubeAudio", "AttachYoutubeAudioCommand");
    }

    [Fact]
    public void Tools_Tones_still_holds_three_rows_after_separate_stems_left()
    {
        var tones = Group(Menu("ToolsMenu.axaml"), "Menu_GroupTones");

        Assert.True(Rows(tones).Count() >= 3, "a submenu under three rows fails MainMenuLocalizationTests");
    }

    [Fact]
    public void The_screenshot_row_is_in_FileExport_with_F12_and_not_in_Tools()
    {
        var row = Group(Menu("FileMenu.axaml"), "Menu_GroupExport").Elements(Ns + "MenuItem")
            .Single(r => Key(r) == "Menu_Screenshot");

        Assert.Equal("Export.CaptureHighwayScreenshotCommand", Command(row));
        Assert.Equal("F12", (string?)row.Attribute("InputGesture"));
        Assert.DoesNotContain(Menu("ToolsMenu.axaml").Descendants(Ns + "MenuItem"), r => Key(r) == "Menu_Screenshot");
        AssertPaletteRow("view.screenshot", "CaptureHighwayScreenshotCommand");
    }

    [Fact]
    public void TempoMap_has_one_Start_and_end_row_and_the_five_palette_ids_still_open_it()
    {
        var rows = Rows(Menu("TempoMapMenu.axaml")).ToList();

        Assert.Equal("ShellDialogs.OpenStartEndDialogCommand", Command(rows.Single(r => Key(r) == "Menu_StartEnd")));
        Assert.DoesNotContain(rows, r => Key(r) == "Menu_GroupStartEnd");
        AssertPaletteRow("tempo.startEnd", "OpenStartEndDialogCommand");
        AssertPaletteRow("tempo.setDefaultStartSilence", "OpenSetDefaultStartSilenceDialogCommand");
        AssertPaletteRow("tempo.addSilenceAtStart", "OpenAddSilenceAtStartDialogCommand");
        AssertPaletteRow("tempo.addSilenceAtEnd", "OpenAddSilenceAtEndDialogCommand");
        AssertPaletteRow("tempo.addMeasuresAtStart", "OpenAddMeasuresAtStartDialogCommand");
        AssertPaletteRow("tempo.addMeasuresAtEnd", "OpenAddMeasuresAtEndDialogCommand");
    }

    /// <summary>Insert measures at playhead is a playhead verb, so it sits with Insert / Remove beat,
    /// not inside Start &amp; end, and has its own palette id.</summary>
    [Fact]
    public void TempoMap_has_an_Insert_measures_at_playhead_row_and_palette_id()
    {
        var rows = Rows(Menu("TempoMapMenu.axaml")).ToList();

        Assert.Equal("ShellDialogs.OpenInsertMeasuresAtPlayheadDialogCommand",
            Command(rows.Single(r => Key(r) == "Menu_InsertMeasuresAtPlayhead")));
        AssertPaletteRow("tempo.insertMeasures", "OpenInsertMeasuresAtPlayheadDialogCommand");
    }

    [Fact]
    public void Help_opens_on_shortcuts_about_and_updates_with_the_diagnostics_below_a_separator()
    {
        var help = Menu("HelpMenu.axaml");
        var items = help.Elements().Where(e => e.Name.LocalName is "MenuItem" or "Separator")
            .Select(e => e.Name.LocalName == "Separator" ? "-" : Key(e)).ToList();

        Assert.Equal(
            ["Menu_KeyboardShortcuts", "-", "Menu_About", "Menu_License", "-", "Menu_CheckForUpdates", "-",
                "Menu_UsageStatistics", "Menu_SessionHealth"], items);
        AssertPaletteRow("help.usageStatistics", "ShowUsageStatisticsCommand");
        AssertPaletteRow("help.sessionHealth", "ShowDiagnosticsCommand");
    }

    [Fact]
    public void EditBookmarks_is_one_row_that_opens_the_dialog_and_the_digit_toggles_stay_in_the_palette()
    {
        var edit = Menu("EditMenu.axaml");
        var row = Group(edit, "Menu_Bookmarks");

        Assert.Equal("ShellDialogs.OpenBookmarksCommand", Command(row));
        Assert.Empty(row.Elements(Ns + "MenuItem"));
        AssertPaletteRow("edit.bookmarks", "OpenBookmarksCommand");
        Assert.Contains("edit.toggleBookmark.", Catalog());
        Assert.Contains("BookmarkBase", File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Input", "AppKeybindRegistrar.cs")));
    }

    [Fact]
    public void The_Song_tab_ends_with_an_Edit_song_metadata_button_that_runs_the_File_menu_verb()
    {
        var view = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector",
            "SongInspectorView.axaml")).Root!;
        var button = view.Descendants(Ns + "Button")
            .Single(b => (string?)b.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name") == "EditSongMetadataButton");

        Assert.Equal("{Binding OpenSongMetadataCommand}", (string?)button.Attribute("Command"));
        // design D's "More details": the caption is short, the name and the tooltip say what it opens.
        Assert.Contains("Inspector_EditSongMetadata", (string?)button.Attribute("{https://github.com/avaloniaui}AutomationProperties.Name")
            ?? (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("ShellDialogs.OpenSongMetadataCommand",
            Command(Rows(Menu("FileMenu.axaml")).Single(r => Key(r) == "Menu_SongMetadata")));
        Assert.Contains("Inspector.SongOverview.OpenSongMetadataCommand = shellDialogs.OpenSongMetadataCommand",
            File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell",
                "MainWindowViewModel.cs")));
    }

    /// <summary>
    ///     <c>Preview3DView</c> hosts the Vulkan viewport and is never built headless (UiSweepCatalog.Exclusions),
    ///     so the button is pinned by its markup; the command it binds is the one File ▸ Export runs and F12 drives,
    ///     tested by <c>ExportMenuViewModelTests</c>.
    /// </summary>
    [Fact]
    public void The_3D_preview_strip_has_a_camera_button_on_the_screenshot_command_with_F12_in_its_tip()
    {
        var view = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Preview3DView.axaml")).Root!;
        var button = view.Descendants(Ns + "Button")
            .Single(b => (string?)b.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name") == "ScreenshotButton");

        Assert.Equal("Export.CaptureHighwayScreenshotCommand", Command(button));
        Assert.Contains("Preview3D_Screenshot_Tip", (string?)button.Attribute("ToolTip.Tip"));
        Assert.Contains("Menu_Screenshot", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Matches(@"name=""Preview3D_Screenshot_Tip""[^>]*><value>[^<]*F12", File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx")));
    }
}
