using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Edit shape dialog bound to the REAL view model over a real chart (the catalog sweep draws
///     it on its fake), opened the way the app opens it: a real <see cref="DialogHost" /> inside the
///     real <see cref="DialogContainerView" />, which is where the dialog grammar
///     (<c>DialogStyles.axaml</c>) comes from. A bare window around the view has no such grammar, so
///     the card, the section cards, the banner and the footer pair all lose their styling while the
///     controls still measure. Hence the assertions on the resolved brushes and classes, not only on
///     size: the dialog card and every section card carry a theme background, the result banner is the
///     green strip, Apply is the primary button, and the song strip has one cell per bar.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapeEditDialogLiveUiPerfSmokeTests
{
    private const double HostWidth = 1600;
    private const double HostHeight = 900;

    [Fact]
    public async Task TheEditor_OverARealChart_HostedLikeTheApp_IsStyledAndFollowsTheScope()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var h = new ChartEditHarness();
            for (var i = 16; i < 160; i++)
            {
                h.Project.Beats.Add(new BeatMarker
                { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
            }

            var arrangement = h.Editor.ActiveArrangement!;
            var am = new ChordTemplate { Id = 0, Name = "Am", Frets = new() { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 } };
            arrangement.ChordTemplates.Add(am);
            var chords = new List<Chord>();
            foreach (var beat in new[] { 20, 28, 29, 44, 52, 60, 61, 62, 88, 96, 97, 112, 130, 150 })
            {
                var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = 0 };
                foreach (var (s, f) in am.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
                arrangement.Levels[0].Chords.Add(chord);
                chords.Add(chord);
            }

            ShapeEditViewModel? vm = null;
            var services = new ServiceCollection();
            services.AddSingleton<DialogsSlot>();
            services.AddScoped<IShapeEditScreen>(_ => vm = new ShapeEditViewModel(h.Services, new ChordUseMarks(h.Editor)));
            using var provider = services.BuildServiceProvider();
            var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
                provider.GetRequiredService<DialogsSlot>());
            var container = new DialogContainerView();
            container.DialogPanel.Children.Clear();
            container.DialogPanel.Children.Add(host);
            var window = new Window { Width = HostWidth, Height = HostHeight, Content = container, Background = Avalonia.Media.Brushes.Black };
            window.Show();
            try
            {
                var answer = host.ShowAsync(new ShapeEditRequest(am, chords[1]));
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(vm);
                vm.Strings[2].Fret = "0";
                Layout(container);
                var view = Assert.IsType<ShapeEditDialogView>(Assert.Single(host.OpenViews));
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"the dialog laid out as {view.Bounds}");
                Shoot(window, "every-use");
                AssertStyled(view, vm);

                vm.Scopes[0].SelectCommand.Execute(null);
                Layout(container);
                Assert.Contains(vm.ResultText, view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                Assert.Contains("this chord", vm.ResultText, StringComparison.Ordinal);
                AssertStyled(view, vm);
                Shoot(window, "this-chord");

                host.CloseTop();
                Dispatcher.UIThread.RunJobs();
                // The dialog's own answer is set at once; the host's task completes on a pool continuation.
                Assert.True(vm.Result.IsCompleted);
                Assert.NotNull(answer);
            }
            finally
            {
                window.Close();
                host.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheEditorOnTickedShapes_HostedLikeTheApp_OffersTheTwoScopes_AndApplyMakesOneShape()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var h = new ChartEditHarness();
            for (var i = 16; i < 160; i++)
            {
                h.Project.Beats.Add(new BeatMarker
                { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
            }

            var arrangement = h.Editor.ActiveArrangement!;
            var frets = new Dictionary<int, int> { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 };
            var am = new ChordTemplate { Id = 0, Name = "Am", Frets = new(frets), Fingers = new() { [2] = 2, [3] = 3, [4] = 1 } };
            var copy = new ChordTemplate { Id = 1, Name = "", Frets = new(frets), Fingers = new() { [2] = 3, [3] = 2, [4] = 1 } };
            arrangement.ChordTemplates.Add(am);
            arrangement.ChordTemplates.Add(copy);
            foreach (var (template, beats) in new[] { (am, new[] { 20, 28, 44, 52 }), (copy, new[] { 60, 88, 112 }) })
            {
                foreach (var beat in beats)
                {
                    var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = template.Id };
                    foreach (var (s, f) in frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
                    arrangement.Levels[0].Chords.Add(chord);
                }
            }

            ShapeEditViewModel? vm = null;
            var services = new ServiceCollection();
            services.AddSingleton<DialogsSlot>();
            services.AddScoped<IShapeEditScreen>(_ => vm = new ShapeEditViewModel(h.Services, new ChordUseMarks(h.Editor)));
            using var provider = services.BuildServiceProvider();
            var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
                provider.GetRequiredService<DialogsSlot>());
            var container = new DialogContainerView();
            container.DialogPanel.Children.Clear();
            container.DialogPanel.Children.Add(host);
            var window = new Window { Width = HostWidth, Height = HostHeight, Content = container, Background = Avalonia.Media.Brushes.Black };
            window.Show();
            try
            {
                var answer = host.ShowAsync(new ShapeEditRequest(am, null) { Together = [am, copy] });
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(vm);
                Layout(container);
                var view = Assert.IsType<ShapeEditDialogView>(Assert.Single(host.OpenViews));
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"the dialog laid out as {view.Bounds}");
                Assert.Equal(["This shape", "2 selected shapes"], vm.Scopes.Select(s => s.Label));
                Assert.Equal("Opened on 2 ticked shapes in the Shapes panel", vm.OpenedFromText);
                var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("2 selected shapes", texts);
                Assert.Contains("7 chords", texts);
                AssertStyled(view, vm);
                Shoot(window, "together");

                vm.ApplyCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.Result.IsCompleted, "Apply did not answer the dialog");
                Assert.True(vm.Result.Result);
                Assert.NotNull(answer);
                Assert.Single(arrangement.ChordTemplates);
                Assert.All(arrangement.Levels[0].Chords, c => Assert.Equal(0, c.TemplateId));
            }
            finally
            {
                window.Close();
                host.Dispose();
            }
        }, CancellationToken.None);
    }

    private static void Layout(Control root)
    {
        Dispatcher.UIThread.RunJobs();
        root.Measure(new Size(HostWidth, HostHeight));
        root.Arrange(new Rect(0, 0, HostWidth, HostHeight));
        root.UpdateLayout();
    }

    private static void AssertStyled(ShapeEditDialogView view, ShapeEditViewModel vm)
    {
        var all = view.GetVisualDescendants().ToList();
        var texts = all.OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(vm.ResultText, texts);
        Assert.Contains(vm.PreviewText, texts);
        Assert.Equal(vm.StringCount, vm.Strings.Count);

        var card = Assert.Single(all.OfType<Border>(), b => b.Classes.Contains("DialogCard"));
        Assert.True(IsPainted(card.Background), "the dialog card has no theme background: the dialog grammar was not applied");
        Assert.True(card.Bounds.Width > 400 && card.Bounds.Width < view.Bounds.Width, $"the card is {card.Bounds.Width} wide in a {view.Bounds.Width} host");

        var sections = all.OfType<Border>().Where(b => b.Classes.Contains("DlgSection")).ToList();
        Assert.True(sections.Count >= 2, $"{sections.Count} section cards");
        Assert.All(sections, s => Assert.True(IsPainted(s.Background) && s.Bounds.Height > 1, "a section card is unstyled"));

        var banner = Assert.Single(all.OfType<Border>(), b => b.Name == "ShapeEditResult");
        Assert.True(IsPainted(banner.Background) && IsPainted(banner.BorderBrush), "the result sentence is not the banner strip");

        var apply = Assert.Single(all.OfType<Button>(), b => Equals(b.Content, vm.ApplyLabel));
        Assert.True(apply.Classes.Contains("Primary"), "Apply is not the primary button");
        Assert.True(IsPainted(apply.Background), "Apply has no primary background");
        var cancel = Assert.Single(all.OfType<Button>(), b => b.IsCancel);
        Assert.True(apply.Bounds.X > cancel.Bounds.X || apply.TranslatePoint(default, card)!.Value.X > cancel.TranslatePoint(default, card)!.Value.X,
            "the footer pair is not Cancel then Apply");
        Assert.True(apply.TranslatePoint(new Point(apply.Bounds.Width, 0), card)!.Value.X > card.Bounds.Width * 0.7, "the footer pair is not right-aligned");

        var strip = Assert.Single(all.OfType<ItemsControl>(), i => i.Name == "ShapeEditStrip");
        var cells = strip.GetVisualDescendants().OfType<Panel>().Where(p => p.DataContext is IShapeUseBar).ToList();
        Assert.Equal(vm.UseBars.Count, cells.Count);
        Assert.True(cells.Count >= 30, $"{cells.Count} strip cells: the strip is not one cell per bar of the song");
        Assert.All(cells, c => Assert.True(c.Bounds.Width is > 1 and < 60, $"a strip cell is {c.Bounds.Width} wide"));
    }

    private static bool IsPainted(IBrush? brush) => brush is ISolidColorBrush { Color.A: > 0 } or IGradientBrush;

    private static void Shoot(Window window, string state)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"shape-edit-{state}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
