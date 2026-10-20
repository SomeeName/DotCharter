using System.Xml.Linq;
using DotCharter.UI.Views.Shell.Menus;

namespace DotCharter.Tests.UI;

/// <summary>
///     The menu bar is one file per top-level menu (#653): <c>MainMenuView.axaml</c>
///     lists the menus, <c>Views/Shell/Menus/&lt;Name&gt;.axaml</c> holds each one.
///     These pin the SHAPE every menu file must keep for the bar to look and
///     behave as the single file did — the parts a build does not check.
/// </summary>
public sealed class MainMenuFilesTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheBar_ListsEightMenus_AndHoldsNoRowItself()
    {
        Assert.Equal(
            ["FileMenu", "EditMenu", "TempoMapMenu", "ArrangementsMenu", "VocalsMenu", "ToolsMenu", "ViewMenu", "HelpMenu"],
            MainMenuSource.MenuFiles().Select(Path.GetFileNameWithoutExtension));

        // A row added to the bar file is a row on no menu's file — and the next
        // reader looks for it in the wrong place.
        var bar = XDocument.Load(MainMenuSource.BarFile);
        Assert.DoesNotContain(bar.Descendants(), e => e.Name.LocalName == "MenuItem");
    }

    /// <summary>
    ///     Each file's root is the top-level header itself: a <c>MenuItem</c>
    ///     wearing <c>FireMenuTop</c>, bound against the shell's view model, with
    ///     the code-behind class named after the file. No <c>x:Name</c>: a menu
    ///     file is its own namescope, so a name in it is invisible to
    ///     <c>MainMenuView.FindControl</c> and to every test that looks there.
    /// </summary>
    [Fact]
    public void EveryMenuFile_IsATopLevelHeaderOnTheShellViewModel()
    {
        foreach (var file in MainMenuSource.MenuFiles())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var root = XDocument.Load(file).Root!;

            Assert.Equal("MenuItem", root.Name.LocalName);
            Assert.Equal($"DotCharter.UI.Views.Shell.Menus.{name}", (string?)root.Attribute(Xaml + "Class"));
            Assert.Equal("viewModels:MainWindowViewModel", (string?)root.Attribute(Xaml + "DataType"));
            Assert.Equal("FireMenuTop", (string?)root.Attribute("Classes"));
            Assert.StartsWith("{localization:Loc Menu_", (string?)root.Attribute("Header") ?? "", StringComparison.Ordinal);
            Assert.True(root.Elements().Count(e => e.Name.LocalName == "MenuItem") >= 3,
                $"{name} carries fewer than three rows — the menu's rows did not come with it");

            var named = root.DescendantsAndSelf().Where(e => e.Attribute(Xaml + "Name") is not null)
                .Select(e => (string?)e.Attribute(Xaml + "Name"))
                .ToList();
            Assert.True(named.Count == 0, $"{name} declares x:Name {string.Join(", ", named)} — out of the bar's namescope");
        }
    }

    /// <summary>
    ///     …and each class is an <see cref="EditorTopMenu" />, whose style key is
    ///     <c>MenuItem</c>. A menu class that derived from <c>MenuItem</c> directly
    ///     would find no control theme: no template, a header that renders as
    ///     nothing. (That half is measured by
    ///     <c>Perf/MainMenuDropdownUiPerfSmokeTests</c>.)
    /// </summary>
    [Fact]
    public void EveryMenuClass_KeepsTheMenuItemStyleKey()
    {
        foreach (var file in MainMenuSource.MenuFiles())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var type = typeof(EditorTopMenu).Assembly.GetType($"DotCharter.UI.Views.Shell.Menus.{name}");
            Assert.True(type is not null, $"no class for {name}.axaml");
            Assert.True(type!.IsSubclassOf(typeof(EditorTopMenu)), $"{name} is not an EditorTopMenu");
        }

        var styleKey = typeof(EditorTopMenu).GetProperty("StyleKeyOverride",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(typeof(EditorTopMenu), styleKey.DeclaringType);
    }
}
