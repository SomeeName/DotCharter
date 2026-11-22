using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Settings;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.Views.Shell.Popovers;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>Construction and optional before/after captures for #532's longer chart-editor labels.</summary>
[Collection("HeadlessUI")]
public sealed class ChartEditorWordingUiPerfSmokeTests
{
    [Theory]
    [InlineData("arrangements")]
    [InlineData("mixer-mute")]
    [InlineData("mixer-solo")]
    [InlineData("claps")]
    [InlineData("ignore")]
    [InlineData("audio-check")]
    [InlineData("task")]
    [InlineData("phrases")]
    [InlineData("phrases-confirm")]
    [InlineData("event")]
    [InlineData("midi-import")]
    [InlineData("keybind-arrangements")]
    [InlineData("keybind-tools")]
    public async Task ChangedWordingSurfaces_AttachAndMeasure(string surface) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("The headless app did not boot.");
            UiSweepDataContexts.Build(services);
            var shell = services.GetRequiredService<MainWindowViewModel>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            using var screenScope = services.CreateScope();
            if (surface == "task")
            {
                services.GetRequiredService<ISelectionService>().ClearSelections();
                InspectorCapture.InspectorCaptureFixture.Seed(shell.ProjectState);
                inspector.Queue.Refresh();
            }
            if (surface == "audio-check")
            {
                shell.ProjectState.ActiveArrangement = null;
                inspector.IssuesSections.Open(IssuesSection.Project);
                inspector.AudioLint.RunCommand.Execute(null);
                Assert.True(inspector.AudioLint.HasResult);
            }
            if (surface == "ignore")
                services.GetRequiredService<ISelectionService>().SelectedNotes.Add(shell.ProjectState.ActiveArrangement!.Levels[0].Notes[0]);
            var (view, width, height) = Build(surface, shell, inspector, screenScope.ServiceProvider);
            var content = view;
            if (surface is "arrangements" or "mixer-mute" or "mixer-solo") content = new FlyoutPresenter { Content = view };
            if (surface is "event" or "midi-import")
            {
                var container = new DialogContainerView();
                container.DialogPanel.Children.Clear();
                container.DialogPanel.Children.Add(view);
                content = container;
            }
            var host = new UserControl { Content = content };
            if (surface is "task" or "phrases" or "phrases-confirm" or "ignore" or "audio-check")
            {
                host.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/"))
                {
                    Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
                });
            }

            var window = new Window
            {
                Width = width, Height = height, Content = host, DataContext = shell,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x19))
            };
            window.Show();
            Control? tipAnchor = null;
            try
            {
                window.UpdateLayout();
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{surface} measured {view.Bounds}.");
                if (surface == "task")
                    Assert.True(view.FindControl<Button>("SoundTaskFixNext") is { IsEffectivelyVisible: true, Bounds.Width: > 1 });
                var tipKey = surface switch
                {
                    "mixer-mute" => "Inspector_MuteMidi",
                    "mixer-solo" => "Inspector_SoloMidi",
                    "claps" => "Toolbar_ToggleClaps",
                    "ignore" => "Inspector_Ignore",
                    _ => null
                };
                if (tipKey != null)
                {
                    var label = services.GetRequiredService<DotCharter.Localization.ILocalizationService>()[tipKey];
                    tipAnchor = view.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetName(c) == label);
                    ToolTip.SetIsOpen(tipAnchor, true);
                }
                if (!UiSweepRunner.CaptureEnabled) return;
                var directory = Environment.GetEnvironmentVariable("DOTCHARTER_WORDING_CAPTURE_DIR");
                if (string.IsNullOrEmpty(directory)) return;
                Directory.CreateDirectory(directory);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, surface + ".png"), new PngBitmapEncoderOptions());
            }
            finally
            {
                if (tipAnchor != null) ToolTip.SetIsOpen(tipAnchor, false);
                window.Close();
                if (surface == "ignore") services.GetRequiredService<ISelectionService>().ClearSelections();
                if (surface == "phrases-confirm") inspector.Phrases.Refresh();
            }
        }, CancellationToken.None);

    private static (Control View, double Width, double Height) Build(string surface, MainWindowViewModel shell,
        InspectorViewModel inspector, IServiceProvider services) =>
        surface switch
        {
            "arrangements" => (new TracksPopoverView { DataContext = shell }, 376, 900),
            "mixer-mute" or "mixer-solo" => (new MixPopoverView { DataContext = shell }, 600, 1000),
            "claps" => (new ToolbarView { DataContext = shell }, 1600, 250),
            "ignore" => (new SoundNoteDetailView { DataContext = inspector }, 650, 900),
            "audio-check" => (new IssuesInspectorView { DataContext = inspector }, 600, 900),
            "task" => (new SoundTaskView { DataContext = inspector }, 420, 800),
            "phrases" or "phrases-confirm" => (Phrases(inspector, surface), 420, 900),
            "event" => (new EventDialogView { DataContext = FakeEventScreen.Default() }, 900, 900),
            "midi-import" => (new MidiImportDialogView { DataContext = FakeMidiImportScreen.Default() }, 900, 700),
            "keybind-arrangements" or "keybind-tools" => (Keybinds(services, surface), 1000, 900),
            _ => throw new ArgumentOutOfRangeException(nameof(surface))
        };

    private static Control Phrases(InspectorViewModel inspector, string surface)
    {
        inspector.Phrases.Refresh();
        var repeat = inspector.Phrases.Phrases.SelectMany(p => p.Iterations).First(r => r.CanPropagate);
        if (surface == "phrases-confirm") inspector.Phrases.PropagateCommand.Execute(repeat);
        return new PhrasesInspectorView { DataContext = inspector.Phrases };
    }

    private static Control Keybinds(IServiceProvider services, string surface)
    {
        var screen = Assert.IsType<DotCharter.Presentation.Screens.Settings.KeybindsSettingsViewModel>(
            services.GetRequiredService<DotCharter.Presentation.Contracts.Settings.IKeybindsSettingsScreen>());
        var loc = services.GetRequiredService<DotCharter.Localization.ILocalizationService>();
        screen.ApplySearch(loc[surface == "keybind-tools" ? "Keybind_OpenToolsPopover" : "Keybind_OpenTracksPopover"]);
        return new KeybindsSettingsView { DataContext = screen };
    }
}
