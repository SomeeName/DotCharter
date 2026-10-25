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
using DotCharter.Tests.Services.Editing.ChordShapes;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Clean up shapes dialog bound to the REAL view model over a messy chart (the catalog sweep draws it on
///     its fake), opened the way the app opens it: a real <see cref="DialogHost" /> inside the real
///     <see cref="DialogContainerView" />, which is where the dialog grammar (<c>DialogStyles.axaml</c>) comes
///     from. A bare window around the view has no such grammar, so the card and the section cards lose their
///     styling while the controls still measure; hence the assertions on resolved brushes and classes, not only on
///     size: the card and each of the three section cards carry a theme background, Apply is the primary button in
///     the right-hand footer, and every row of every group is on screen with its diagram laid out.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapeCleanupDialogLiveUiPerfSmokeTests
{
    private const double HostWidth = 1600;
    private const double HostHeight = 900;

    [Fact]
    public async Task TheDialog_OverAMessyChart_HostedLikeTheApp_IsStyled_AndTheDecisionsRecountApply()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var h = new ChartEditHarness();
            var messy = new MessyShapesFixture();
            h.Project.Arrangements.Add(messy.Arrangement);
            h.Editor.ActiveArrangement = messy.Arrangement;
            Run(h, vm =>
            {
                Assert.Equal(2, vm.Duplicates.Count);
                Assert.Equal(3, vm.Unused.Count);
                Assert.Single(vm.NearDuplicates);
                return "default";
            }, vm =>
            {
                vm.Unused[0].IsIncluded = false;
                vm.NearDuplicates[0].Choices[1].SelectCommand.Execute(null);
                return "decided";
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ABigImport_ListsEveryRow_AndStaysInsideTheCard()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var h = new ChartEditHarness();
            var arrangement = BigImport();
            h.Project.Arrangements.Add(arrangement);
            h.Editor.ActiveArrangement = arrangement;
            Run(h, vm =>
            {
                Assert.Equal(12, vm.Duplicates.Count);
                Assert.Equal(30, vm.Unused.Count);
                Assert.Equal(8, vm.NearDuplicates.Count);
                return "full";
            });
        }, CancellationToken.None);
    }

    /// <summary>Hosts the dialog like the app, lays it out and checks it after each step; every step names its capture.</summary>
    private static void Run(ChartEditHarness h, params Func<ShapeCleanupViewModel, string>[] steps)
    {
        ShapeCleanupViewModel? vm = null;
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped<IShapeCleanupScreen>(_ => vm = new ShapeCleanupViewModel(h.Services));
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
            var answer = host.ShowAsync<IShapeCleanupScreen, bool>();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(vm);
            var view = Assert.IsType<ShapeCleanupDialogView>(Assert.Single(host.OpenViews));
            foreach (var step in steps)
            {
                var state = step(vm);
                Layout(container);
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"the dialog laid out as {view.Bounds}");
                AssertStyled(view, vm);
                Shoot(window, state);
            }

            host.CloseTop();
            Dispatcher.UIThread.RunJobs();
            Assert.True(answer.IsCompleted);
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }

    private static void Layout(Control root)
    {
        Dispatcher.UIThread.RunJobs();
        root.Measure(new Size(HostWidth, HostHeight));
        root.Arrange(new Rect(0, 0, HostWidth, HostHeight));
        root.UpdateLayout();
    }

    private static void AssertStyled(ShapeCleanupDialogView view, ShapeCleanupViewModel vm)
    {
        var all = view.GetVisualDescendants().ToList();
        var texts = all.OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(vm.Summary, texts);
        Assert.Contains(vm.DuplicatesHeader, texts);
        Assert.Contains(vm.UnusedHeader, texts);
        Assert.Contains(vm.NearDuplicatesHeader, texts);

        var card = Assert.Single(all.OfType<Border>(), b => b.Classes.Contains("DialogCard"));
        Assert.True(IsPainted(card.Background), "the dialog card has no theme background: the dialog grammar was not applied");
        Assert.True(card.Bounds.Width > 400 && card.Bounds.Width < view.Bounds.Width, $"the card is {card.Bounds.Width} wide in a {view.Bounds.Width} host");
        Assert.True(card.Bounds.Height <= HostHeight, $"the card is {card.Bounds.Height} tall in a {HostHeight} host");

        var sections = all.OfType<Border>().Where(b => b.Classes.Contains("DlgSection")).ToList();
        Assert.Equal(3, sections.Count);
        Assert.All(sections, s => Assert.True(IsPainted(s.Background) && s.Bounds.Height > 1, "a section card is unstyled"));
        Assert.Equal(["ShapeCleanupDuplicates", "ShapeCleanupReview", "ShapeCleanupUnused"], sections.Select(s => s.Name));

        var checks = all.OfType<CheckBox>().ToList();
        Assert.Equal(vm.Duplicates.Count + vm.Unused.Count, checks.Count);
        Assert.All(checks, c => Assert.True(c.Bounds.Width > 1 && c.Bounds.Height > 1, "a tick measured nothing"));
        var diagrams = all.OfType<DotCharter.UI.Controls.ChordDiagramControl>().ToList();
        Assert.Equal(vm.Duplicates.Count + vm.Unused.Count + 2 * vm.NearDuplicates.Count, diagrams.Count);
        Assert.All(diagrams, d => Assert.True(d.Bounds.Width > 1 && d.Bounds.Height > 1, "a chord diagram measured nothing"));
        var choices = all.OfType<RadioButton>().ToList();
        Assert.Equal(3 * vm.NearDuplicates.Count, choices.Count);
        Assert.Equal(vm.NearDuplicates.Count, choices.Count(c => c.IsChecked == true));

        var apply = Assert.Single(all.OfType<Button>(), b => Equals(b.Content, vm.ApplyLabel));
        Assert.True(apply.Classes.Contains("Primary"), "Apply is not the primary button");
        Assert.True(IsPainted(apply.Background), "Apply has no primary background");
        var cancel = Assert.Single(all.OfType<Button>(), b => b.IsCancel);
        Assert.True(apply.TranslatePoint(default, card)!.Value.X > cancel.TranslatePoint(default, card)!.Value.X, "the footer pair is not Cancel then Apply");
        Assert.True(apply.TranslatePoint(new Point(apply.Bounds.Width, 0), card)!.Value.X > card.Bounds.Width * 0.7, "the footer pair is not right-aligned");
    }

    /// <summary>
    ///     A long import: twelve identical pairs, thirty shapes nothing uses and eight pairs that differ by one finger,
    ///     each on frets of its own so no two groups meet.
    /// </summary>
    private static Arrangement BigImport()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        var id = 0;
        var beat = 0;
        ChordTemplate Add(string name, int fret, int finger)
        {
            var t = new ChordTemplate
            {
                Id = id++, Name = name, Frets = new() { [2] = fret, [3] = fret + 2 }, Fingers = new() { [2] = finger, [3] = 3 }
            };
            arrangement.ChordTemplates.Add(t);
            return t;
        }

        void Use(ChordTemplate t) => level.Chords.Add(ChordShapesFixture.ChordOn(t, beat++ * 2));
        for (var g = 0; g < 12; g++)
        {
            Use(Add(g % 2 == 0 ? "" : $"G{g}", 1 + g, 1));
            Use(Add("", 1 + g, 1));
        }

        for (var p = 0; p < 8; p++)
        {
            Use(Add("", 20 + p, 1));
            Use(Add("", 20 + p, 2));
        }

        for (var u = 0; u < 30; u++) Add("", 40 + u, 1);
        return arrangement;
    }

    private static bool IsPainted(IBrush? brush) => brush is ISolidColorBrush { Color.A: > 0 } or IGradientBrush;

    private static void Shoot(Window window, string state)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"shape-cleanup-{state}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
