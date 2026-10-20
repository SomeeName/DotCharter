using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Home;
using DotCharter.UI.Views.Home;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Home on its fakes, attached and laid out: the empty state says what to do
///     instead of leaving two thirds of the screen blank, and a destination card
///     has ONE frame. The card's host button once wore a class that matched no
///     selector, so the Fluent button drew a second padded frame round every card.
/// </summary>
[Collection("HeadlessUI")]
public sealed class HomeViewUiPerfSmokeTests
{
    [Fact]
    public async Task WithNothingPlayed_TheEmptyHeroTakesTheSongsPlace()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new HomeView { DataContext = FakeHomeScreen.Empty() };
            WithLaidOut(view, () =>
            {
                var empty = view.FindControl<Border>("HomeEmptyHero")!;
                Assert.True(empty.IsVisible && empty.Bounds is { Width: > 400, Height: > 300 },
                    $"the empty hero laid out as {empty.Bounds}, visible={empty.IsVisible}");
                Assert.False(view.FindControl<Border>("HomeHero")!.IsVisible);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithASong_TheHeroShows_AndEachDestinationHasOneFrame()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new HomeView { DataContext = FakeHomeScreen.Full() };
            WithLaidOut(view, () =>
            {
                Assert.True(view.FindControl<Border>("HomeHero")!.IsVisible);
                Assert.False(view.FindControl<Border>("HomeEmptyHero")!.IsVisible);

                var cards = view.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("Bare")).ToList();
                Assert.NotEmpty(cards);
                foreach (var card in cards)
                {
                    var presenter = card.GetVisualDescendants().OfType<ContentPresenter>()
                        .First(p => p.Name == "PART_ContentPresenter");
                    Assert.True(presenter.Background is null or ISolidColorBrush { Color.A: 0 },
                        $"a Home card's host button paints {presenter.Background}: a second frame round the card");
                    Assert.Equal(new Thickness(0), card.Padding);
                }
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     With no cover the hero is a strummed guitar neck rather than an empty panel (owner,
    ///     2026-09-24). By default it is STILL — no clock at all — and with the Graphics setting on
    ///     it strums, and like every animated backdrop parks the moment Home is hidden: Home is an
    ///     IsVisible-flipped screen, so it would otherwise tick for the whole session.
    /// </summary>
    [Fact]
    public async Task ByDefault_TheHeroIsStill()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new HomeView { DataContext = FakeHomeScreen.Full() };
            WithLaidOut(view, () =>
            {
                Assert.False(view.FindControl<DotCharter.UI.Controls.HeroStringsBackdrop>("HomeHeroStrings")!.IsStrumming);
                Assert.False(view.FindControl<DotCharter.UI.Controls.Hero.HeroStage>("HomeHeroStage")!.IsStaging);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoCover_TheHeroStrums_AndParksWhenHidden()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new HomeView { DataContext = FakeHomeScreen.WithCover("", animate: true) };
            WithLaidOut(view, () =>
            {
                var strings = view.FindControl<DotCharter.UI.Controls.HeroStringsBackdrop>("HomeHeroStrings")!;
                Assert.True(strings.IsVisible && strings.Bounds is { Width: > 400, Height: > 300 },
                    $"the strings laid out as {strings.Bounds}, visible={strings.IsVisible}");
                Assert.False(view.FindControl<DotCharter.UI.Controls.Hero.HeroStage>("HomeHeroStage")!.HasCover);
                Assert.True(strings.IsStrumming, "the strings are not animating on a shown Home");

                view.IsVisible = false;
                Assert.False(strings.IsStrumming, "the strings kept ticking under a hidden Home");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The hero's cover is a PATH on the contract; the converter has to turn it into pixels on
    ///     its own. Its first cut asked the service provider for an artwork source nobody registers,
    ///     so Home found the cover and drew an empty tile (seen live, 2026-09-24).
    /// </summary>
    [Fact]
    public async Task ACoverPath_DecodesToABitmap()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"dc-cover-{Guid.NewGuid():N}.png");
            try
            {
                // A real PNG: the headless platform's Bitmap.Save does not write one.
                using (var source = new SkiaSharp.SKBitmap(64, 64))
                {
                    source.Erase(new SkiaSharp.SKColor(200, 60, 40));
                    using var png = source.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(path, png.ToArray());
                }

                var bitmap = DotCharter.UI.Converters.ArtworkPathToBitmapConverter.Instance.Convert(
                    path, typeof(IImage), null, System.Globalization.CultureInfo.InvariantCulture);

                Assert.True(bitmap is Avalonia.Media.Imaging.Bitmap,
                    $"the cover at {path} converted to {bitmap?.GetType().Name ?? "null"}");

                // …and a cover never sizes the hero. Home scrolls vertically, so a picture that
                // asks for its own aspect made the hero as tall as it is wide and pushed the
                // header and the cards out of view.
                var view = new HomeView { DataContext = FakeHomeScreen.WithCover(path, animate: true) };
                WithLaidOut(view, () =>
                {
                    var hero = view.FindControl<Border>("HomeHero")!;
                    var stage = view.FindControl<DotCharter.UI.Controls.Hero.HeroStage>("HomeHeroStage")!;
                    // The stage decodes the cover off the UI thread and posts it back.
                    Assert.NotNull(DotCharter.UI.Controls.Hero.HeroStage.DecodeRgba(path).Rgba);
                    Assert.True(stage.PendingLoad?.Wait(TimeSpan.FromSeconds(30)), "the cover decode never finished");
                    for (var i = 0; i < 200 && !stage.HasCover; i++)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        Thread.Sleep(25);
                    }

                    Assert.True(stage.HasCover, $"the hero stage never took the cover (CoverPath={stage.CoverPath ?? "null"}, exists={File.Exists(path)})");
                    Assert.True(stage.IsStaging, "the hero stage is not animating on a shown Home");
                    Assert.True(hero.Bounds.Height < 900,
                        $"with a cover the hero is {hero.Bounds.Height:0} px tall in a 900 px window");
                });
            }
            finally
            {
                File.Delete(path);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The header's profile and device chips stretch to the height of the
    ///     Settings button beside them, so a top-aligned caption sat high in its
    ///     chip (owner, 2026-09-24: "the text here is not centered").
    /// </summary>
    [Fact]
    public async Task TheHeaderChips_CentreTheirCaption()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new HomeView { DataContext = FakeHomeScreen.Full() };
            WithLaidOut(view, () =>
            {
                foreach (var name in new[] { "HomeProfileChip", "HomeDeviceChip" })
                {
                    var text = view.FindControl<TextBlock>(name)!;
                    var chip = (Border)text.Parent!;
                    // A STRETCHED TextBlock fills the chip and so looks centred by its bounds,
                    // while it draws its one line at the top. The line must be the box.
                    Assert.True(text.Bounds.Height <= text.DesiredSize.Height + 0.5,
                        $"{name} is stretched to {text.Bounds.Height:0.0} px for a {text.DesiredSize.Height:0.0} px line: its text sits at the top");
                    var above = text.Bounds.Top - chip.Padding.Top;
                    var below = chip.Bounds.Height - chip.Padding.Bottom - text.Bounds.Bottom;
                    Assert.True(Math.Abs(above - below) <= 1.0,
                        $"{name} sits {above:0.0} px below its chip's top padding and {below:0.0} px above its bottom one");
                }
            });
        }, CancellationToken.None);
    }

    private static void WithLaidOut(Control view, Action assert)
    {
        var window = new Window { Width = 1600, Height = 900, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}
