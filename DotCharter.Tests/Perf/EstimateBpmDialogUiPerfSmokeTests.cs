using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Tempo map ▸ Estimate BPM, built the way the app builds it: a real <see cref="DialogHost" />
///     inside the real <see cref="DialogContainerView" />, in a shown window, laid out. Every
///     fake variant must MEASURE its rows, and the one holding an estimate its result rows too.
/// </summary>
[Collection("HeadlessUI")]
public sealed class EstimateBpmDialogUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;

    private static readonly string[] MeasuredParts = ["SourceValue", "RangePanel", "EstimateButton", "ApplyButton"];

    [Fact]
    public async Task EveryFakeVariant_LaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var variants = FakeScreens.VariantsOf(typeof(IEstimateBpmScreen));
            Assert.True(variants.Count >= 3);
            foreach (var variant in variants)
            {
                var screen = (IEstimateBpmScreen)variant.Create();
                WithOpenDialog(() => screen, view =>
                {
                    AssertLaidOut(variant.Name, view, screen.IsBusy);
                    if (!screen.HasResult) return;
                    foreach (var name in new[] { "ResultPanel", "BpmValue", "TargetNote" })
                    {
                        var part = view.FindControl<Control>(name);
                        Assert.True(part is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant.Name}: {name} laid out {part?.Bounds}");
                    }
                });
            }
        }, TestContext.Current.CancellationToken);
    }

    private static void AssertLaidOut(string variant, Control view, bool busy)
    {
        var card = view.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("DialogCard"));
        Assert.True(card is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: the card laid out {card?.Bounds}");
        Assert.True(card!.Bounds.Width <= HostWidth && card.Bounds.Height <= HostHeight,
            $"{variant}: the card ({card.Bounds}) does not fit a {HostWidth}x{HostHeight} window");
        foreach (var name in MeasuredParts)
        {
            if (busy && name == "EstimateButton") continue;
            var part = view.FindControl<Control>(name);
            Assert.True(part is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: {name} laid out {part?.Bounds}");
        }
    }

    private static void WithOpenDialog(Func<IEstimateBpmScreen> create, Action<Control> assert)
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
            _ = ((IDialogs)host).ShowAsync<IEstimateBpmScreen, bool>();
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
