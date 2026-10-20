using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI;
using DotCharter.UI.Layout;
using DotCharter.UI.ViewModels.CommandPalette;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The AI chat pane DOCKED in the real shell (#559): the editor's right-hand column at
///     1920x1080 and 1280x720 (the editor keeps its floor, nothing runs off the window); the
///     toolbar toggle lit while it shows and its keybind and palette entry; the splitters the
///     owner could not see — the chat's, the Inspector / 3D preview one and the timeline / dock
///     one — drawn, hit-testable, and the chat's resizes its column; and the history dropdown with
///     a search and a delete that asks, over the REAL per-chart store in a temp project folder.
///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> each state is captured.
/// </summary>
[Collection("HeadlessUI")]
public sealed class AiChatDockUiPerfSmokeTests
{
    private static readonly (int Width, int Height)[] Sizes = [(1920, 1080), (1280, 720)];

    [Fact]
    public Task ThePane_DocksOnTheRight_TheFullHeight_AtBothSizes() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, vm) =>
        {
            vm.IsAiChatVisible = true;
            foreach (var (width, height) in Sizes)
            {
                window.Width = width;
                window.Height = height;
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"ai-chat-shell-{width}x{height}");
                var at = $"{width}x{height}";

                var host = NarrowWidthLayout.Named<Panel>(window, "AiChatDockHost");
                var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
                Assert.True(host.IsEffectivelyVisible, $"{at}: the chat column is hidden");
                Assert.True(host.Bounds.Width >= AiChatColumnPolicy.MinWidthPx - 1, $"{at}: the chat column is {host.Bounds.Width:0} px");
                Assert.True(Math.Abs(host.Bounds.Height - split.Bounds.Height) < 1, $"{at}: the chat column is not the editor's full height");
                Assert.True(NarrowWidthLayout.Span(host, window).Right <= width + 0.5, $"{at}: the chat column runs off the window");
                Assert.True(split.ColumnDefinitions[0].ActualWidth >= AiChatColumnPolicy.MinEditorWidthPx - 1,
                    $"{at}: the editor kept only {split.ColumnDefinitions[0].ActualWidth:0} px");

                var pane = window.GetVisualDescendants().OfType<AiChatPaneView>().Single();
                Assert.Same(vm.AiChatPane!.Chat, pane.DataContext);
                Assert.True(pane.Bounds is { Width: > 300, Height: > 300 }, $"{at}: the docked pane laid out as {pane.Bounds}");
                foreach (var name in new[] { "AiChatHead", "AiChatComposer", "AiChatInput", "AiChatJob", "AiChatScope", "AiChatModel" })
                {
                    var part = NarrowWidthLayout.Named<Control>(pane, name);
                    Assert.True(part.IsEffectivelyVisible && part.Bounds is { Width: > 1, Height: > 1 }, $"{at}: {name} laid out as {part.Bounds}");
                }

                var inspector = window.GetVisualDescendants().OfType<InspectorView>().Single();
                var inspectorLeft = NarrowWidthLayout.Span(inspector, split).Left;
                Assert.True(inspectorLeft >= -0.5, $"{at}: the inspector starts {-inspectorLeft:0} px left of the editor: clipped");
            }
        }), CancellationToken.None);

    [Fact]
    public Task TheToolbarToggle_IsLitWhileThePaneShows_AndItsKeybindAndPaletteEntryToggleIt() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, vm) =>
        {
            NarrowWidthLayout.Settle(window);
            var toggle = window.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "ToolbarAiChatToggle");
            var host = NarrowWidthLayout.Named<Panel>(window, "AiChatDockHost");
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            Assert.False(toggle.IsChecked);
            Assert.False(host.IsVisible);
            Assert.Equal(0, split.ColumnDefinitions[2].ActualWidth);
            Assert.Equal(LocalizationService.Instance["Main_ToggleAiChat"], Avalonia.Automation.AutomationProperties.GetName(toggle));
            NarrowWidthLayout.Capture(window, "ai-chat-toggle-off");

            Click(window, toggle);
            Assert.True(vm.IsAiChatVisible, "clicking the toolbar toggle did not show the pane");
            Assert.True(toggle.IsChecked);
            Assert.True(host.IsEffectivelyVisible && host.Bounds.Width >= AiChatColumnPolicy.MinWidthPx - 1, $"the column is {host.Bounds}");
            NarrowWidthLayout.Capture(window, "ai-chat-toggle-on");

            Click(window, toggle);
            Assert.False(vm.IsAiChatVisible);
            Assert.False(toggle.IsChecked);
            Assert.Equal(0, split.ColumnDefinitions[2].ActualWidth);

            // Its keybind: Ctrl+Shift+A by default, through the shell's one key route.
            Assert.Equal(new KeyChord(Key.A, KeyModifiers.Control | KeyModifiers.Shift), vm.ProjectState.Keybinds.ToggleAiChat);
            split.Focus();
            window.KeyPress(Key.A, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.A, null);
            NarrowWidthLayout.Settle(window);
            Assert.True(vm.IsAiChatVisible, "Ctrl+Shift+A did not show the pane");
            Assert.True(toggle.IsChecked);

            // …and the command palette's entry.
            var entry = CommandCatalog.Build(vm, LocalizationService.Instance).Single(c => c.Id == "view.toggleAiChat");
            entry.Execute();
            NarrowWidthLayout.Settle(window);
            Assert.False(vm.IsAiChatVisible);
        }), CancellationToken.None);

    [Theory]
    [InlineData("AiChatSplitter")]
    [InlineData("InspectorPreviewSplitter")]
    [InlineData("DockRowSplitter")]
    public Task EveryEditorSplitter_IsADrawnSeam_ThatThePointerFinds(string name) =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, vm) =>
        {
            vm.IsAiChatVisible = true;
            window.Width = 1920;
            window.Height = 1080;
            NarrowWidthLayout.Settle(window);
            var splitter = NarrowWidthLayout.Named<GridSplitter>(window, name);
            Assert.True(splitter.IsEffectivelyVisible, $"{name} is hidden");
            Assert.True(Math.Min(splitter.Bounds.Width, splitter.Bounds.Height) >= 5, $"{name} is {splitter.Bounds}, too thin to grab");
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background);
            Assert.True(brush.Color.A * brush.Opacity > 100, $"{name} is drawn in {brush.Color} at {brush.Opacity}: nothing to see");

            var centre = Centre(splitter, window);
            var hit = window.InputHitTest(centre) as Visual;
            Assert.True(hit is not null && (ReferenceEquals(hit, splitter) || hit.GetVisualAncestors().Contains(splitter)),
                $"the pointer over {name}'s centre finds {hit?.GetType().Name ?? "nothing"}");
        }), CancellationToken.None);

    [Fact]
    public Task DraggingTheChatSplitter_ResizesTheChatColumn() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, vm) =>
        {
            vm.IsAiChatVisible = true;
            window.Width = 1920;
            window.Height = 1080;
            NarrowWidthLayout.Settle(window);
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var splitter = NarrowWidthLayout.Named<GridSplitter>(window, "AiChatSplitter");
            var before = split.ColumnDefinitions[2].ActualWidth;

            var from = Centre(splitter, window);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from.WithX(from.X - 60));
            window.MouseMove(from.WithX(from.X - 120));
            window.MouseUp(from.WithX(from.X - 120), MouseButton.Left);
            NarrowWidthLayout.Settle(window);

            var after = split.ColumnDefinitions[2].ActualWidth;
            NarrowWidthLayout.Report($"chat column {before:0} px -> {after:0} px after a 120 px drag");
            Assert.True(Math.Abs(after - before - 120) < 12, $"a 120 px drag moved the chat column from {before:0} to {after:0} px");
            NarrowWidthLayout.Capture(window, "ai-chat-splitter-dragged");
        }), CancellationToken.None);

    [Fact]
    public Task TheHistoryDropdown_SearchesThisChartsChats_AndDeleteAsksFirst() =>
        HeadlessSessionFixture.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "DotCharterTests", "ai-chat-dock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var config = services.GetRequiredService<IConfigurationService>().LoadAppConfig();
            var previousAi = config.Ai;
            var state = services.GetRequiredService<ProjectState>();
            var previousPath = state.CurrentProjectPath;
            try
            {
                await WithShellAsync(async (window, vm) =>
                {
                    var chat = vm.AiChatPane!.Chat;
                    config.Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-sonnet-5-5", null);
                    chat.RefreshProvider();
                    state.CurrentProjectPath = Path.Combine(folder, "fixture.rscp");
                    vm.IsAiChatVisible = true;
                    window.Width = 1920;
                    window.Height = 1080;

                    // Four chats written straight to the store: Send would run a real chart pass.
                    var store = services.GetRequiredService<IAiChatHistory>();
                    var at = new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero);
                    foreach (var message in new[]
                             {
                                 "Finger the chorus barre chords with the index barre",
                                 "What is wrong in bar 34?",
                                 "Why is the hand in 5th position for the verse riff?",
                                 "Mark the sections: intro, verse, chorus, solo"
                             })
                    {
                        at = at.AddMinutes(7);
                        var conversation = store.StartNew("claude-sonnet-5-5")!
                            .Append(new AiChatTurn(AiChatRole.User, message, at));
                        Assert.Equal(AiChatStatus.Ok, (await store.SaveAsync(conversation)).Status);
                    }

                    NarrowWidthLayout.Settle(window);
                    var pane = window.GetVisualDescendants().OfType<AiChatPaneView>().Single();
                    Click(window, NarrowWidthLayout.Named<ToggleButton>(pane, "AiChatHistoryToggle"));
                    await ((AiChatHistoryViewModel)chat.History).Loading;
                    NarrowWidthLayout.Settle(window);
                    Assert.True(chat.History.IsOpen, "clicking the title did not open the dropdown");
                    Assert.Equal(4, chat.History.Entries.Count);

                    // The search box has the keyboard: typing narrows the list.
                    var popup = NarrowWidthLayout.Named<Popup>(pane, "AiChatHistoryPopup");
                    var search = NarrowWidthLayout.Named<TextBox>(Assert.IsAssignableFrom<Control>(popup.Child), "AiChatHistorySearch");
                    Assert.True(search.IsFocused, "the dropdown opened without the keyboard in its search box");
                    window.KeyTextInput("bar");
                    NarrowWidthLayout.Settle(window);
                    Assert.Equal("bar", chat.History.SearchText);
                    Assert.Equal(["What is wrong in bar 34?", "Finger the chorus barre chords with the index barre"],
                        chat.History.Entries.Select(e => e.Title));
                    NarrowWidthLayout.Capture(window, "ai-chat-shell-history-search");

                    // Delete asks first; the file stays until the answer is yes.
                    var doomed = chat.History.Entries[0];
                    doomed.DeleteCommand.Execute(null);
                    NarrowWidthLayout.Settle(window);
                    var prompt = popup.Child!.GetVisualDescendants().OfType<TextBlock>()
                        .Single(t => t.Text == doomed.DeletePromptText);
                    Assert.True(prompt.IsEffectivelyVisible && prompt.Bounds.Height > 1, "the delete question is not shown");
                    var files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
                    Assert.Equal(4, files.Length);
                    NarrowWidthLayout.Capture(window, "ai-chat-shell-delete-confirm");

                    await ((IAsyncRelayCommand)doomed.ConfirmDeleteCommand).ExecuteAsync(null);
                    NarrowWidthLayout.Settle(window);
                    Assert.Equal(3, Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).Length);
                    Assert.Equal(["Finger the chorus barre chords with the index barre"], chat.History.Entries.Select(e => e.Title));
                    chat.History.IsOpen = false;
                    chat.NewChatCommand.Execute(null);
                });
            }
            finally
            {
                config.Ai = previousAi;
                RefreshTheChatProvider(services);
                state.CurrentProjectPath = previousPath;
                BestEffortDelete.Directory(folder);
            }
        }, CancellationToken.None);

    private static void RefreshTheChatProvider(IServiceProvider services) =>
        services.GetRequiredService<MainWindowViewModel>().AiChatPane?.Chat.RefreshProvider();

    private static Point Centre(Visual control, Visual window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control)
    {
        var at = Centre(control, window);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        NarrowWidthLayout.Settle(window);
    }

    private static void WithShell(Action<MainWindow, MainWindowViewModel> body) =>
        WithShellAsync((window, vm) =>
        {
            body(window, vm);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

    private static async Task WithShellAsync(Func<MainWindow, MainWindowViewModel, Task> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();
        foreach (var dialog in vm.Dialogs.AllDialogs)
        {
            if (dialog.IsOpen) dialog.RequestClose();
        }

        vm.CommandPalette.Close();
        vm.IsGameplayPresentation = false;
        vm.SelectedMainTabIndex = 0;
        vm.IsInspectorVisible = true;
        vm.IsPreviewVisible = true;
        vm.IsSheetMusicVisible = false;
        vm.IsAiChatVisible = false;
        var previous = (state.ActiveProject, state.ActiveArrangement);
        NarrowWidthLayout.SeedSong(state);
        selection.ClearSelections();

        // No window.Close(): closing the SHELL runs the app's shutdown path (see EditorShortWindowUiPerfSmokeTests).
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            await body(window, vm);
        }
        finally
        {
            vm.IsAiChatVisible = false;
            state.ActiveProject = previous.ActiveProject;
            state.ActiveArrangement = previous.ActiveArrangement;
        }
    }
}
