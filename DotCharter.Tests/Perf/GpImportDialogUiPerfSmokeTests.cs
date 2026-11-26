using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Import.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     File ▸ Import ▸ Guitar Pro, built the way the app builds it: a real <see cref="DialogHost" />
///     inside the real <see cref="DialogContainerView" />, in a shown window, laid out. On the REAL
///     view model the "replace the tempo map" box (#1025) must MEASURE and start unticked over a
///     charted project, ticked over an empty one, and hide outside add-new.
/// </summary>
[Collection("HeadlessUI")]
public sealed class GpImportDialogUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRealDialog_LaysOut_WithTheTempoMapChoice(bool charted)
    {
        await HeadlessSessionFixture.Dispatch(() =>
            WithOpenDialog(() => RealScreen(charted), (window, view) =>
            {
                Capture(window, charted ? "charted" : "empty");
                AssertLaidOut("real", view);
                foreach (var name in new[] { "TempoMapRow", "TempoMapNote", "ReplaceTempoMapCheck" })
                {
                    var part = view.FindControl<Control>(name);
                    Assert.True(part is { IsEffectivelyVisible: true, Bounds.Width: > 1, Bounds.Height: > 1 }, $"{name} laid out {part?.Bounds}");
                }

                var check = view.FindControl<CheckBox>("ReplaceTempoMapCheck")!;
                Assert.Equal(!charted, check.IsChecked);
                Assert.True(check.IsEffectivelyEnabled);

                var screen = (IGpImportScreen)view.DataContext!;
                screen.IsTechniqueOnlyMode = true;
                Dispatcher.UIThread.RunJobs();
                Assert.False(view.FindControl<Control>("TempoMapRow")!.IsVisible);
            }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ALockedMap_ShowsTheChoiceDisabled_WithWhy()
    {
        await HeadlessSessionFixture.Dispatch(() =>
            WithOpenDialog(TempoMapLockedVariant, (_, view) =>
            {
                AssertLaidOut("locked", view);
                Assert.False(view.FindControl<CheckBox>("ReplaceTempoMapCheck")!.IsEffectivelyEnabled);
                var why = view.FindControl<TextBlock>("TempoMapLockedNote");
                Assert.True(why is { IsEffectivelyVisible: true, Bounds.Height: > 1 }, $"locked note laid out {why?.Bounds}");
            }), TestContext.Current.CancellationToken);
    }

    private static IGpImportScreen TempoMapLockedVariant() =>
        (IGpImportScreen)FakeScreens.VariantsOf(typeof(IGpImportScreen)).Single(v => v.Name == "TempoMapLocked").Create();

    [Fact]
    public async Task EveryFakeVariant_LaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var variants = FakeScreens.VariantsOf(typeof(IGpImportScreen));
            Assert.True(variants.Count >= 3);
            foreach (var variant in variants)
                WithOpenDialog(() => (IGpImportScreen)variant.Create(), (_, view) => AssertLaidOut(variant.Name, view));
        }, TestContext.Current.CancellationToken);
    }

    private static GpImportViewModel RealScreen(bool charted)
    {
        var edit = new ChartEditHarness();
        var project = new ChartProject { Bpm = 120 };
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        if (charted) level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 2, Fret = 5 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        edit.State.ActiveProject = project;
        edit.State.ActiveArrangement = arrangement;
        var (maintenance, _) = UndoStepProxy.Create<IChartMaintenanceService>();
        var config = new FakeConfigurationService();
        var applier = new GpImportApplier(edit.State, edit.Editor, edit.Transport, config, maintenance, new FakeAppLogger(),
            FakeLocalization.English());
        var vm = new GpImportViewModel(applier, new FakeFileStorageService(), config, edit.State, new FakeAppLogger(),
            FakeLocalization.English());
        vm.Configure(new GpImportRequest("score.gp", [new GpTrackInfo { Id = 0, Name = "Lead" }, new GpTrackInfo { Id = 1, Name = "Rhythm" }]));
        return vm;
    }

    private static void AssertLaidOut(string variant, Control view)
    {
        var card = view.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("DialogCard"));
        Assert.True(card is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: the card laid out {card?.Bounds}");
        Assert.True(card!.Bounds.Width <= HostWidth && card.Bounds.Height <= HostHeight,
            $"{variant}: the card ({card.Bounds}) does not fit a {HostWidth}x{HostHeight} window");
    }

    /// <summary>Opt-in review capture: <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> plus a folder in
    ///     <c>DOTCHARTER_GPIMPORT_CAPTURE_DIR</c>.</summary>
    private static void Capture(Window window, string name)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        var directory = Environment.GetEnvironmentVariable("DOTCHARTER_GPIMPORT_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, $"gp-import-{name}-{HeadlessSessionFixture.SweptCulture}.png"), new PngBitmapEncoderOptions());
    }

    private static void WithOpenDialog(Func<IGpImportScreen> create, Action<Window, Control> assert)
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
            _ = ((IDialogs)host).ShowAsync<IGpImportScreen, bool>();
            Dispatcher.UIThread.RunJobs();
            container.Measure(new Size(HostWidth, HostHeight));
            container.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            assert(window, Assert.Single(host.OpenViews));
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
