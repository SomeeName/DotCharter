using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.UI.Views.Prompts;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <see cref="ScreenHost" /> and <see cref="DialogHost" /> for real, in a
///     1600×900 window: the screen and dialog machinery of
///     target-architecture.md § 3.1–3.2, against the catalog's own views bound to
///     fakes through a real <see cref="ServiceCollection" /> — the same
///     <c>ScreenShellModule</c> the app registers.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ScreenHostUiPerfSmokeTests
{
    private sealed class ScopeProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static ServiceProvider BuildServices(List<ScopeProbe> probes)
    {
        var services = new ServiceCollection();
        ScreenShellModule.Register(services);
        services.AddScoped<ScopeProbe>(_ =>
        {
            var probe = new ScopeProbe();
            probes.Add(probe);
            return probe;
        });
        foreach (var entry in ScreenCatalog.Default.Entries.Where(e => e.Kind == ScreenKind.Screen))
        {
            var variant = FakeScreens.VariantsOf(entry.Contract).First();
            services.AddScoped(entry.Contract, sp =>
            {
                sp.GetRequiredService<ScopeProbe>();
                return variant.Create();
            });
        }

        // The dialogs module registers the PRODUCT dialog view models and the
        // helpers they take (a preview session, a run, a service bundle), which
        // reach into the app's services; here every dialog but the message
        // prompt (the one these cases open) is its fake, so its helpers go too
        // and the graph validates.
        foreach (var helper in services.Where(d => !d.ServiceType.IsInterface
                                                   && d.ServiceType != typeof(DotCharter.Presentation.Screens.Dialogs.DialogMemory)
                                                   && d.ServiceType.Namespace is { } ns
                                                   && (ns == "DotCharter.Presentation.Screens.Dialogs"
                                                       || ns.StartsWith("DotCharter.Services.", StringComparison.Ordinal)
                                                       || ns.StartsWith("DotCharter.Tone.", StringComparison.Ordinal)))
                     .ToList())
        {
            services.Remove(helper);
        }

        foreach (var entry in ScreenCatalog.Default.Entries.Where(e =>
                     e.Kind == ScreenKind.Dialog && e.Contract != typeof(IMessagePromptScreen)))
        {
            var variant = FakeScreens.VariantsOf(entry.Contract).First();
            services.RemoveAll(entry.Contract);
            services.AddScoped(entry.Contract, _ => variant.Create());
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static (Window Window, ScreenHost Screens, DialogHost Dialogs) Host(ServiceProvider services)
    {
        var screens = new ScreenHost(services.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default);
        var dialogs = new DialogHost(services.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
            services.GetRequiredService<DialogsSlot>());
        var window = new Window { Width = 1600, Height = 900, Content = new Grid { Children = { screens, dialogs } } };
        window.Show();
        window.UpdateLayout();
        return (window, screens, dialogs);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
    }

    [Fact]
    public async Task Show_creates_one_child_per_contract_flips_visibility_and_Back_reuses_it()
    {
        var probes = new List<ScopeProbe>();
        using var services = BuildServices(probes);
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, host, dialogs) = Host(services);
            try
            {
                host.Navigator.Show<IStartScreen>();
                Settle(window);
                var start = host.ViewOf(typeof(IStartScreen))!;
                var startModel = start.DataContext;
                Assert.True(start.IsVisible);
                Assert.IsAssignableFrom<IStartScreen>(startModel);
                Assert.True(start.Bounds is { Width: > 1000, Height: > 500 }, $"Start laid out as {start.Bounds}");

                host.Navigator.Show<ISongSelectScreen>();
                Settle(window);
                var menu = host.ViewOf(typeof(ISongSelectScreen))!;
                Assert.False(start.IsVisible);
                Assert.True(menu.IsVisible);
                Assert.Equal(2, host.Children.Count);
                Assert.Equal([start], host.HiddenSurfaces());

                Assert.True(host.Navigator.Back());
                Settle(window);
                Assert.Same(start, host.ViewOf(typeof(IStartScreen)));
                Assert.Same(startModel, start.DataContext);
                Assert.True(start.IsVisible);
                Assert.False(menu.IsVisible);
                Assert.Equal(2, host.Children.Count);
            }
            finally
            {
                window.Close();
                dialogs.Dispose();
                host.Dispose();
            }
        }, TestContext.Current.CancellationToken);

        Assert.NotEmpty(probes);
        Assert.All(probes, p => Assert.True(p.Disposed, "disposing the host must dispose its scope"));
    }

    [Fact]
    public async Task Showing_a_screen_prewarms_its_flow_neighbours_hidden_and_realized()
    {
        using var services = BuildServices([]);
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, host, dialogs) = Host(services);
            try
            {
                host.Flow = ScreenCatalog.Default.FlowOf(ScreenCatalog.PlayMode);
                host.Navigator.Show<ISongSelectScreen>();
                Settle(window);

                var before = host.ViewOf(typeof(IStartScreen));
                var after = host.ViewOf(typeof(ITuningCheckScreen));
                Assert.NotNull(before);
                Assert.NotNull(after);
                Assert.Null(host.ViewOf(typeof(ICountdownScreen)));

                foreach (var neighbour in new[] { before, after })
                {
                    Assert.False(neighbour.IsVisible, $"{neighbour.GetType().Name} was shown by its prewarm");
                    var realized = neighbour.GetVisualDescendants().OfType<Layoutable>()
                        .Count(v => v.DesiredSize is { Width: > 1, Height: > 1 });
                    Assert.True(realized > 10,
                        $"{neighbour.GetType().Name}: only {realized} descendants measured — the prewarm created it but never realized it");
                }

                Assert.Equal(2, host.HiddenSurfaces().Count);
                host.Navigator.Show<IStartScreen>();
                Settle(window);
                Assert.Same(before, host.ViewOf(typeof(IStartScreen)));
                Assert.True(before.IsVisible);
            }
            finally
            {
                window.Close();
                dialogs.Dispose();
                host.Dispose();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_message_opens_over_the_shared_scrim_and_completes_with_the_choice()
    {
        using var services = BuildServices([]);
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, host, dialogs) = Host(services);
            try
            {
                var app = services.GetRequiredService<IDialogs>();
                var answer = app.MessageAsync("Discard?", "The tones go too.", isConfirmation: true);
                Settle(window);

                Assert.True(app.HasOpenDialog);
                Assert.True(dialogs.Scrim.IsVisible);
                var view = Assert.Single(dialogs.OpenViews);
                var prompt = Assert.IsAssignableFrom<IMessagePromptScreen>(view.DataContext);
                Assert.Equal("Discard?", prompt.Title);
                Assert.True(prompt.IsConfirmation);
                var card = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "MessagePromptCard");
                Assert.True(card.Bounds is { Width: > 300, Height: > 80 }, $"the card laid out as {card.Bounds}");

                prompt.OkCommand.Execute(null);
                // The view model completes its result on a pool continuation that then posts
                // back here; pump until it lands. A deadlock detector, not a budget.
                PumpUntil(() => answer.IsCompleted);
                Settle(window);

                Assert.True(answer.IsCompletedSuccessfully);
                Assert.True(answer.Result);
                Assert.False(dialogs.HasOpenDialog);
                Assert.False(dialogs.Scrim.IsVisible);
                Assert.Null(view.DataContext);
                Assert.False(view.IsVisible);
            }
            finally
            {
                window.Close();
                dialogs.Dispose();
                host.Dispose();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_second_dialog_stacks_over_the_first_and_Esc_closes_the_top_before_going_back()
    {
        using var services = BuildServices([]);
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, host, dialogs) = Host(services);
            try
            {
                host.Navigator.Show<IStartScreen>();
                host.Navigator.Show<ISongSelectScreen>();
                var first = dialogs.MessageAsync("First", "");
                var second = dialogs.MessageAsync("Second", "", isConfirmation: true);
                Settle(window);

                Assert.Equal(2, dialogs.OpenViews.Count);
                var scrimAt = dialogs.Children.IndexOf(dialogs.Scrim);
                Assert.Equal(dialogs.Children.IndexOf(dialogs.OpenViews[1]) - 1, scrimAt);
                Assert.True(dialogs.Children.IndexOf(dialogs.OpenViews[0]) < scrimAt, "the scrim must dim the lower dialog");

                PressEscape(host);
                Settle(window);
                Assert.True(second.IsCompletedSuccessfully);
                Assert.False(second.Result);
                Assert.False(first.IsCompleted);
                Assert.Equal(typeof(ISongSelectScreen), host.Navigator.Current);
                Assert.Equal(dialogs.Children.IndexOf(dialogs.OpenViews[0]) - 1, dialogs.Children.IndexOf(dialogs.Scrim));

                PressEscape(host);
                Settle(window);
                Assert.True(first.IsCompletedSuccessfully);
                Assert.Equal(typeof(ISongSelectScreen), host.Navigator.Current);

                PressEscape(host);
                Settle(window);
                Assert.Equal(typeof(IStartScreen), host.Navigator.Current);
            }
            finally
            {
                window.Close();
                dialogs.Dispose();
                host.Dispose();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dialog_prewarm_builds_a_context_free_view_that_the_first_open_reuses()
    {
        using var services = BuildServices([]);
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, host, dialogs) = Host(services);
            try
            {
                var surfaces = dialogs.HiddenSurfaces();
                Assert.Equal(ScreenCatalog.Default.Entries.Count(e => e.Kind == ScreenKind.Dialog), surfaces.Count);
                Assert.All(surfaces, s => Assert.Null(s.DataContext));
                var warm = Assert.Single(surfaces.OfType<MessagePromptView>());
                Assert.True(HiddenSubtreePrewarmer.Realize(warm, new Size(1600, 900)));
                Assert.False(warm.IsVisible);
                Assert.Equal(surfaces, dialogs.HiddenSurfaces());

                _ = dialogs.MessageAsync("Hello", "");
                Settle(window);
                Assert.Same(warm, Assert.Single(dialogs.OpenViews));
                Assert.NotNull(warm.DataContext);
                dialogs.CloseTop();
                Settle(window);
            }
            finally
            {
                window.Close();
                dialogs.Dispose();
                host.Dispose();
            }
        }, TestContext.Current.CancellationToken);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.True(done(), "the dialog never completed — its continuation was not delivered in 60 s");
    }

    private static void PressEscape(Control target) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = target, Key = Key.Escape });
}
