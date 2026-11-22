using System.Reflection;
using Avalonia.Input;
using Avalonia.Controls;
using DotCharter.UI.Views.Shell.Menus;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.CommandPalette;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class PickSlideDispatchUiPerfSmokeTests
{
    [Theory]
    [InlineData("palette")]
    [InlineData("keybind")]
    [InlineData("menu")]
    [InlineData("context")]
    public Task CommandDoorCallsTheHighwayFake(string door) => HeadlessSessionFixture.Dispatch(() =>
    {
        var shell = App.Services!.GetRequiredService<MainWindowViewModel>();
        var h = new ChartEditHarness();
        var config = new FakeConfigurationService();
        var (global, _) = RecordingProxy.Create<IGlobalEditingCoordinator>();
        var (highway, recorder) = RecordingProxy.Create<IHighwayEditingService>();
        var edits = new SelectionEditsViewModel(global, highway, h.Editor.Selection, shell.ProjectState,
            FakeLocalization.English(), new MatchPatternStore(config));
        var field = typeof(ShellDialogsViewModel).GetField("<SelectionEdits>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = shell.SelectionEdits;
        var project = shell.ProjectState.ActiveProject;
        var arrangement = shell.ProjectState.ActiveArrangement;
        var level = shell.ProjectState.ActiveLevel;
        field.SetValue(shell.ShellDialogs, edits);
        shell.ProjectState.ActiveProject = h.Project;
        shell.ProjectState.ActiveArrangement = h.State.ActiveArrangement;
        shell.ProjectState.ActiveLevel = h.State.ActiveLevel;
        try
        {
            if (door == "palette")
            {
                var entry = Assert.Single(CommandCatalog.Build(shell, FakeLocalization.English()), c => c.Id == "note.pickSlide");
                Assert.True(entry.CanExecute());
                entry.Execute();
            }
            else if (door == "menu")
            {
                var menu = new EditMenu { DataContext = shell };
                var row = Assert.Single(Rows(menu), r => ReferenceEquals(r.Command, edits.PickSlideCommand));
                row.Command!.Execute(null);
            }
            else if (door == "context")
            {
                var catalog = CommandCatalog.Build(shell, FakeLocalization.English());
                var actions = new TimelineContextMenuActions(
                    () => { }, () => { }, () => { }, _ => { }, () => { }, () => { },
                    () => { }, () => { }, () => { }, () => { },
                    Run: id => catalog.Single(c => c.Id == id).Execute(),
                    ChordOf: _ => KeyChord.None, CanRun: _ => true,
                    SelectedNotes: 1, SelectedChords: 0, DisabledReason: _ => null);
                var group = TimelineContextMenuBuilder.ForSound(actions).Single(r => r.LocKey == "Menu_GroupTechnique");
                Assert.Single(group.Children!, r => r.LocKey == "Menu_PickSlide").Invoke!();
            }
            else
            {
                var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
                var registrar = new AppKeybindRegistrar(handler, shell.ProjectState, null!, null!, global, highway,
                    null!, config, null!, null!, null!, null!, shell, null!, new MatchPatternStore(config));
                registrar.RegisterAll();
                handler.RefreshBinds();
                var chord = shell.ProjectState.Keybinds.PickSlide;
                Assert.True(chord.IsBound);
                Assert.True(handler.HandleKeyPress(chord.Key, chord.Modifiers.HasFlag(KeyModifiers.Control),
                    chord.Modifiers.HasFlag(KeyModifiers.Shift), chord.Modifiers.HasFlag(KeyModifiers.Alt)));
            }

            Assert.Single(recorder.Invocations, call => call.Method == nameof(IHighwayEditingService.PickSlide));
        }
        finally
        {
            field.SetValue(shell.ShellDialogs, original);
            shell.ProjectState.ActiveProject = project;
            shell.ProjectState.ActiveArrangement = arrangement;
            shell.ProjectState.ActiveLevel = level;
        }
    }, CancellationToken.None);

    private static IEnumerable<MenuItem> Rows(MenuItem menu)
    {
        yield return menu;
        foreach (var child in menu.Items.OfType<MenuItem>())
        {
            foreach (var row in Rows(child)) yield return row;
        }
    }
}
