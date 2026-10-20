using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class TypedContextUiPerfSmokeTests
{
    [Fact]
    public Task PatternEditorUsesTheDialogsSelectedRow() => HeadlessSessionFixture.Dispatch(() =>
    {
        using var scope = App.Services!.CreateScope();
        var vm = (PatternBuilderViewModel)scope.ServiceProvider.GetRequiredService<IPatternBuilderScreen>();
        {
            var first = new MatchPatternRowViewModel(new MatchPattern { Name = "First" }, vm.Choices);
            var second = new MatchPatternRowViewModel(new MatchPattern { Name = "Second" }, vm.Choices);
            vm.SelectedPattern = first;
            var view = new PatternBuilderDialogView { DataContext = vm };
            WithWindow(view, window =>
            {
                var body = view.FindControl<StackPanel>("PatternEditorBody")!;
                var editor = Assert.IsType<StackPanel>(Assert.Single(body.Children));
                Assert.Same(first, editor.DataContext);
                Assert.True(editor.Bounds.Width > 0 && editor.Bounds.Height > 0);
                vm.SelectedPattern = second;
                window.UpdateLayout();
                Assert.Same(second, editor.DataContext);
                vm.SelectedPattern = null;
                Assert.False(body.IsVisible);
            });
        }
    }, CancellationToken.None);

    [Fact]
    public Task RigBrowserSaveDialogAndNamPanelUseTheirOwningViewModels() => HeadlessSessionFixture.Dispatch(() =>
    {
        var rig = App.Services!.GetRequiredService<PedalboardViewModel>();
        var oldBrowser = rig.IsBrowserOpen;
        var oldSave = rig.Browser.IsSaveDialogOpen;
        try
        {
            rig.IsBrowserOpen = true;
            rig.Browser.IsSaveDialogOpen = true;
            Assert.NotNull(rig.Browser.NamBrowser);
            var view = new AmpFxRigView { DataContext = new AmpFxRigViewModel(rig) };
            WithWindow(view, _ =>
            {
                // The Tones view is its own name scope (2026-09-27).
                var drawer = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "RigToneBrowser");
                Assert.Same(rig.Browser, drawer.Child!.DataContext);
                Assert.True(drawer.Bounds.Width > 0 && drawer.Bounds.Height > 0);
                var save = view.FindControl<Border>("RigBrowserSaveDialog")!;
                Assert.Same(rig.Browser, save.Child!.DataContext);
                Assert.True(save.Bounds.Width > 0 && save.Bounds.Height > 0);
                Assert.Same(rig.Browser, view.FindControl<TextBox>("RigSaveNameBox")!.DataContext);
                Assert.Same(rig.Browser.NamBrowser,
                    view.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Name == "NamOnlineTab").DataContext);
            });
        }
        finally { rig.Browser.IsSaveDialogOpen = oldSave; rig.IsBrowserOpen = oldBrowser; }
    }, CancellationToken.None);

    [Fact]
    public Task DisabledEqBandStillDimsAndReenablingRestoresIt() => HeadlessSessionFixture.Dispatch(() =>
    {
        var vm = new EqModuleViewModel(new FakeAudioPlaybackService(), LocalizationService.Instance);
        var view = new EqModuleView { DataContext = vm, Width = 420 };
        WithWindow(view, window =>
        {
            var chip = view.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("EqChip"));
            var band = Assert.IsType<EqBandChipViewModel>(chip.DataContext);
            Assert.True(chip.Bounds.Width > 0 && chip.Bounds.Height > 0);
            if (band.IsEnabled) band.ToggleEnabledCommand.Execute(null);
            window.UpdateLayout();
            Assert.Contains("EqBandOff", chip.Classes);
            Assert.Equal(0.5, chip.Opacity);
            band.ToggleEnabledCommand.Execute(null);
            window.UpdateLayout();
            Assert.DoesNotContain("EqBandOff", chip.Classes);
            Assert.Equal(1, chip.Opacity);
        }, inspectorStyles: true);
    }, CancellationToken.None);

    private static void WithWindow(Control view, Action<Window> assert, bool inspectorStyles = false)
    {
        var window = new Window { Width = 1600, Height = 1100, Content = view };
        if (inspectorStyles)
        {
            window.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/"))
            { Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml") });
        }

        window.Show();
        try { window.UpdateLayout(); assert(window); }
        finally { window.Close(); }
    }
}
