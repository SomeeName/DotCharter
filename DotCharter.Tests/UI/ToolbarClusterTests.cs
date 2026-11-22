using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     #492: the chart editor toolbar's shape. One playback cluster on the left (the
///     transport, loop, metronome, claps, the chart synth and a speed chip), then Edit, then
///     View, and on the right ONE tone-preview split button, the tuner, the panel toggles as
///     their own group and an Export button. These are the structural halves; the layout and
///     the open popups are measured by <c>Perf/ToolbarClusterUiPerfSmokeTests</c>.
/// </summary>
public sealed class ToolbarClusterTests
{
    private static string ShellPath(params string[] parts) => UiSourceTree.Resolve(
        Path.Combine([TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", .. parts]));

    private static string Toolbar() => File.ReadAllText(ShellPath("ToolbarView.axaml"));

    private static string BindingPath(string? binding)
        => Regex.Match(binding ?? "", @"^\{Binding\s+(?:Path=)?([\w.]+)") is { Success: true } m ? m.Groups[1].Value : "";

    private static string Element(string markup, string closing, string marker)
    {
        var at = markup.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{marker} is gone from the toolbar");
        return markup[at..markup.IndexOf(closing, at, StringComparison.Ordinal)];
    }

    [Fact]
    public void TheGroupsRunPlaybackThenEditThenViewThenTheRightEnd()
    {
        var markup = Toolbar();
        string[] inOrder =
        [
            "Transport.TogglePlayPauseCommand", "Transport.StopCommand", "Transport.ToggleLoopCommand",
            "Transport.MetronomeEnabled", "Transport.ClapsEnabled", "Transport.ToggleChartSynthCommand",
            "x:Name=\"TempoFlyoutButton\"",
            "Transport.SelectedGridSnapOption", "ChartEditor.UndoCommand", "ChartEditor.RedoCommand",
            "ZoomOutCommand", "ZoomInCommand", "ProjectState.TimelineFollowsPlayhead", "x:Name=\"TracksFlyoutButton\"",
            "x:Name=\"ToolbarQuickGroup\"",
            "ToneRig.ToggleTonePreviewCommand", "ShellDialogs.OpenTunerCommand",
            "IsInspectorVisible", "IsPreviewVisible", "IsSheetMusicVisible", "IsAiChatVisible",
            "x:Name=\"ExportFlyoutButton\""
        ];
        var last = -1;
        foreach (var token in inOrder)
        {
            var at = markup.IndexOf(token, StringComparison.Ordinal);
            Assert.True(at >= 0, $"the toolbar no longer carries {token}");
            Assert.True(at > last, $"{token} is out of order (playback, edit, view, then the right end)");
            last = at;
        }
    }

    [Fact]
    public void NothingTheToolbarOffered_WentMissing()
    {
        var markup = Toolbar();
        string[] kept =
        [
            "Transport.StopCommand", "Project.SaveProjectCommand", "x:Name=\"MixFlyoutButton\"", "x:Name=\"ToolsFlyoutButton\"",
            "x:Name=\"ToolbarOverflowButton\"", "Transport.MidiEnabled",
            "ChartSynthToolbarToggle", "LoopToolbarToggle", "ToolbarAiChatToggle"
        ];
        foreach (var token in kept) Assert.Contains(token, markup);
    }

    [Fact]
    public void PlayAndPause_AreOneButton_ThatShowsTheStateOfTheTransport()
    {
        var markup = Toolbar();
        Assert.DoesNotContain("Transport.PlayCommand", markup);
        Assert.DoesNotContain("Transport.PauseCommand", markup);
        var button = Element(markup, "</Button>", "x:Name=\"PlayPauseButton\"");
        Assert.Contains("Command=\"{Binding Transport.TogglePlayPauseCommand}\"", button);
        Assert.Contains("AutomationProperties.Name=\"{Binding Transport.PlayPauseLabel}\"", button);
        Assert.Contains("Transport.PlayPauseTip", button);
        Assert.Contains("IconPlay", button);
        Assert.Contains("IconPause", button);
        Assert.Contains("IsVisible=\"{Binding Transport.IsPlaying}\"", button);
        Assert.Contains("IsVisible=\"{Binding !Transport.IsPlaying}\"", button);
    }

    [Fact]
    public void TheSpeedChip_ShowsTheSpeed_AndOpensThePlaybackSpeedPopover()
    {
        var block = Element(Toolbar(), "</Button>", "x:Name=\"TempoFlyoutButton\"");
        Assert.Contains("TempoPopoverView", block);
        Assert.Contains("Transport.PlaybackSpeed", block);
    }

    /// <summary>One click still toggles: the main half is the old toggle's command; the caret opens a
    /// prewarmed popup whose two rows pick the target.</summary>
    [Fact]
    public void ThePreviewIsOneSplitButton_ClickToggles_CaretPicksTheTarget()
    {
        var markup = Toolbar();
        var main = Element(markup, "</ToggleButton>", "x:Name=\"TonePreviewButton\"");
        Assert.Contains("Command=\"{Binding ToneRig.ToggleTonePreviewCommand}\"", main + markup[markup.LastIndexOf("<ToggleButton", markup.IndexOf("x:Name=\"TonePreviewButton\"", StringComparison.Ordinal), StringComparison.Ordinal)..markup.IndexOf("x:Name=\"TonePreviewButton\"", StringComparison.Ordinal)]);
        Assert.DoesNotContain("Flyout", main);

        var caret = Element(markup, "</Button>", "x:Name=\"TonePreviewTargetButton\"");
        Assert.True(markup.IndexOf("x:Name=\"TonePreviewTargetButton\"", StringComparison.Ordinal)
                    > markup.IndexOf("x:Name=\"TonePreviewButton\"", StringComparison.Ordinal), "the caret must follow the main half");
        Assert.Contains("TonePreviewTargetPopoverView", caret);
        Assert.Contains("shell:PrewarmableFlyout", caret);

        var popover = XDocument.Load(ShellPath("Popovers", "TonePreviewTargetPopoverView.axaml"));
        var commands = popover.Descendants().Select(e => BindingPath((string?)e.Attribute("Command"))).Where(c => c.Length > 0).ToList();
        Assert.Equal(["ToneRig.SelectTonePreviewRigCommand", "ToneRig.ToggleTonePreviewRocksmithCommand"], commands);
    }

    /// <summary>The Export button is a second door onto File ▸ Export: the same commands, Rocksmith first.</summary>
    [Fact]
    public void TheExportPopover_RunsTheSameCommandsAsFileExport_RocksmithFirst()
    {
        var file = XDocument.Load(ShellPath("Menus", "FileMenu.axaml"));
        var group = file.Descendants().First(e => e.Name.LocalName == "MenuItem"
            && ((string?)e.Attribute("Header"))?.Contains("Menu_GroupExport", StringComparison.Ordinal) == true);
        var menuCommands = group.Elements().Where(e => e.Name.LocalName == "MenuItem")
            .Select(e => BindingPath((string?)e.Attribute("Command"))).ToList();
        Assert.Equal("ShellDialogs.OpenRocksmithExportCommand", menuCommands[0]);
        Assert.True(menuCommands.Count >= 5, "File > Export lost rows");

        var popover = XDocument.Load(ShellPath("Popovers", "ExportPopoverView.axaml"));
        var rowCommands = popover.Descendants().Where(e => e.Name.LocalName == "Button")
            .Select(e => BindingPath((string?)e.Attribute("Command"))).Where(c => c.Length > 0).ToList();
        Assert.Equal(menuCommands, rowCommands);

        var block = Element(Toolbar(), "</Button>", "x:Name=\"ExportFlyoutButton\"");
        Assert.Contains("ExportPopoverView", block);
        Assert.Contains("shell:PrewarmableFlyout", block);
    }

    [Fact]
    public void ThePanelToggles_AreTheirOwnVisiblySeparateGroup()
    {
        var markup = Toolbar();
        var before = markup[..markup.IndexOf("IsInspectorVisible", StringComparison.Ordinal)];
        Assert.True(before.LastIndexOf("Classes=\"GroupDivider\"", StringComparison.Ordinal)
                    > before.LastIndexOf("ShellDialogs.OpenTunerCommand", StringComparison.Ordinal),
            "the panel toggles are not set off from the tuner by a group divider");
    }

    [Fact]
    public void EveryPopupOnTheToolbar_GoesThroughAPrewarmableFlyout()
    {
        var doc = XDocument.Load(ShellPath("ToolbarView.axaml"));
        var flyouts = doc.Descendants().Where(e => e.Name.LocalName is "Flyout" or "MenuFlyout" or "PrewarmableFlyout").ToList();
        Assert.All(flyouts, f => Assert.Equal("PrewarmableFlyout", f.Name.LocalName));
        Assert.True(flyouts.Count >= 9, $"{flyouts.Count} flyouts: Undo, Redo, Mix, Tempo, Tools, Tracks, Overflow, tone target and Export");
    }
}
