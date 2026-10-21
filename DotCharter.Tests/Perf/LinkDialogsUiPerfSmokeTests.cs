using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Linking;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.State.Linking;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The linked-measures dialogs (the import's summary and Tools ▸ Linked measures), built the way
///     the app builds them: a real <see cref="DialogHost" /> inside the real
///     <see cref="DialogContainerView" />, in a shown window, laid out. Every named part must MEASURE —
///     a view whose styles failed still arranges, with no size — on the real view models over a
///     linked chart and on every fake variant.
/// </summary>
[Collection("HeadlessUI")]
public sealed class LinkDialogsUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;
    private static readonly int[] PatternRepeats = [0, 1, 2, 3, 1, 5, 6, 1, 8];

    [Fact]
    public async Task TheRealManager_ListsItsGroupsAndSuggestions_AndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (edit, link) = Edit();
            link.Link((link.Lead, 1), (link.Lead, 4), (link.Rhythm, 1));
            var suggestions = LinkSuggestionFinder.Find(link.Project, [link.Lead, link.Rhythm], PatternRepeats, link.Rule);
            Assert.NotEmpty(suggestions);

            WithOpenDialog<ILinkGroupsScreen, bool>(() =>
            {
                var vm = new LinkGroupsViewModel(edit);
                vm.Configure(new LinkGroupsRequest(suggestions));
                return vm;
            }, view =>
            {
                AssertLaidOut("real", view, "LinkGroupList", "LinkSuggestionsSection", "LinkSelectedButton");
                var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Contains(buttons, b => Equals(b.Content, "Lead · M5") && b.Bounds.Width > 1);
                Assert.Contains(view.GetVisualDescendants().OfType<CheckBox>(), c => c.IsChecked == true && c.Bounds.Width > 1);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheRealSummary_ShowsItsLines_AndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (edit, link) = Edit();
            var suggestions = LinkSuggestionFinder.Find(link.Project, [link.Lead, link.Rhythm], PatternRepeats, link.Rule);

            WithOpenDialog<ILinkSuggestionsScreen, LinkSuggestionsChoice>(() =>
            {
                var vm = new LinkSuggestionsViewModel(edit);
                vm.Configure(new LinkSuggestionsRequest(suggestions));
                return vm;
            }, view =>
            {
                AssertLaidOut("real", view, "LinkSuggestBody", "LinkSuggestHeadline");
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.StartsWith("Lead and Rhythm share", StringComparison.Ordinal) == true && t.Bounds.Width > 1);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EveryFakeVariant_LaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var managers = FakeScreens.VariantsOf(typeof(ILinkGroupsScreen));
            var summaries = FakeScreens.VariantsOf(typeof(ILinkSuggestionsScreen));
            Assert.True(managers.Count >= 3 && summaries.Count >= 3);
            foreach (var variant in managers)
            {
                WithOpenDialog<ILinkGroupsScreen, bool>(() => (ILinkGroupsScreen)variant.Create(),
                    view => AssertLaidOut(variant.Name, view));
            }

            foreach (var variant in summaries)
            {
                WithOpenDialog<ILinkSuggestionsScreen, LinkSuggestionsChoice>(() => (ILinkSuggestionsScreen)variant.Create(),
                    view => AssertLaidOut(variant.Name, view, "LinkSuggestBody", "LinkSuggestHeadline"));
            }
        }, TestContext.Current.CancellationToken);
    }

    private static (ChartEditDialogServices Edit, LinkHarness Link) Edit()
    {
        var link = LinkHarness.WithRhythm();
        var edit = new ChartEditDialogServices(RecordingProxy.Create<IChartEditorSurface>().Proxy, link.History, link.State,
            new FakeConfigurationService { AppConfig = link.Config }, FakeLocalization.English(), new FakeAppLogger());
        return (edit, link);
    }

    private static void AssertLaidOut(string variant, Control view, params string[] parts)
    {
        var card = view.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("DialogCard"));
        Assert.True(card is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: the card laid out {card?.Bounds}");
        Assert.True(card!.Bounds.Width <= HostWidth && card.Bounds.Height <= HostHeight,
            $"{variant}: the card ({card.Bounds}) does not fit a {HostWidth}x{HostHeight} window");
        foreach (var name in parts)
        {
            var part = view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
            Assert.True(part is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: {name} laid out {part?.Bounds}");
        }
    }

    private static void WithOpenDialog<TContract, TResult>(Func<TContract> create, Action<Control> assert)
        where TContract : class, IDialogScreen<TResult>
    {
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped(_ => create());
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
            _ = ((IDialogs)host).ShowAsync<TContract, TResult>();
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
