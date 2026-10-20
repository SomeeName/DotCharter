using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.DraftMerge;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Import.Audio;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     File ▸ Chart from audio's stem strip (#641), built the way the app builds the
///     dialog: a real <see cref="DialogHost" /> inside the real <see cref="DialogContainerView" />,
///     in a shown window, laid out. Each state's parts must MEASURE (a view whose styles
///     failed still arranges, with no size), the visible button must sit inside the card
///     (German labels are the long ones), and on the REAL view model a song with no stem
///     must show the strip with a live Separate stems button. The draft's target picker
///     (#647: a new arrangement, or a merge into the current one with Replace existing
///     notes under it) lays out inside the card in every state.
///     <para>
///         <c>DOTCHARTER_CHARTFROMAUDIO_CAPTURE_DIR</c> + <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>
///         writes the real dialog as a PNG at 3x, per culture, for a look by eye.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChartFromAudioStemStripUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;
    private const string CaptureDirEnvVar = "DOTCHARTER_CHARTFROMAUDIO_CAPTURE_DIR";

    [Fact]
    public async Task EveryFakeVariant_LaysOut_AndTheStripShowsOnlyWhereItSpeaks()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var variants = FakeScreens.VariantsOf(typeof(IChartFromAudioScreen));
            Assert.Contains(variants, v => ((IChartFromAudioScreen)v.Create()).HasStemHint);
            foreach (var variant in variants)
            {
                IChartFromAudioScreen? screen = null;
                WithOpenDialog(() => screen = (IChartFromAudioScreen)variant.Create(), (view, window) =>
                {
                    AssertStrip(variant.Name, view, screen!);
                    if (screen!.HasStemHint) Capture(window, variant.Name);
                });
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EveryFakeVariant_ShowsTheTargetPicker_AndReplaceFollowsTheMergeTarget()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var variants = FakeScreens.VariantsOf(typeof(IChartFromAudioScreen));
            Assert.Contains(variants, v => ((IChartFromAudioScreen)v.Create()).MergeIntoCurrent);
            foreach (var variant in variants)
            {
                IChartFromAudioScreen? screen = null;
                WithOpenDialog(() => screen = (IChartFromAudioScreen)variant.Create(), (view, window) =>
                {
                    // #647: the target pair, the merge hint and Replace existing notes all lay out
                    // inside the card, whatever the dialog's state.
                    var card = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogCard"));
                    foreach (var name in new[] { "TargetPanel", "TargetNewArrangement", "TargetMerge", "TargetMergeText", "TargetMergeHint", "ReplaceExistingBox" })
                    {
                        AssertMeasured(variant.Name, view, name);
                        var part = view.FindControl<Control>(name)!;
                        var right = part.TranslatePoint(new Point(part.Bounds.Width, 0), card)!.Value.X;
                        Assert.True(right <= card.Bounds.Width + 0.5,
                            $"{variant.Name}: {name} ends at {right:0.#}, past the card's {card.Bounds.Width:0.#}");
                    }

                    Assert.Equal(screen!.MergeIntoCurrent, view.FindControl<RadioButton>("TargetMerge")!.IsChecked);
                    Assert.Equal(screen.AddAsNewArrangement, view.FindControl<RadioButton>("TargetNewArrangement")!.IsChecked);
                    Assert.Equal(screen.MergeIntoCurrent && !screen.IsBusy,
                        view.FindControl<CheckBox>("ReplaceExistingBox")!.IsEffectivelyEnabled);
                    Assert.Equal(screen.ReplaceExisting, view.FindControl<CheckBox>("ReplaceExistingBox")!.IsChecked);
                    Assert.Equal(screen.ApplyText, view.FindControl<Button>("ChartFromAudioApply")!.Content);
                    if (screen.MergeIntoCurrent) Capture(window, variant.Name);
                });
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheRealViewModel_OffersSeparateStems_WhenTheSongHasNoStem()
    {
        // A capture run puts the song under the capture folder, so the picture shows no profile path.
        var captureDir = Environment.GetEnvironmentVariable(CaptureDirEnvVar);
        var dir = UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(captureDir)
            ? Path.Combine(captureDir, "Song")
            : Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
        try
        {
            await HeadlessSessionFixture.Dispatch(async () =>
            {
                var mix = WriteSilentWav(dir, "mix.wav");
                var separation = new PendingSeparation(mix);
                ChartFromAudioViewModel? vm = null;
                await WithOpenDialogAsync(() => vm = RealViewModel(mix, separation), async (view, window) =>
                {
                    await vm!.StemCheck;
                    Settle(window);

                    Assert.True(vm.HasStemHint, "no stem was located, so the full-mix hint must show");
                    AssertStrip("real", view, vm);
                    Assert.True(view.FindControl<Button>("SeparateStemsButton")!.IsEffectivelyEnabled);
                    Capture(window, "real");

                    var run = ((IAsyncRelayCommand)vm.SeparateStemsCommand).ExecuteAsync(null);
                    separation.Report(0.4);
                    Settle(window);
                    Assert.True(vm.IsSeparating);
                    AssertStrip("real, separating", view, vm);
                    Assert.Equal(0.4, view.FindControl<ProgressBar>("SeparationProgressBar")!.Value, 3);
                    Capture(window, "real-separating");

                    vm.CancelSeparationCommand.Execute(null);
                    await run;
                    Settle(window);
                    Assert.False(vm.IsSeparating);
                    AssertStrip("real, cancelled", view, vm);
                    Assert.True(view.FindControl<Button>("SeparateStemsButton")!.IsEffectivelyEnabled,
                        "a cancelled run can be started again");
                    Assert.Equal(LocalizationService.Instance["ChartFromAudio_SeparationCancelled"],
                        view.FindControl<TextBlock>("StemHintText")!.Text);
                    Capture(window, "real-cancelled");
                });
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    /// <summary>
    ///     #710: closing the dialog mid-separation leaves the run going; reopened, the REAL
    ///     view model on the real host follows that run — progress, Cancel separation and the
    ///     note that closing does not stop it — and starts no second one.
    /// </summary>
    [Fact]
    public async Task TheRealViewModel_ReopenedMidSeparation_FollowsTheRunAndSaysClosingLeavesItRunning()
    {
        var captureDir = Environment.GetEnvironmentVariable(CaptureDirEnvVar);
        var dir = UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(captureDir)
            ? Path.Combine(captureDir, "Song")
            : Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
        try
        {
            await HeadlessSessionFixture.Dispatch(async () =>
            {
                var mix = WriteSilentWav(dir, "mix.wav");
                var separation = new PendingSeparation(mix);
                ChartFromAudioViewModel? first = null;
                await WithOpenDialogAsync(() => first = RealViewModel(mix, separation), async (view, window) =>
                {
                    await first!.StemCheck;
                    _ = ((IAsyncRelayCommand)first.SeparateStemsCommand).ExecuteAsync(null);
                    separation.Report(0.4);
                    Settle(window);
                    Settle(window);
                    AssertStrip("real, separating", view, first);
                    AssertNoteInView("real, separating", view);
                    Assert.Equal(LocalizationService.Instance["ChartFromAudio_SeparationKeepsRunning"],
                        view.FindControl<TextBlock>("SeparationKeepsRunningNote")!.Text);
                });

                var run = Assert.IsType<SongStemSeparationRun>(separation.Running);
                Assert.False(run.Token.IsCancellationRequested, "closing the dialog cancelled the separation");

                ChartFromAudioViewModel? reopened = null;
                await WithOpenDialogAsync(() => reopened = RealViewModel(mix, separation), async (view, window) =>
                {
                    Settle(window);
                    Assert.True(reopened!.IsSeparating, "the reopened dialog did not follow the running separation");
                    AssertStrip("real, reopened", view, reopened);
                    Assert.Equal(0.4, view.FindControl<ProgressBar>("SeparationProgressBar")!.Value, 3);
                    AssertNoteInView("real, reopened", view);
                    Capture(window, "real-reopened");

                    separation.Report(0.7);
                    Settle(window);
                    Assert.Equal(0.7, view.FindControl<ProgressBar>("SeparationProgressBar")!.Value, 3);
                    Assert.Equal(1, separation.Runs);

                    reopened.CancelSeparationCommand.Execute(null);
                    await reopened.StemCheck;
                    Settle(window);
                    Assert.False(reopened.IsSeparating);
                    AssertStrip("real, reopened then cancelled", view, reopened);
                });
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static ChartFromAudioViewModel RealViewModel(string mix, ISongStemSeparation separation)
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        var state = new ProjectState { ActiveProject = project, ActiveArrangement = arrangement, CurrentAudioFileName = mix };
        var edit = new ChartEditDialogServices(RecordingProxy.Create<IChartEditorSurface>().Proxy, history, state,
            new FakeConfigurationService(), LocalizationService.Instance, logger);
        var stems = new NoStemLocator();
        return new ChartFromAudioViewModel(edit, new FakeFileStorageService(), new ExportTargetWriter(),
            new ChartFromAudioAnalyzer(stems), new ChartFromAudioStemServices(stems, separation),
            new ChartFromAudioMerge(edit, new DraftMergeReviewHost(history, new FretAnchorGenerationService())),
            new DialogMemory());
    }

    private static void AssertStrip(string variant, Control view, IChartFromAudioScreen screen)
    {
        var card = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogCard"));
        Assert.True(card is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: the card laid out {card.Bounds}");
        Assert.True(card.Bounds.Width <= HostWidth && card.Bounds.Height <= HostHeight,
            $"{variant}: the card ({card.Bounds}) does not fit a {HostWidth}x{HostHeight} window");
        AssertNothingSpillsUnderTheFooter(variant, view, card);

        var panel = view.FindControl<StackPanel>("StemPanel")!;
        Assert.Equal(screen.HasStemHint, panel.IsEffectivelyVisible);
        if (!screen.HasStemHint) return;

        AssertMeasured(variant, view, "StemHintText");
        var button = screen.IsSeparating ? "CancelSeparationButton" : "SeparateStemsButton";
        var hidden = screen.IsSeparating ? "SeparateStemsButton" : "CancelSeparationButton";
        AssertMeasured(variant, view, button);
        Assert.False(view.FindControl<Button>(hidden)!.IsEffectivelyVisible, $"{variant}: {hidden} shows beside {button}");
        var note = view.FindControl<TextBlock>("SeparationKeepsRunningNote")!;
        if (screen.IsSeparating)
        {
            AssertMeasured(variant, view, "SeparationProgressBar");
            AssertMeasured(variant, view, "SeparationKeepsRunningNote");
            Assert.False(string.IsNullOrWhiteSpace(note.Text), $"{variant}: the keeps-running note is empty");
            Assert.Equal(screen.SeparationKeepsRunningNote, note.Text);
        }
        else
        {
            Assert.Equal(screen.CanSeparateStems, view.FindControl<Button>(button)!.IsEffectivelyEnabled);
            Assert.False(note.IsEffectivelyVisible, $"{variant}: the keeps-running note shows with no separation running");
        }
        if (screen.SeparateStemsBlockedReason.Length > 0) AssertMeasured(variant, view, "SeparateStemsReason");

        var shown = view.FindControl<Button>(button)!;
        var right = shown.TranslatePoint(new Point(shown.Bounds.Width, 0), card)!.Value.X;
        Assert.True(right <= card.Bounds.Width + 0.5,
            $"{variant}: {button} ends at {right:0.#}, past the card's {card.Bounds.Width:0.#}");
    }

    /// <summary>
    ///     #710: the strip grows while a separation runs (progress, the keeps-running note), and
    ///     an unscrolled stack arranged shorter than it asked for keeps its own bounds but paints
    ///     its children on under the footer — no bounds check sees that. Everything in the body
    ///     that no ScrollViewer clips must end above the footer.
    /// </summary>
    private static void AssertNothingSpillsUnderTheFooter(string variant, Control view, Border card)
    {
        var footer = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogFooter"));
        var footerTop = footer.TranslatePoint(default, card)!.Value.Y;
        var body = Assert.IsAssignableFrom<Panel>(footer.Parent).Children.Where(c => c != footer && c.IsVisible).ToList();
        foreach (var part in body.Where(p => p.TranslatePoint(default, card)!.Value.Y < footerTop))
        {
            var spilled = Unscrolled(part).FirstOrDefault(c =>
                c.Bounds.Height > 0 && c.TranslatePoint(new Point(0, c.Bounds.Height), card)!.Value.Y > footerTop + 0.5);
            Assert.True(spilled is null,
                $"{variant}: {spilled?.GetType().Name} {spilled?.Name} in the body paints under the footer at {footerTop:0.#}");
        }
    }

    /// <summary>
    ///     The keeps-running note is the point of the strip: in a 1280x720 window the body
    ///     scrolls, and the view brings the note into the body's viewport when it appears.
    /// </summary>
    private static void AssertNoteInView(string variant, Control view)
    {
        var body = view.FindControl<ScrollViewer>("ChartFromAudioBody")!;
        var note = view.FindControl<TextBlock>("SeparationKeepsRunningNote")!;
        var top = note.TranslatePoint(default, body)!.Value.Y;
        var bottom = top + note.Bounds.Height;
        Assert.True(top >= -0.5 && bottom <= body.Viewport.Height + 0.5,
            $"{variant}: the note spans y {top:0.#}..{bottom:0.#} of a {body.Viewport.Height:0.#} px viewport (offset {body.Offset.Y:0.#})");
    }

    /// <summary>The visible visuals under <paramref name="part" /> that no ScrollViewer clips (a Viewbox scales its own).</summary>
    private static IEnumerable<Control> Unscrolled(Control part)
    {
        if (part is ScrollViewer or Viewbox) yield break;
        foreach (var child in part.GetVisualChildren().OfType<Control>().Where(c => c.IsVisible))
        {
            yield return child;
            foreach (var inner in Unscrolled(child)) yield return inner;
        }
    }

    private static void AssertMeasured(string variant, Control view, string name)
    {
        var part = view.FindControl<Control>(name);
        Assert.True(part is { IsEffectivelyVisible: true, Bounds.Width: > 1, Bounds.Height: > 1 },
            $"{variant}: {name} laid out {part?.Bounds} (visible: {part?.IsEffectivelyVisible})");
    }

    private static void Capture(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable(CaptureDirEnvVar);
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"chart-from-audio.{name}.{HeadlessSessionFixture.SweptCulture}.png"),
            new PngBitmapEncoderOptions());
    }

    private static string WriteSilentWav(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var writer = new BinaryWriter(File.Create(path));
        const int rate = 44100;
        const int samples = rate / 10;
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
        return path;
    }

    private static void WithOpenDialog(Func<IChartFromAudioScreen> create, Action<Control, Window> assert) =>
        WithOpenDialogAsync(create, (view, window) =>
        {
            assert(view, window);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

    private static async Task WithOpenDialogAsync(Func<IChartFromAudioScreen> create, Func<Control, Window, Task> assert)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped(_ => create());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
            provider.GetRequiredService<DialogsSlot>());
        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(host);
        var window = new Window { Width = HostWidth, Height = HostHeight, Content = container };
        window.Show();
        try
        {
            _ = ((IDialogs)host).ShowAsync<IChartFromAudioScreen, bool>();
            Dispatcher.UIThread.RunJobs();
            container.Measure(new Size(HostWidth, HostHeight));
            container.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            await assert(Assert.Single(host.OpenViews), window);
            host.CloseTop();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }

    private sealed class NoStemLocator : IChartFromAudioStemLocator
    {
        public ChartFromAudioStem? TryLocate(string mixWavPath, ArrangementType? arrangementType) => null;
    }

    /// <summary>
    ///     Never separates: a run waits for its cancel, and the test reports its progress.
    ///     Like the app's, it publishes the run in flight, so a reopened dialog can follow it.
    /// </summary>
    private sealed class PendingSeparation(string songAudio) : ISongStemSeparation
    {
        public IProgress<double>? Progress { get; private set; }

        public SongStemSeparationRun? Running { get; private set; }

        public int Runs { get; private set; }

        public Task<SongStemSeparationAvailability> GetAvailabilityAsync(string audioPath, CancellationToken ct = default) =>
            Task.FromResult(SongStemSeparationAvailability.Ready);

        public Task<SongStemSeparationOutcome> SeparateAsync(IProgress<double>? progress, CancellationToken ct)
        {
            Runs++;
            Progress = progress;
            var task = new TaskCompletionSource<SongStemSeparationOutcome>();
            var run = new SongStemSeparationRun(songAudio, ct);
            Running = run;
            run.Token.Register(() =>
            {
                var cancelled = new SongStemSeparationOutcome(SongStemSeparationStatus.Cancelled);
                Running = null;
                run.Complete(cancelled);
                task.TrySetResult(cancelled);
            });
            return task.Task;
        }

        public void Report(double value)
        {
            Progress?.Report(value);
            Running?.Report(value);
        }
    }
}
