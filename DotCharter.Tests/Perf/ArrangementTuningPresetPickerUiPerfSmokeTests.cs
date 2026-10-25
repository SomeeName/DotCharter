using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The arrangement dialog's tuning picker (#230) binds the preset KEY as its value and shows
///     the label the screen built for the string count. Built the way the app builds it: a real
///     <see cref="DialogHost" /> inside the real <see cref="DialogContainerView" />, shown and laid out.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ArrangementTuningPresetPickerUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 900;

    [Fact]
    public async Task ThePicker_ShowsTheLabel_AndWritesTheKey()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeArrangementSettingsScreen.Default();
            WithOpenDialog(screen, view =>
            {
                var picker = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "TuningPresetPicker");
                Assert.True(picker.Bounds is { Width: > 1, Height: > 1 }, $"the picker laid out {picker.Bounds}");

                var selected = Assert.IsAssignableFrom<ITuningPresetOption>(picker.SelectedItem);
                Assert.Equal("Drop D", selected.Key);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text == "Drop D (D A D G B E)" && t.Bounds.Width > 1);

                picker.SelectedItem = screen.TuningPresets.Single(o => o.Key == "Eb Standard");
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Eb Standard", screen.TuningPreset);
            });
        }, TestContext.Current.CancellationToken);
    }

    private static void WithOpenDialog(IArrangementSettingsScreen screen, Action<Control> assert)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped(_ => screen);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
            provider.GetRequiredService<DialogsSlot>());
        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(host);
        var window = new Window { Width = HostWidth, Height = HostHeight, Content = container };
        window.Show();
        try
        {
            _ = ((IDialogs)host).ShowAsync<IArrangementSettingsScreen, bool>();
            Dispatcher.UIThread.RunJobs();
            container.Measure(new Size(HostWidth, HostHeight));
            container.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            assert(Assert.Single(host.OpenViews));
            host.CloseTop();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }
}
