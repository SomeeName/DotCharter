using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tests.Analysis;
using DotCharter.Tests.Services.Editing.Tempo;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Tempo map ▸ Align to recording (#784), built the way the app builds it: a real
///     <see cref="DialogHost" /> inside the real <see cref="DialogContainerView" />, in a shown window,
///     laid out. Every part must MEASURE, the card must fit 1280x720 with a 30-section song, and on the
///     REAL view model — analysed against a synthetic recording — every section row is laid out and
///     unticking one flows back to the view model through the binding.
/// </summary>
[Collection("HeadlessUI")]
public sealed class AlignToRecordingDialogUiPerfSmokeTests : IDisposable
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;

    private static readonly string[] MeasuredParts = ["SourceValue", "RangePanel", "FromBarInput", "AnchorsNote", "ApplyButton"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-784-ui-" + Guid.NewGuid().ToString("N"));

    public AlignToRecordingDialogUiPerfSmokeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TheRealDialog_AfterAnAnalysis_LaysOutEverySection_AndTheTickBoxesBindBack()
    {
        var (h, service, audio) = RecordingAlignmentProject.Build(new RecordingAlignmentFixture(mismatchedSection: 2), _dir);
        var vm = new AlignToRecordingViewModel(h.State, service, new Playback(audio),
            new DialogBasics(FakeLocalization.English(), new FakeAppLogger()));
        await ((IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.StatusText);

        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithOpenDialog(() => vm, view =>
            {
                AssertLaidOut("real", view);
                var list = view.FindControl<Border>("SectionList")!;
                Assert.True(list.Bounds is { Width: > 1, Height: > 1 }, $"the section list laid out {list.Bounds}");
                var boxes = list.GetVisualDescendants().OfType<CheckBox>().ToList();
                Assert.Equal(vm.Sections.Count, boxes.Count);
                Assert.All(boxes, b => Assert.True(b.Bounds is { Width: > 1, Height: > 1 }));
                Assert.False(boxes[2].IsChecked, "the mismatched section is offered unticked");

                boxes[0].IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.Sections[0].IsApplied);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EveryFakeVariant_LaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var variants = FakeScreens.VariantsOf(typeof(IAlignToRecordingScreen));
            Assert.True(variants.Count >= 5);
            foreach (var variant in variants)
                WithOpenDialog(() => (IAlignToRecordingScreen)variant.Create(), view => AssertLaidOut(variant.Name, view));
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

    private static void WithOpenDialog(Func<IAlignToRecordingScreen> create, Action<Control> assert)
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
            _ = ((IDialogs)host).ShowAsync<IAlignToRecordingScreen, bool>();
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

    private sealed class Playback(string path) : FakeAudioPlaybackService, IPlaybackTransport
    {
        public string CurrentAudioPath => path;
    }
}
