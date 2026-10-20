using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Presentation.Fakes.Library;
using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.Controls;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Library;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The redesigned Library (wave 5) over its fake, in a real window: every variant builds and
///     lays out, the column captions sit over their columns, a row prints only the paths the song
///     has, and first run is the folder card alone — no empty rail, no footer of dead buttons.
/// </summary>
[Collection("HeadlessUI")]
public sealed class LibraryConstructionUiPerfSmokeTests
{
    public static TheoryData<string> Variants => [.. FakeLibraryScreen.Variants.Select(v => v.Name)];

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task EveryVariant_BuildsAndLaysOut(string variant)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new LibraryView { DataContext = FakeLibraryScreen.Variants.Single(v => v.Name == variant).Create() };
            WithLaidOut(view, () =>
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{variant} laid out as {view.Bounds}"));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheCaptions_SitOverTheirColumns_AndARow_ShowsOnlyItsPaths()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeLibraryScreen.Default();
            var view = new LibraryView { DataContext = screen };
            WithLaidOut(view, () =>
            {
                // One caption cell per column of the layout, plus the Status funnel's stand-in.
                var header = Named<Border>(view, "LibraryColumnHeader");
                var cells = ((SongTablePanel)header.Child!).Children.Where(c => SongTablePanel.GetStandInFor(c) < 0).ToList();
                Assert.Equal(SongTableLayout.Library.Columns.Count - 1, cells.Count); // the cover column has no caption
                // A cell is one caption, or (the song column) a row of them.
                var captions = cells.SelectMany(c => c is StackPanel group ? group.Children : [c]).OfType<ContentControl>().ToList();

                // The difficulty column: caption and value START at the same x (#212). Measured on the
                // caption's TEXT: the caption's host spans its column whatever its content does.
                var row = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "RowDifficulty");
                var caption = captions.Single(c => ReferenceEquals(c.Content, screen.DifficultyColumn));
                var label = caption.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == screen.DifficultyColumn.Title);
                var captionLeft = label.TranslatePoint(default, view)!.Value.X;
                var valueLeft = row.TranslatePoint(new Point(row.TextLayout.HitTestTextPosition(0).X, 0), view)!.Value.X;
                Assert.True(Math.Abs(captionLeft - valueLeft) < 2,
                    $"the Diff caption starts at {captionLeft:F1}, its values at {valueLeft:F1}");

                var strips = view.GetVisualDescendants().OfType<FireChipStrip>().Where(s => s.Name == "RowPaths").ToList();
                Assert.NotEmpty(strips);
                Assert.Contains(strips, s => s.Initials is { Count: 4 });
                Assert.All(strips, s => Assert.DoesNotContain("", s.Initials ?? []));

                // A song with all four paths fits the column: the strip's natural width from its own
                // geometry (a measured DesiredSize is clamped to the slot and could never fail).
                Assert.All(strips, s =>
                {
                    var wants = FireChipStripGeometry.RowWidth(s.Initials?.Count ?? 0, s.ChipSize, s.Gap);
                    Assert.True(wants <= s.Bounds.Width + 1, $"a path strip wants {wants:0.#} px in a {s.Bounds.Width:0.#} px column");
                });
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     #306: the header is the one sort control, so the song column captions all three of its
    ///     orders — artist, title, pack / file — and each one sorts and lights. A caption that does
    ///     not lay out (a binding or style fault) has no size, so the sizes are asserted.
    /// </summary>
    [Fact]
    public async Task TheSongColumn_CaptionsItsThreeOrders_AndEachSorts()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeLibraryScreen.Default();
            var view = new LibraryView { DataContext = screen };
            WithLaidOut(view, () =>
            {
                var group = Named<StackPanel>(view, "LibrarySongSorts");
                var buttons = group.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("ColumnSort")).ToList();
                Assert.Equal(
                    [screen.SongColumn.Title, screen.TitleSort.Title, screen.ArchiveSort.Title],
                    buttons.Select(b => ((ILibraryColumn)b.DataContext!).Title));
                Assert.All(buttons, b => Assert.True(b.IsEffectivelyVisible && b.Bounds is { Width: > 1, Height: > 1 },
                    $"the '{((ILibraryColumn)b.DataContext!).Title}' sort caption laid out as {b.Bounds}"));

                // The three fit their column: the last caption ends inside the song cell.
                var right = buttons[^1].TranslatePoint(new Point(buttons[^1].Bounds.Width, 0), group)!.Value.X;
                Assert.True(right <= group.Bounds.Width + 0.5, $"the song column's sort captions run to {right:0.#} px in a {group.Bounds.Width:0.#} px cell");

                buttons[1].Command!.Execute(null);
                Assert.Equal("↑", screen.TitleSort.SortGlyph);
                Assert.Equal("", screen.SongColumn.SortGlyph);
                Assert.Equal(screen.Rows.Select(r => r.Title).Order(StringComparer.OrdinalIgnoreCase), screen.Rows.Select(r => r.Title));

                buttons[2].Command!.Execute(null);
                Assert.Equal("↑", screen.ArchiveSort.SortGlyph);
                Assert.Equal("", screen.TitleSort.SortGlyph);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The wired Library — <see cref="LibraryScreen" /> over the real song browser — at the
    ///     owner's scale (1,800 songs): the list builds a container only for the rows it shows.
    ///     Unvirtualized, one layout of a list this size runs for tens of seconds.
    /// </summary>
    [Fact]
    public async Task AnOwnerSizedLibrary_RealizesOnlyTheRowsOnScreen()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var h = await LibraryResponsivenessUiPerfSmokeTests.Scanned();
            var screen = new LibraryScreen(h.Vm);
            var view = new LibraryView { DataContext = screen };
            WithLaidOut(view, () =>
            {
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"the Library laid out as {view.Bounds}");
                var list = LibraryResponsivenessUiPerfSmokeTests.Rows(view);
                Assert.True(screen.Rows.Count >= SyntheticSongLibrary.OwnerScale - 1, $"only {screen.Rows.Count} rows - the fixture is not owner-sized");
                Assert.Equal(screen.Rows.Count, list.ItemCount);

                var viewport = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                var realized = list.GetRealizedContainers().ToList();
                var onScreen = realized.Count(c =>
                {
                    var top = c.TranslatePoint(default, viewport)!.Value.Y;
                    return top < viewport.Bounds.Height && top + c.Bounds.Height > 0;
                });
                Assert.InRange(onScreen, 5, 40);
                Assert.True(realized.Count <= onScreen + 2,
                    $"{realized.Count} rows realized for {onScreen} on screen out of {list.ItemCount} - the list stopped virtualizing");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     #708: a DotCharter song package says so in its row — a tag ahead of its subtitle that lays
    ///     out with a size, at the subtitle's size so the row keeps the height every other row has —
    ///     and the one this computer cannot play shows its problem.
    /// </summary>
    [Fact]
    public async Task APackageRow_CarriesItsTag_AtTheHeightOfEveryOtherRow()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new LibraryView { DataContext = FakeLibraryScreen.Packages() };
            WithLaidOut(view, () =>
            {
                var rows = view.GetVisualDescendants().OfType<LibraryRowView>().Where(r => r.IsEffectivelyVisible).ToList();
                var tagged = rows.Where(r => r.DataContext is ILibraryRow { HasSourceTag: true }).ToList();
                var plain = rows.Where(r => r.DataContext is ILibraryRow { HasSourceTag: false, HasGroupHeader: false }).ToList();
                Assert.True(tagged.Count >= 2, $"{tagged.Count} package rows realized");
                Assert.NotEmpty(plain);

                foreach (var row in tagged)
                {
                    var tag = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RowSourceTag");
                    Assert.True(tag.IsEffectivelyVisible && tag.Bounds is { Width: > 1, Height: > 1 }, $"the tag laid out as {tag.Bounds}");
                    Assert.Equal("DotCharter song", tag.Text);
                }

                Assert.All(plain, r => Assert.False(
                    r.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RowSourceTag").IsEffectivelyVisible));
                // ...and a plain row's subtitle still starts under its title, as it did before the tag.
                Assert.All(plain, r =>
                {
                    var texts = r.GetVisualDescendants().OfType<TextBlock>().ToList();
                    var title = texts.Single(t => t.Name == "RowTitle").TranslatePoint(default, r)!.Value.X;
                    var sub = texts.Single(t => t.Name == null && t.Classes.Contains("LibraryRowSub")).TranslatePoint(default, r)!.Value.X;
                    Assert.True(Math.Abs(title - sub) < 0.5, $"the subtitle starts at {sub:0.#}, its title at {title:0.#}");
                });
                var plainHeight = plain[0].Bounds.Height;
                var taggedPlain = tagged.Where(r => r.DataContext is ILibraryRow { HasGroupHeader: false }).ToList();
                Assert.NotEmpty(taggedPlain);
                Assert.All(taggedPlain,
                    r => Assert.True(Math.Abs(r.Bounds.Height - plainHeight) < 0.5, $"a package row is {r.Bounds.Height:0.#} px, a plain one {plainHeight:0.#}"));
            });

            var needsFfmpeg = new LibraryView { DataContext = FakeLibraryScreen.PackageNeedsFfmpeg() };
            WithLaidOut(needsFfmpeg, () =>
            {
                var blocked = needsFfmpeg.GetVisualDescendants().OfType<LibraryRowView>()
                    .Single(r => r.DataContext is ILibraryRow { HasSourceTag: true, HasProblem: true });
                var status = blocked.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RowStatus");
                Assert.True(status.IsEffectivelyVisible && status.Bounds.Width > 1, $"the problem laid out as {status.Bounds}");
                Assert.Equal("Needs ffmpeg", status.Text);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FirstRun_IsTheFolderCardAlone()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new LibraryView { DataContext = FakeLibraryScreen.Empty() };
            WithLaidOut(view, () =>
            {
                Assert.True(Named<StackPanel>(view, "LibraryFirstRun").IsEffectivelyVisible);
                Assert.False(Named<Border>(view, "LibraryDetail").IsEffectivelyVisible, "an empty detail rail on first run");
                Assert.False(Named<Border>(view, "LibraryFooter").IsEffectivelyVisible, "a footer of disabled buttons on first run");
                Assert.False(Named<Border>(view, "LibraryColumnHeader").IsEffectivelyVisible);
            });
        }, CancellationToken.None);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    private static void WithLaidOut(Control view, Action assert)
    {
        var window = new Window { Width = 1600, Height = 900, Content = view };
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
