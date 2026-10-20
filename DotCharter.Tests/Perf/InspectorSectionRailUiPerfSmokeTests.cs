using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #601: the Mixer, Song and Lyrics tabs wear the Sound tab's icon-only section rail
///     (<see cref="InspectorSectionRail" />). Through the real <see cref="InspectorView" /> over the
///     app's own view models: every entry is named and tipped off ONE key and shows no caption;
///     a click on an entry shows exactly that section, measured non-zero, with its accent bar and
///     no other section; an entry whose section does not apply (Stems without stems, the selected
///     syllable without a selection) is hidden and never the open one; and every section body is
///     listed for the prewarm (<see cref="InspectorView.NestedPaneNames" />).
/// </summary>
[Collection("HeadlessUI")]
public sealed class InspectorSectionRailUiPerfSmokeTests
{
    [Theory]
    [InlineData(InspectorViewModel.MixerTabIndex, "Mixer")]
    [InlineData(2, "Song")]
    [InlineData(3, "Lyrics")]
    public async Task EverySection_OpensAlone_FromItsNamedEntry(int tab, string prefix) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = (App.Services ?? throw new InvalidOperationException("the headless app session did not boot."))
                .GetRequiredService<InspectorViewModel>();
            var previousTab = vm.SelectedTabIndex;
            var view = new InspectorView { DataContext = vm };
            var window = new Window { Width = 600, Height = 524, Content = view };
            window.Show();
            try
            {
                vm.SelectedTabIndex = tab;
                Settle(window);
                var panel = view.FindControl<Grid>("InspectorTabPanels")!.Children.Single(c => c.IsVisible);
                var rail = panel.GetLogicalDescendants().OfType<StackPanel>().Single(s => s.Name == prefix + "RailEntries");
                var entries = rail.Children.OfType<Panel>().ToList();
                Assert.True(entries.Count >= 3, $"the {prefix} rail has {entries.Count} entries — repoint this test");

                var shown = entries.Where(e => e.IsVisible).ToList();
                Assert.NotEmpty(shown);
                foreach (var entry in entries)
                {
                    var toggle = entry.Children.OfType<ToggleButton>().Single();
                    var name = AutomationProperties.GetName(toggle);
                    Assert.False(string.IsNullOrWhiteSpace(name), $"{toggle.Name} has no accessible name");
                    Assert.Equal(name, ToolTip.GetTip(toggle) as string);
                    Assert.DoesNotContain(toggle.GetVisualDescendants(), d => d is TextBlock { IsEffectivelyVisible: true });
                    var body = (string)toggle.Tag!;
                    Assert.Contains(body, InspectorView.NestedPaneNames);
                    if (!entry.IsVisible) Assert.False(toggle.IsChecked == true, $"the hidden entry {toggle.Name} is the open one");
                }

                foreach (var entry in shown)
                {
                    var toggle = entry.Children.OfType<ToggleButton>().Single();
                    toggle.IsChecked = false; // what a click on a lit entry does to it locally
                    toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    Settle(window);
                    foreach (var other in entries)
                    {
                        var otherToggle = other.Children.OfType<ToggleButton>().Single();
                        var open = ReferenceEquals(other, entry);
                        var body = panel.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == (string)otherToggle.Tag!);
                        Assert.Equal(open, otherToggle.IsChecked == true);
                        Assert.Equal(open, body.IsVisible);
                        Assert.Equal(open, other.Children.OfType<Border>().Single().IsVisible);
                        if (open)
                            Assert.True(body.Bounds is { Width: > 1, Height: > 1 }, $"{body.Name} laid out as {body.Bounds}");
                    }
                }
            }
            finally
            {
                vm.SelectedTabIndex = previousTab;
                window.Close();
            }
        }, CancellationToken.None);

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
