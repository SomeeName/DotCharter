using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The multi-song package picker, built the way the app builds it: a real
///     <see cref="DialogHost" /> in a shown window, laid out in the swept culture's
///     strings. The card, every song row and both footer buttons must MEASURE and sit
///     inside the card, for a short package, a long-named one and a compilation that
///     scrolls. Width-shaped, so it is in the release's <c>$HeadlessLayoutFilter</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class PackageSongPickerDialogUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;

    public static TheoryData<string> Variants => new("Default", "LongText", "Full");

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task EveryVariant_LaysOutInsideItsCard(string variant)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithOpenDialog(() => variant switch
            {
                "LongText" => FakePackageSongPickerScreen.LongText(),
                "Full" => FakePackageSongPickerScreen.Full(),
                _ => FakePackageSongPickerScreen.Default()
            }, (view, _) =>
            {
                var card = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogCard"));
                Assert.True(card.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: the card laid out {card.Bounds}");
                var list = view.FindControl<ListBox>("SongRows")!;
                Assert.True(list.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: the song list laid out {list.Bounds}");
                var name = view.FindControl<TextBlock>("PackageNameText")!;
                Assert.True(name.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: the package name laid out {name.Bounds}");
                AssertInside(list, card, $"{variant}: the song list");
                AssertInside(name, card, $"{variant}: the package name");
                var footer = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogFooter"));
                var buttons = footer.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();
                Assert.Equal(2, buttons.Count);
                foreach (var button in buttons) AssertInside(button, card, $"{variant}: footer button '{button.Content}'");
                Capture(view, $"package-song-picker-{variant.ToLowerInvariant()}.png");
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>Double-clicking a song row opens THAT song, not whichever was selected before.</summary>
    [Fact]
    public async Task DoubleClickingARow_OpensThatSong()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithOpenDialog(FakePackageSongPickerScreen.Default, (view, answer) =>
            {
                var list = view.FindControl<ListBox>("SongRows")!;
                var item = list.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(1);
                var window = (Window)TopLevel.GetTopLevel(view)!;
                var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.True(answer.IsCompleted, "the double-click did not answer the dialog");
                Assert.Equal(1, answer.Result);
            });
        }, TestContext.Current.CancellationToken);
    }

    private static void AssertInside(Control child, Control ancestor, string what)
    {
        var topLeft = child.TranslatePoint(default, ancestor);
        var bottomRight = child.TranslatePoint(new Point(child.Bounds.Width, child.Bounds.Height), ancestor);
        Assert.True(topLeft.HasValue && bottomRight.HasValue, $"{what} is not under the card");
        Assert.True(topLeft!.Value.X >= -1 && bottomRight!.Value.X <= ancestor.Bounds.Width + 1,
            $"{what} spans x={topLeft.Value.X:F0}..{bottomRight!.Value.X:F0} in a {ancestor.Bounds.Width:F0} px card");
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

    private static void WithOpenDialog(Func<IPackageSongPickerScreen> create, Action<Control, Task<int?>> assert)
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
            var answer = ((IDialogs)host).ShowAsync<IPackageSongPickerScreen, int?>();
            Dispatcher.UIThread.RunJobs();
            container.Measure(new Size(HostWidth, HostHeight));
            container.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            assert(Assert.Single(host.OpenViews), answer);
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
