using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Presentation.Fakes.Library;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Library;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #187: the Library's detail rail shows EVERY path of a four-path song without scrolling, at
///     1280×720 and at 1920×1080, with and without cover art. The rail's whole job is the path
///     pick ("Load chart" opens what is picked there), and a fixed 278 px cover box left one of
///     four on screen at 1280×720. The cover takes only the height the details leave
///     (<see cref="YieldingHeaderPanel" />), and a song with no art has no cover box.
///     <para>
///         The sizes are the Library's own area inside the window: the charting context loses the
///         76 px activity rail, the 38 px title bar and the status bar (taken as 30 px); the playing
///         context is full-bleed under the 44 px back bar. Text wraps differently in de-DE and
///         ja-JP, so this class is in the release's culture re-run (<c>$HeadlessLayoutFilter</c>).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class LibraryDetailRailUiPerfSmokeTests
{
    public static TheoryData<string, int, int, bool> Cases()
    {
        var data = new TheoryData<string, int, int, bool>();
        var sizes = new[] { (1204, 652), (1280, 676), (1844, 1012), (1920, 1036) };
        foreach (var (variant, (width, height), cover) in
                 from v in new[] { "Default", "LongText" } from size in sizes from c in new[] { false, true } select (v, size, c))
        {
            data.Add(variant, width, height, cover);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryPathOfAFourPathSong_IsOnScreen(string variant, int width, int height, bool cover)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = (FakeLibraryScreen)FakeLibraryScreen.Variants.Single(v => v.Name == variant).Create();
            Assert.Equal(4, screen.Paths.Count); // both fixtures select a song with all four paths
            using var art = cover ? new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul) : null;
            screen.DetailCover = art;
            var view = new LibraryView { DataContext = screen };
            WithLaidOut(view, width, height, () =>
            {
                var rail = Named<Border>(view, "LibraryDetail");
                var body = Named<YieldingHeaderPanel>(view, "LibraryDetailBody");
                var scroll = Named<ScrollViewer>(view, "DetailScroll");
                var paths = Named<ListBox>(view, "DetailPaths");
                Assert.True(rail.Bounds is { Width: > 1, Height: > 1 }, $"the rail laid out as {rail.Bounds}");
                Assert.True(body.Bounds is { Width: > 1, Height: > 1 }, $"the rail's body laid out as {body.Bounds}");

                var rows = paths.GetRealizedContainers().OfType<ListBoxItem>().ToList();
                // The list inside the rail's scroller realizes only the rows in its viewport.
                Assert.True(rows.Count == 4,
                    $"{variant} {width}x{height} cover={cover}: {rows.Count} of 4 path rows are on screen (cover {body.HeaderHeight:0.#} px, viewport {scroll.Bounds.Height:0.#} px)");
                var viewport = scroll.Bounds.Height;
                var railBottom = rail.TranslatePoint(new Point(0, rail.Bounds.Height), view)!.Value.Y;
                foreach (var row in rows)
                {
                    var top = row.TranslatePoint(default, scroll)!.Value.Y;
                    var bottom = top + row.Bounds.Height;
                    Assert.True(row.Bounds.Height > 1, $"{row.DataContext} measured {row.Bounds}");
                    Assert.True(top >= -0.5 && bottom <= viewport + 0.5,
                        $"{variant} {width}x{height} cover={cover}: the path '{(row.DataContext as ILibraryPath)?.Label}' " +
                        $"spans {top:0.#}..{bottom:0.#} in a {viewport:0.#} px viewport (cover {body.HeaderHeight:0.#} px)");
                    Assert.True(row.TranslatePoint(new Point(0, row.Bounds.Height), view)!.Value.Y <= railBottom + 0.5, "a path row below the rail");
                }

                var artBox = Named<Border>(view, "DetailArt");
                if (!cover)
                {
                    Assert.False(artBox.IsVisible, "a song without art still reserves a cover box");
                    Assert.Equal(0, body.HeaderHeight);
                }
                else if (height >= 1000)
                {
                    // A tall window still shows the cover (whole for the plain song; the long-text one gives
                    // up a few pixels to its wrapped title): the yield only takes what the details need.
                    var whole = variant == "Default" ? 278 : 240;
                    Assert.True(body.HeaderHeight >= whole - 0.5 && Math.Abs(artBox.Bounds.Height - body.HeaderHeight) < 1,
                        $"{variant} {width}x{height}: the cover was given {body.HeaderHeight:0.#} px and drawn {artBox.Bounds.Height:0.#} px tall (wanted {whole}+)");
                }
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The header's Refresh answer is on screen and inside the view, beside the folder verbs.</summary>
    [Fact]
    public async Task TheRefreshAnswer_IsShownInTheHeader()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new LibraryView { DataContext = FakeLibraryScreen.Variants.Single(v => v.Name == "Refreshed").Create() };
            WithLaidOut(view, 1204, 652, () =>
            {
                var line = Named<TextBlock>(view, "LibraryRefreshResult");
                Assert.True(line.IsEffectivelyVisible);
                Assert.True(line.Bounds is { Width: > 1, Height: > 1 }, $"the refresh answer laid out as {line.Bounds}");
                var right = line.TranslatePoint(new Point(line.Bounds.Width, 0), view)!.Value.X;
                Assert.True(right <= view.Bounds.Width + 0.5, $"the refresh answer ends at {right:0.#} in a {view.Bounds.Width:0.#} px view");
            });
        }, TestContext.Current.CancellationToken);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    private static void WithLaidOut(Control view, int width, int height, Action assert)
    {
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}
