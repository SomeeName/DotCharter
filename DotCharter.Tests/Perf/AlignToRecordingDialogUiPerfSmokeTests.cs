using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tests.Analysis;
using DotCharter.Tests.Services.Editing.Tempo;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Tempo map ▸ Fit the tab's tempo to the recording (#784, #499), built the way the app builds it: a real
///     <see cref="DialogHost" /> inside the real <see cref="DialogContainerView" />, in a shown window,
///     laid out. Every part must MEASURE, the card must fit 1280x720 with a 30-section song, and on the
///     REAL view model — analysed against a synthetic recording — every section row is laid out and
///     unticking one flows back to the view model through the binding. On a chart without notes the
///     problem line says what the tool needs; a section without notes has no tick box and prints its
///     reason; a result in which nothing matched is one line and a link that opens the rows.
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
    public async Task OnAChartWithoutNotes_TheRealDialogSaysWhatTheToolNeeds_AndListsNothing()
    {
        var (h, service, audio) = RecordingAlignmentProject.Build(new RecordingAlignmentFixture(), _dir);
        h.Project.Arrangements[0].Levels[0].Notes.Clear();
        var vm = new AlignToRecordingViewModel(h.State, service, new Playback(audio),
            new DialogBasics(FakeLocalization.English(), new FakeAppLogger()));
        await ((IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithOpenDialog(() => vm, view =>
            {
                AssertLaidOut("no notes", view);
                var problem = view.FindControl<TextBlock>("ProblemNote")!;
                Assert.True(problem.IsVisible);
                Assert.Equal(FakeLocalization.English()["Dialog_AlignToRecording_NeedsNotes"], problem.Text);
                Assert.True(problem.Bounds is { Width: > 1, Height: > 1 }, $"the problem line laid out {problem.Bounds}");
                Assert.False(view.FindControl<Border>("SectionList")!.IsVisible);
                Assert.False(view.FindControl<Control>("SectionsToggle")!.IsVisible);
                Assert.False(view.FindControl<Button>("ApplyButton")!.IsEffectivelyEnabled);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASectionWithoutNotes_HasNoTickBox_PrintsItsReason_AndItsTitleLinesUpWithTheOthers()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeAlignToRecordingScreen.Default();
            var silent = Assert.Single(screen.Sections, s => !s.CanApply);
            WithOpenDialog(() => screen, view =>
            {
                var list = view.FindControl<Border>("SectionList")!;
                var boxes = list.GetVisualDescendants().OfType<CheckBox>().Where(b => b.IsVisible).ToList();
                Assert.Equal(screen.Sections.Count(s => s.CanApply), boxes.Count);
                Assert.DoesNotContain(boxes, b => ReferenceEquals(b.DataContext, silent));

                var texts = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
                var reason = Assert.Single(texts, t => t.Text == silent.SkipReason);
                Assert.True(reason.Bounds is { Width: > 1, Height: > 1 }, $"the reason laid out {reason.Bounds}");

                var titles = texts.Where(t => screen.Sections.Any(s => s.Title == t.Text)).ToList();
                Assert.Equal(screen.Sections.Count, titles.Count);
                var lefts = titles.Select(t => Math.Round(t.TranslatePoint(default, list)!.Value.X)).Distinct().ToList();
                Assert.True(lefts.Count == 1, "the titles start at " + string.Join(", ", lefts));
                var heights = titles.Select(t => Math.Round(t.FindAncestorOfType<Grid>()!.Bounds.Height)).Distinct().ToList();
                Assert.True(heights.Count == 1, "the rows are " + string.Join(", ", heights) + " tall");
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AWeakMatch_IsOneLineAndALink_AndTheLinkOpensTheRows()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeAlignToRecordingScreen.WeakMatch();
            WithOpenDialog(() => screen, view =>
            {
                var list = view.FindControl<Border>("SectionList")!;
                var toggle = view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("SectionsToggle")!;
                var problem = view.FindControl<TextBlock>("ProblemNote")!;
                Assert.True(problem.IsVisible && problem.Bounds is { Width: > 1, Height: > 1 }, $"the result line laid out {problem.Bounds}");
                Assert.True(toggle.IsVisible && toggle.Bounds is { Width: > 1, Height: > 1 }, $"the link laid out {toggle.Bounds}");
                Assert.False(list.IsVisible);
                Assert.False(view.FindControl<TextBlock>("SummaryNote")!.IsVisible);

                toggle.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                TopLevel.GetTopLevel(view)!.UpdateLayout();

                Assert.True(screen.ShowSections);
                Assert.True(list.IsVisible && list.Bounds is { Width: > 1, Height: > 1 }, $"the opened list laid out {list.Bounds}");
                Assert.Contains(list.GetVisualDescendants().OfType<CheckBox>(), b => b.Bounds is { Width: > 1, Height: > 1 });
                AssertLaidOut("weak match, rows open", view);
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
