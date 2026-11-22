using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Editing;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Shell;
using System.Xml.Linq;
using Avalonia.Input;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.UI;

public sealed class PickSlideWiringTests
{
    [Fact]
    public void PickSlideHasAUniqueStructuralDefaultAndASettingsRow()
    {
        var property = typeof(KeybindConfig).GetProperty("PickSlide");
        Assert.NotNull(property);
        var config = new KeybindConfig();
        var chord = Assert.IsType<KeyChord>(property.GetValue(config));
        Assert.Equal(new KeyChord(Key.L, KeyModifiers.Control | KeyModifiers.Shift), chord);
        Assert.Single(typeof(KeybindConfig).GetProperties(), p => p.PropertyType == typeof(KeyChord) && Equals(p.GetValue(config), chord));
        var row = Assert.Single(KeybindRowCatalog.Groups.SelectMany(g => g.Rows), r => r.Property == "PickSlide");
        Assert.Equal("Keybind_PickSlide", row.LabelKey);
        Assert.Equal("Keybind_PickSlide_Tip", row.TooltipKey);
    }

    [Fact]
    public void SelectionEditsCommandCallsTheHighwayService()
    {
        var h = new ChartEditHarness();
        var (global, _) = RecordingProxy.Create<IGlobalEditingCoordinator>();
        var (highway, recorder) = RecordingProxy.Create<IHighwayEditingService>();
        var vm = new SelectionEditsViewModel(global, highway, h.Editor.Selection, h.State,
            FakeLocalization.English(), new MatchPatternStore(new FakeConfigurationService()));
        vm.PickSlideCommand.Execute(null);
        Assert.Single(recorder.Invocations, call => call.Method == nameof(IHighwayEditingService.PickSlide));
    }

    [Theory]
    [InlineData("Resources.resx", "Pick slide")]
    [InlineData("Resources.de-DE.resx", "Pick-Slide")]
    [InlineData("Resources.ja-JP.resx", "ピックスクラッチ")]
    public void EveryLocaleHasThePickSlideLabelAndTip(string file, string expected)
    {
        var data = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file)).Root!.Elements("data");
        Assert.Equal(expected, Assert.Single(data, e => (string?)e.Attribute("name") == "Keybind_PickSlide").Element("value")!.Value);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(data, e => (string?)e.Attribute("name") == "Keybind_PickSlide_Tip").Element("value")!.Value));
    }

}
