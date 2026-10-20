using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tests.Services.Editing.Maintenance;
using DotCharter.Tests.Services.Tempo;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Tempo map ▸ Align beat grid to notes (#403), built the way the app builds it:
///     a real <see cref="DialogHost" /> inside the real <see cref="DialogContainerView" />
///     (where the dialog grammar is included), in a shown window, laid out. Every
///     row must MEASURE — a view whose styles failed still arranges, with no size —
///     and on the REAL view model the box must carry the measured offset and the
///     "after" readout must follow it through the binding.
/// </summary>
[Collection("HeadlessUI")]
public sealed class AlignBeatGridDialogUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;

    private static readonly string[] MeasuredParts = ["OffsetInput", "OffNowValue", "OffAfterValue"];

    [Fact]
    public async Task TheRealDialog_LaysOut_AndTheAfterReadoutFollowsTheBox()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var h = new ChartPowerToolsTests.Harness();
            var chart = BeatGridFixtures.Chart(gridErrorMs: -121);
            var project = h.State.ActiveProject!;
            project.Beats.Clear();
            foreach (var beat in chart.Beats) project.Beats.Add(beat);
            h.State.ActiveLevel!.Notes.Clear();
            foreach (var note in chart.Arrangements[0].Levels[0].Notes) h.State.ActiveLevel!.Notes.Add(note);

            WithOpenDialog(() => new AlignBeatGridViewModel(h.State, h.Maintenance, FakeLocalization.English(), new FakeAppLogger()),
                view =>
                {
                    AssertLaidOut("real", view);
                    var input = view.FindControl<NumericUpDown>("OffsetInput")!;
                    var now = view.FindControl<TextBlock>("OffNowValue")!;
                    var after = view.FindControl<TextBlock>("OffAfterValue")!;
                    Assert.Equal(121, (double)input.Value!.Value, 0.5);
                    Assert.False(string.IsNullOrWhiteSpace(view.FindControl<TextBlock>("EstimateNote")!.Text));
                    Assert.NotEqual(now.Text, after.Text);

                    input.Value = 0;
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(now.Text, after.Text);
                });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EveryFakeVariant_LaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var variants = FakeScreens.VariantsOf(typeof(IAlignBeatGridScreen));
            Assert.True(variants.Count >= 3);
            foreach (var variant in variants)
                WithOpenDialog(() => (IAlignBeatGridScreen)variant.Create(), view => AssertLaidOut(variant.Name, view));
        }, TestContext.Current.CancellationToken);
    }

    private static void AssertLaidOut(string variant, Control view)
    {
        var card = view.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("DialogCard"));
        Assert.True(card is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: the card laid out {card?.Bounds}");
        Assert.True(card!.Bounds.Width <= HostWidth && card.Bounds.Height <= HostHeight,
            $"{variant}: the card ({card.Bounds}) does not fit a {HostWidth}x{HostHeight} window");
        foreach (var name in MeasuredParts)
        {
            var part = view.FindControl<Control>(name);
            Assert.True(part is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: {name} laid out {part?.Bounds}");
        }
    }

    private static void WithOpenDialog(Func<IAlignBeatGridScreen> create, Action<Control> assert)
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
            _ = ((IDialogs)host).ShowAsync<IAlignBeatGridScreen, bool>();
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
