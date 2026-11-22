using System.Text.RegularExpressions;
using Avalonia.Input;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI;

public sealed class ChartWorkflowCommandTests
{
    [Theory]
    [InlineData("Menu_SnapSelected", "edit.snapSelected")]
    [InlineData("Menu_IncreaseLength", "edit.increaseLength")]
    [InlineData("Menu_DecreaseLength", "edit.decreaseLength")]
    [InlineData("Menu_NudgeEarlier", "note.nudgeEarlier")]
    [InlineData("Menu_NudgeLater", "note.nudgeLater")]
    [InlineData("Menu_NudgeEarlier10Ms", "note.nudgeEarlier10Ms")]
    [InlineData("Menu_NudgeLater10Ms", "note.nudgeLater10Ms")]
    [InlineData("Menu_NudgeEarlier1Ms", "note.nudgeEarlier1Ms")]
    [InlineData("Menu_NudgeLater1Ms", "note.nudgeLater1Ms")]
    public void SelectionMovementRows_RunTheirExistingVerb_WithItsCurrentShortcut(string key, string id)
    {
        var invoked = new List<string>();
        var rebound = new KeyChord(Key.F9, KeyModifiers.Control);
        var actions = Actions(invoked) with { ChordOf = _ => rebound };
        var item = Flatten(TimelineContextMenuBuilder.ForSound(actions)).SingleOrDefault(i => i.LocKey == key);
        Assert.NotNull(item);
        Assert.Equal(rebound, item.Chord);
        item.Invoke!();
        Assert.Equal([id], invoked);
    }

    [Fact]
    public void UnavailableCommands_StayDiscoverable_AndCannotBeInvoked()
    {
        var invoked = new List<string>();
        var actions = Actions(invoked) with { CanRun = _ => false };
        var rows = Flatten(TimelineContextMenuBuilder.ForSound(actions)).ToList();
        var row = rows.SingleOrDefault(i => i.LocKey == "Menu_Refret");
        Assert.NotNull(row);
        Assert.False(row.IsEnabled);
        Assert.Equal("Unavailable for this selection.", row.DisabledReason);
        row.Invoke?.Invoke();
        Assert.Empty(invoked);
        Assert.Contains(rows, i => i.LocKey == "Menu_MergeNotesIntoChord");
        Assert.Contains(rows, i => i.LocKey == "Menu_SplitChordsIntoNotes");
    }

    [Fact]
    public void TheFhpPaletteEntry_UsesTheSameInPlaceEditorAsTheShortcut()
    {
        var catalog = Source("ViewModels/CommandPalette/CommandCatalog.cs");
        Assert.Matches(new Regex("\\\"tools.editFhp\\\"[^;]+EditFhpAtPlayheadInPlace", RegexOptions.Singleline), catalog);
        Assert.Contains("EditFhpAtPlayheadInPlace()", Source("Input/AppKeybindRegistrar.cs"));
    }

    [Fact]
    public void SaveAndNext_HasARebindableNonCollidingShortcut_RegisteredAndListed()
    {
        var property = typeof(KeybindConfig).GetProperty("SaveAndNextFingering");
        Assert.NotNull(property);
        var config = new KeybindConfig();
        var chord = Assert.IsType<KeyChord>(property.GetValue(config));
        Assert.True(chord.IsBound);
        Assert.DoesNotContain(typeof(KeybindConfig).GetProperties(), p => p != property && p.PropertyType == typeof(KeyChord) && Equals(p.GetValue(config), chord));
        Assert.Contains("Keybinds.SaveAndNextFingering", Source("Input/AppKeybindRegistrar.cs"));
        Assert.Contains("k.SaveAndNextFingering", Source("ViewModels/CommandPalette/CommandCatalog.cs"));
        var rows = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Presentation/Screens/Settings/KeybindRowCatalog.cs"));
        Assert.Contains("nameof(KeybindConfig.SaveAndNextFingering)", rows);

        var state = new ProjectState { Keybinds = config };
        var configuration = new FakeConfigurationService();
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        var registrar = new AppKeybindRegistrar(handler, state, null!, null!, null!, null!, null!,
            configuration, null!, null!, null!, null!, null!, null!, new MatchPatternStore(configuration));
        registrar.RegisterAll();
        handler.RefreshBinds();
        Assert.Equal("SaveAndNextFingering", handler.DescribeBinding(chord));
        var custom = new KeyChord(Key.F24, KeyModifiers.Shift);
        property.SetValue(config, custom);
        handler.RefreshBinds();
        Assert.Equal("SaveAndNextFingering", handler.DescribeBinding(custom));
        Assert.Null(handler.DescribeBinding(chord));
    }

    private static TimelineContextMenuActions Actions(List<string> invoked) => new(
        () => { }, () => { }, () => { }, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
        invoked.Add, _ => KeyChord.None, _ => true, 1, 0, _ => "Unavailable for this selection.");

    private static IEnumerable<TimelineMenuItem> Flatten(IEnumerable<TimelineMenuItem> items) =>
        items.SelectMany(i => i.Children == null ? [i] : Flatten(i.Children).Prepend(i));

    private static string Source(string path) => UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", path));
}
