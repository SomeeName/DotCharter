using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Wave 1 of the chart-workflow declutter. Each removed or renamed entry must still be
///     reachable: the Vocals menu folded into Arrangements keeps all four rows, the Tracks popover
///     keeps ONE door to Arrangement settings, the toolbar flyouts stop sharing names with menus,
///     and the command palette still lists every folded verb.
/// </summary>
public sealed class ChartWorkflowDeclutterTests
{
    private static string Root => TestPaths.RepoRoot();

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void VocalsMenuFile_IsGone_AndTheBarHasNoVocalsMenu()
    {
        Assert.False(File.Exists(Path.Combine(Root, "DotCharter.UI", "Views", "Shell", "Menus", "VocalsMenu.axaml")));
        Assert.DoesNotContain("VocalsMenu", Read("DotCharter.UI", "Views", "Shell", "MainMenuView.axaml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Project.NewVocalTrackCommand")]
    [InlineData("Project.DeleteVocalTrackCommand")]
    [InlineData("InsertVocalAtPlayheadCommand")]
    [InlineData("ShellDialogs.OpenVocalTrackSettingsCommand")]
    [InlineData("Project.NewArrangementCommand")]
    [InlineData("ShellDialogs.OpenArrangementSettingsCommand")]
    public void ArrangementsMenu_StillReachesEveryFormerVocalsAndArrangementRow(string command)
    {
        var menu = XDocument.Load(Path.Combine(Root, "DotCharter.UI", "Views", "Shell", "Menus", "ArrangementsMenu.axaml")).Root!;
        Assert.Contains(menu.Descendants(),
            e => e.Name.LocalName == "MenuItem" && ((string?)e.Attribute("Command") ?? "").Contains(command, StringComparison.Ordinal));

        var vocals = menu.Elements().Single(e => ((string?)e.Attribute("Header") ?? "").Contains("Menu_Vocals", StringComparison.Ordinal));
        Assert.True(vocals.Elements().Count(e => e.Name.LocalName == "MenuItem") >= 3, "the Vocals submenu has fewer than three rows");
    }

    [Theory]
    [InlineData("\"vocals.new\"")]
    [InlineData("\"vocals.delete\"")]
    [InlineData("\"vocals.insert\"")]
    [InlineData("\"vocals.settings\"")]
    [InlineData("\"arr.settings\"")]
    public void CommandPalette_StillListsTheFoldedVerbs(string id) =>
        Assert.Contains(id, Read("DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs"), StringComparison.Ordinal);

    [Fact]
    public void TracksPopover_HasOneArrangementSettingsDoor_TheFooterLink()
    {
        var xaml = Read("DotCharter.UI", "Views", "Shell", "Popovers", "TracksPopoverView.axaml");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xaml, "OpenArrangementSettingsCommand"));
        Assert.Contains("Toolbar_Tracks_Manage", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Resources.resx", "Playback speed", "Practice tools")]
    [InlineData("Resources.de-DE.resx", "Wiedergabetempo", "Übungswerkzeuge")]
    [InlineData("Resources.ja-JP.resx", "再生速度", "練習ツール")]
    public void ToolbarFlyouts_NoLongerShareANameWithAMenu(string file, string speed, string practice)
    {
        var data = XDocument.Load(Path.Combine(Root, "DotCharter.Localization", file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
        Assert.Equal(speed, data["Toolbar_TempoFlyout"]);
        Assert.Equal(practice, data["Toolbar_ToolsFlyout"]);
        Assert.False(string.IsNullOrWhiteSpace(data["Dialog_AdvancedMetadata"]));
        Assert.NotEqual(data["Menu_TempoMap"], data["Toolbar_TempoFlyout"]);
        Assert.NotEqual(data["Menu_Tools"], data["Toolbar_ToolsFlyout"]);
        Assert.False(data.ContainsKey("Toolbar_Tracks_Settings"), "the removed Tracks chip's string is still carried");
    }

    [Theory]
    [InlineData("NewProjectDialogView.axaml")]
    [InlineData("SongMetadataDialogView.axaml")]
    public void BothMetadataDialogs_UseTheSharedBlock_AndRepeatNoAdvancedRow(string file)
    {
        var xaml = Read("DotCharter.UI", "Views", "Dialogs", file);
        Assert.Contains("SongNamesFieldsBlock", xaml, StringComparison.Ordinal);
        foreach (var gone in new[] { "Dialog_DlcKey", "Dialog_ArtistSort", "Inspector_Project_JapaneseArtist", "Dialog_ShowSortFields" })
            Assert.DoesNotContain(gone, xaml, StringComparison.Ordinal);
    }
}
