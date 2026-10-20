using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Package.DcSong;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     File ▸ Export ▸ DotCharter song (#707), built the way the app builds it: a real
///     <see cref="DialogHost" /> inside the real <see cref="DialogContainerView" /> (where the
///     dialog grammar is included), in a shown window, laid out, on the REAL view model over a
///     scripted exporter and in the swept culture's strings. Every row must MEASURE (a view whose
///     styles failed still arranges, with no size), the card and every footer button must sit
///     inside a laptop window, the stems toggle must be live exactly when stems exist, and a
///     missing ffmpeg must grey Export with its reason on screen. Width-shaped, so it is in the
///     release's <c>$HeadlessLayoutFilter</c> (de-DE and ja-JP).
/// </summary>
[Collection("HeadlessUI")]
public sealed class DcSongExportDialogUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;

    private static readonly string[] MeasuredParts =
        ["QualityPicker", "StemsToggle", "ArrangementSection", "CoverField", "OutputField", "ExportButton"];

    [Fact]
    public async Task WithStems_TheToggleIsLive_ExportIsLive_AndEveryRowLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var rig = new Rig();
            rig.Exporter.Probe = rig.Exporter.Probe with
            {
                Stems = [rig.Stem("bass"), rig.Stem("drums"), rig.Stem("other"), rig.Stem("vocals")]
            };

            WithOpenDialog(rig.Open, view =>
            {
                AssertLaidOut("stems", view);
                Assert.True(view.FindControl<CheckBox>("StemsToggle")!.IsEffectivelyEnabled);
                Assert.False(view.FindControl<Border>("ExportBlockedStrip")!.IsVisible);
                Assert.True(view.FindControl<Button>("ExportButton")!.IsEffectivelyEnabled);
                Assert.Equal(5, view.FindControl<ItemsControl>("ArrangementList")!.GetVisualDescendants().OfType<CheckBox>().Count());
                Capture(view, "dcsong-export-stems.png");
                var body = view.FindControl<ScrollViewer>("DcSongBody")!;
                body.ScrollToEnd();
                Dispatcher.UIThread.RunJobs();
                Layout(view);
                Capture(view, "dcsong-export-stems-end.png");
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WithoutFfmpegOrStems_ExportIsGreyWithItsReason_AndTheToggleIsDead()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var rig = new Rig();
            rig.Exporter.Probe = rig.Exporter.Probe with { EncoderAvailable = false };

            WithOpenDialog(rig.Open, view =>
            {
                AssertLaidOut("no ffmpeg", view);
                var strip = view.FindControl<Border>("ExportBlockedStrip")!;
                Assert.True(strip is { IsVisible: true, Bounds.Height: > 1 }, $"the reason strip laid out {strip.Bounds}");
                Assert.Equal(LocalizationService.Instance["Dialog_DcSong_FfmpegMissing"],
                    view.FindControl<TextBlock>("ExportBlockedText")!.Text);
                Assert.False(view.FindControl<Button>("ExportButton")!.IsEffectivelyEnabled);
                Assert.False(view.FindControl<CheckBox>("StemsToggle")!.IsEffectivelyEnabled);
                Capture(view, "dcsong-export-no-ffmpeg.png");
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WhileWriting_TheProgressShows_AndTheBodyIsLocked()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var rig = new Rig();
            var gate = new TaskCompletionSource<DcSongWriteResult>();
            rig.Exporter.Write = (_, progress, _) =>
            {
                progress?.Report(new DcSongWriteProgress(2, 5, DcSongWriteStage.EncodingAudio));
                return gate.Task;
            };

            WithOpenDialog(rig.Open, view =>
            {
                var vm = (DcSongExportViewModel)view.DataContext!;
                _ = vm.ExportCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Layout(view);

                var progress = view.FindControl<StackPanel>("ProgressBlock")!;
                Assert.True(progress is { IsVisible: true, Bounds.Height: > 1 }, $"the progress block laid out {progress.Bounds}");
                Assert.False(view.FindControl<ScrollViewer>("DcSongBody")!.Content is Control { IsEffectivelyEnabled: true });
                Capture(view, "dcsong-export-writing.png");
                gate.SetResult(FakeDcSongExporter.Written(rig.Exporter.WriteRequests[0], 5_100_000));
                Dispatcher.UIThread.RunJobs();
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EveryFakeVariant_LaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var variants = FakeScreens.VariantsOf(typeof(IDcSongExportScreen));
            Assert.True(variants.Count >= 6);
            foreach (var variant in variants)
                WithOpenDialog(() => (IDcSongExportScreen)variant.Create(), view => AssertLaidOut(variant.Name, view));
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

        // The estimate holds the footer's meta slot until a package is written.
        var size = view.FindControl<TextBlock>("SizeValue")!;
        var screen = (IDcSongExportScreen)view.DataContext!;
        Assert.Equal(!screen.HasResult, size.IsVisible);
        if (size.IsVisible)
        {
            Assert.True(size.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: the size meta laid out {size.Bounds}");
            AssertInside(size, card, $"{variant}: the size meta");
        }

        // The footer's buttons and the path fields stay inside the card in every language.
        var footer = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogFooter"));
        foreach (var button in footer.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
            AssertInside(button, card, $"{variant}: footer button '{button.Content}'");
        AssertInside(view.FindControl<Control>("OutputField")!, card, $"{variant}: the output field");
    }

    private static void AssertInside(Control child, Control ancestor, string what)
    {
        var topLeft = child.TranslatePoint(default, ancestor);
        var bottomRight = child.TranslatePoint(new Point(child.Bounds.Width, child.Bounds.Height), ancestor);
        Assert.True(topLeft.HasValue && bottomRight.HasValue, $"{what} is not under the card");
        Assert.True(topLeft!.Value.X >= -1 && bottomRight!.Value.X <= ancestor.Bounds.Width + 1,
            $"{what} spans x={topLeft.Value.X:F0}..{bottomRight!.Value.X:F0} in a {ancestor.Bounds.Width:F0} px card");
    }

    private static void Layout(Control view)
    {
        var root = TopLevel.GetTopLevel(view)!;
        root.Measure(new Size(HostWidth, HostHeight));
        root.Arrange(new Rect(0, 0, HostWidth, HostHeight));
    }

    private static void WithOpenDialog(Func<IDcSongExportScreen> create, Action<Control> assert)
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
            _ = ((IDialogs)host).ShowAsync<IDcSongExportScreen, bool>();
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

    private static void Capture(Control view, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        var culture = Environment.GetEnvironmentVariable("DOTCHARTER_HEADLESS_CULTURE");
        var name = string.IsNullOrWhiteSpace(culture) ? file : Path.GetFileNameWithoutExtension(file) + "." + culture + ".png";
        using var frame = ((Window)TopLevel.GetTopLevel(view)!).CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>A realistic chart (a long title, five arrangements, a cover) over a scripted exporter, in the swept culture.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-dcsong-ui-").FullName;

        public Rig()
        {
            var project = Directory.CreateDirectory(Path.Combine(_dir, "Die Ärzte - Westerland")).FullName;
            var library = Directory.CreateDirectory(Path.Combine(_dir, "Rocksmith Library")).FullName;
            Config.AppConfig.PsarcLibraryPath = library;
            var chart = new ChartProject();
            chart.Chart.Artist = "Die Ärzte";
            chart.Chart.SongName = "Westerland (Live in der Wuhlheide, ungekürzte Fassung)";
            chart.Chart.CoverArtPath = "cover.png";
            (string Name, ArrangementType Type)[] arrangements =
            [
                ("Lead", ArrangementType.Lead), ("Leadgitarre (Solo)", ArrangementType.Lead), ("Rhythm", ArrangementType.Rhythm),
                ("Rhythmus 2", ArrangementType.Rhythm), ("Bass", ArrangementType.Bass)
            ];
            foreach (var (name, type) in arrangements) chart.Arrangements.Add(new Arrangement { Name = name, ArrangementType = type });
            State.ActiveProject = chart;
            State.CurrentProjectPath = Path.Combine(project, "Westerland.rscp");
            State.CurrentAudioFileName = "Westerland.wav";
            State.TotalAudioLengthMs = 254_000;
        }

        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeDcSongExporter Exporter { get; } = new();

        public DcSongStemCandidate Stem(string name) => new(name, Path.Combine(_dir, name + ".wav"), 48_000_000);

        public IDcSongExportScreen Open()
        {
            var logger = new FakeAppLogger();
            var edit = new ChartEditDialogServices(RecordingProxy.Create<IChartEditorSurface>().Proxy, new HistoryService(logger), State,
                Config, LocalizationService.Instance, logger);
            var io = new DcSongExportServices(RecordingProxy.Create<IEditorTransport>().Proxy,
                RecordingProxy.Create<IProjectDocumentService>().Proxy, new FakeFileStorageService(), new ExportTargetWriter(),
                RecordingProxy.Create<IShellLauncher>().Proxy, Exporter);
            return new DcSongExportViewModel(edit, io);
        }

        public void Dispose() => BestEffortDelete.Directory(_dir);
    }
}
