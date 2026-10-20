using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     What the last update check found sits in the row whose button checks. It sat below the
///     "check on startup" toggle, where "Update v0.63.0 is available." read as that toggle's note
///     (gallery capture, 2026-09-24).
/// </summary>
public sealed class UpdatesStatusPlacementTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheCheckStatus_LivesInTheCheckRow()
    {
        var doc = XDocument.Load(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "UpdatesSettingsView.axaml")));
        var status = doc.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "UpdateCheckStatus");
        Assert.True(status != null, "the update status line lost its name; re-read this test");
        var row = status!.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Grid" && ((string?)a.Attribute("Classes") ?? "").Contains("FormRow"));
        Assert.True(row != null, "the update status line is not inside a settings row");
        Assert.Contains(row!.Descendants(), e => (string?)e.Attribute("Command") == "{Binding CheckForUpdatesCommand}");
    }
}
