using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.UI.Views.PlayMode.Redesign;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The redesigned Song Select screen, built for real against each fake
///     variant, attached to a 1600 × 900 window and laid out.
///     <para>
///         A view whose styles failed still ARRANGES — it just has no size — so
///         every assertion here is "this named part measured non-zero", which is
///         the shape every silent XAML fault in this repo takes: an unresolvable
///         <c>StaticResource</c>, a <c>Padding="{DynamicResource SpaceMd}"</c>
///         (<c>Space*</c> is an <c>x:Double</c> and <c>Padding</c> a
///         <c>Thickness</c>, so the cast throws on attach), a Fire class that
///         matches no selector, or a view that fails to construct and renders as
///         a "Not Found: …" placeholder.
///     </para>
///     <para>
///         <see cref="EveryRowsText_StaysInsideItsParent_UnderLongText" /> is the
///         one that exists because of a REPORTED bug rather than a class of
///         them: a horizontal panel arranges its children at their own desired
///         size and clips nothing, so it reports a correct rectangle and paints
///         into the next Grid cell. An empty fixture cannot show that, which is
///         why <c>FakeSongSelectScreen.LongText()</c> exists.
///     </para>
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///         <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, each variant is also written to
///         <c>songselect-redesign-&lt;variant&gt;.png</c>, the same way
///         <see cref="PlayModeScreensCaptureUiPerfSmokeTests" /> captures the
///         shipped screens.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongSelectRedesignUiPerfSmokeTests
{
    private const int WindowWidth = 1600;
    private const int WindowHeight = 900;

    public static IEnumerable<object[]> Variants() =>
        [["Library"], ["Empty"], ["NoMatch"], ["LongText"], ["Full"], ["Played"], ["Loading"],
            ["Setlist"], ["SetlistEmpty"], ["SetlistEnded"]];

    private static ISongSelectScreen Fake(string variant) => variant switch
    {
        "Library" => FakeSongSelectScreen.Library(),
        "Empty" => FakeSongSelectScreen.Empty(),
        "NoMatch" => FakeSongSelectScreen.NoMatch(),
        "LongText" => FakeSongSelectScreen.LongText(),
        "Full" => FakeSongSelectScreen.Full(),
        "Played" => FakeSongSelectScreen.Played(),
        "Loading" => FakeSongSelectScreen.Loading(),
        "Setlist" => FakeSongSelectScreen.Setlist(),
        "SetlistEmpty" => FakeSongSelectScreen.SetlistEmpty(),
        "SetlistEnded" => FakeSongSelectScreen.SetlistEnded(),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "unknown fake variant")
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task EveryVariant_BuildsAttachesMeasures_AndIsCaptured(string variant)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = Fake(variant);
            WithScreen(screen, (view, window) =>
            {
                Assert.True(view.Bounds is { Width: > 1000, Height: > 500 },
                    $"{variant}: the screen laid out as {view.Bounds} — it rendered nothing");

                AssertMeasured(view, "SongSelectRedesignRoot", variant);
                AssertMeasured(view, "SongSelectRedesignTopBar", variant);
                AssertMeasured(view, "SongSelectRedesignBack", variant);
                if (!screen.ShowFirstRun)
                {
                    AssertMeasured(view, "SongSelectRedesignDetailRail", variant);
                    AssertMeasured(view, "SongSelectRedesignPlayButton", variant);
                    AssertMeasured(view, "SongSelectSetlistPanel", variant);
                    AssertMeasured(view, "SetlistPlayButton", variant);
                }

                // The list is IsVisible-gated on there being rows; an
                // IsVisible=false subtree has no size by design, so it is only
                // asserted where it is meant to be on screen.
                if (screen.HasRows)
                {
                    AssertMeasured(view, "SongSelectRedesignList", variant);
                    AssertMeasured(view, "SongSelectRedesignColumnHeader", variant);
                }

                Capture(window, variant);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Library_RealizesRows_AndNeitherEmptyStateShows()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(FakeSongSelectScreen.Library(), (view, _) =>
            {
                var rows = RealizedRows(view);
                Assert.True(rows.Count >= 5,
                    $"only {rows.Count} row(s) realized — the list template never ran");
                Assert.All(rows, r => Assert.True(r.Bounds is { Width: > 200, Height: > 20 },
                    $"a row laid out as {r.Bounds} — its template measured nothing"));

                Assert.False(Named(view, "SongSelectRedesignFirstRunPanel").IsVisible);
                Assert.False(Named(view, "SongSelectRedesignNoMatchPanel").IsVisible);
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     2,160 songs lay out because the list VIRTUALIZES: only the rows in view get a
    ///     container. A list that realized every row would still pass the measure checks —
    ///     slowly — so this asserts the structure and logs the time.
    /// </summary>
    [Fact]
    public async Task Full_RealizesOnlyTheRowsInView_Of2160()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var screen = FakeSongSelectScreen.Full();
            WithScreen(screen, (view, _) =>
            {
                Assert.Equal(2_160, screen.Rows.Count);
                var rows = RealizedRows(view);
                Assert.True(rows.Count >= 5, $"only {rows.Count} row(s) realized — the list template never ran");
                Assert.True(rows.Count < 200,
                    $"{rows.Count} of {screen.Rows.Count} rows realized — the song list stopped virtualizing");
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Full: {rows.Count} of {screen.Rows.Count} rows realized; build + first layout {started.ElapsedMilliseconds} ms");
            });
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Empty", "SongSelectRedesignFirstRunPanel", "SongSelectRedesignNoMatchPanel")]
    [InlineData("NoMatch", "SongSelectRedesignNoMatchPanel", "SongSelectRedesignFirstRunPanel")]
    public async Task AnEmptyVariant_ShowsExactlyItsOwnPanel(string variant, string shown, string hidden)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(Fake(variant), (view, _) =>
            {
                var expected = Named(view, shown);
                Assert.True(expected.IsVisible, $"{variant}: {shown} is hidden");
                Assert.True(expected.Bounds is { Width: > 100, Height: > 60 },
                    $"{variant}: {shown} laid out as {expected.Bounds} — its card measured nothing");
                Assert.False(Named(view, hidden).IsVisible, $"{variant}: {hidden} is showing too");
                Assert.Empty(RealizedRows(view));
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThePlayButton_IsDisabledWithNoSelection_AndEnabledWithOne()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(FakeSongSelectScreen.Empty(), (view, _) =>
                Assert.False(Named(view, "SongSelectRedesignPlayButton").IsEffectivelyEnabled,
                    "Play is live with nothing selected"));

            WithScreen(FakeSongSelectScreen.Library(), (view, _) =>
                Assert.True(Named(view, "SongSelectRedesignPlayButton").IsEffectivelyEnabled,
                    "Play is dead with a playable song selected"));
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     No text in a row draws outside the box that contains it.
    ///     <para>
    ///         Bounds are relative to the parent, so the test is
    ///         <c>child.Bounds.Right &lt;= parent.Bounds.Width</c>. A horizontal
    ///         panel that overflows still reports a correct rectangle for itself,
    ///         which is exactly why a bounds assertion on the ROW passes while the
    ///         timestamp paints over the subtitle.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task EveryRowsText_StaysInsideItsParent_UnderLongText()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(FakeSongSelectScreen.LongText(), (view, _) =>
            {
                var rows = RealizedRows(view);
                Assert.NotEmpty(rows);

                var overflows = new List<string>();
                foreach (var row in rows)
                {
                    foreach (var text in row.GetVisualDescendants().OfType<TextBlock>())
                    {
                        if (text.GetVisualParent() is not Visual parent) continue;
                        if (text.Bounds.Width <= 0) continue;
                        if (text.Bounds.Right > parent.Bounds.Width + 1)
                        {
                            overflows.Add(
                            $"'{Trim(text.Text)}' arranged to x={text.Bounds.Right:0.#} in a "
                            + $"{parent.GetType().Name} {parent.Bounds.Width:0.#} px wide");
                        }
                    }
                }

                Assert.True(overflows.Count == 0,
                    "text drew outside its container (the clipped-text / cell-overlap class):"
                    + Environment.NewLine + string.Join(Environment.NewLine, overflows.Take(12)));
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The path chips fit their column. A chip strip draws its own chips, so the text check
    ///     above cannot see them: five chips in an 80 px column painted over the tuning beside it
    ///     (gallery capture, 2026-09-24).
    /// </summary>
    [Fact]
    public async Task ThePathChips_FitTheirColumn_UnderLongText()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(FakeSongSelectScreen.LongText(), (view, _) =>
            {
                var strips = view.GetVisualDescendants().OfType<DotCharter.Tone.UI.Controls.Fire.FireChipStrip>().ToList();
                Assert.NotEmpty(strips);
                // The strip's natural width from its own geometry: a measured DesiredSize is clamped
                // to what the column offers, which would make this pass vacuously.
                Assert.All(strips, s =>
                {
                    var wants = DotCharter.Tone.UI.Controls.Fire.FireChipStripGeometry.RowWidth(s.Initials?.Count ?? 0, s.ChipSize, s.Gap);
                    Assert.True(wants <= s.Bounds.Width + 1, $"a path strip wants {wants:0.#} px in a {s.Bounds.Width:0.#} px column");
                });
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The Play button never covers the rail's body, and the best-run panel
    ///     is READABLE IN FULL AT REST under the longest content the screen can
    ///     hold.
    ///     <para>
    ///         The rail is a two-row Grid — a scrolling body in the <c>*</c> row,
    ///         the CTA in the <c>Auto</c> row — so the two can never overlap, which
    ///         was never the fault. What WAS wrong is that the record sat last in
    ///         the body, so with five arrangement tiles it was below the fold and
    ///         the capture showed a grade badge and an accuracy figure sliced in
    ///         half. The rail is ordered record-then-picker now, and this asserts
    ///         the panel is whole WITHOUT scrolling. The <c>Extent &gt; Viewport</c>
    ///         check stops it passing vacuously on content that happens to fit.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheRailBodyScrolls_AndTheBestRunPanelIsWholeAtRest()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(FakeSongSelectScreen.LongText(), (view, window) =>
            {
                var scroller = (ScrollViewer)Named(view, "SongSelectRedesignRailScroll");
                var play = Named(view, "SongSelectRedesignPlayButton");

                var scrollTop = Top(scroller, window);
                var playTop = Top(play, window);

                Assert.True(scrollTop + scroller.Bounds.Height <= playTop + 0.5,
                    $"the rail body runs to y={scrollTop + scroller.Bounds.Height:0.#} but the Play button "
                    + $"starts at y={playTop:0.#} — the CTA is painted over the body");

                Assert.True(scroller.Extent.Height > scroller.Viewport.Height + 1,
                    $"the rail body measured {scroller.Extent.Height:0.#} px into a "
                    + $"{scroller.Viewport.Height:0.#} px viewport — LongText no longer overflows the "
                    + "rail, so this case proves nothing about scrolling");

                // AT REST — no scrolling. This is the frame the owner reviews, and
                // the record is the one thing on this rail that must never be cut:
                // a grade badge and an accuracy figure sliced through the middle
                // read as a rendering fault, not as "there is more below".
                Assert.Equal(0d, scroller.Offset.Y);

                var panel = Named(view, "SongSelectRedesignBestRunPanel");
                var panelTop = Top(panel, window);
                var panelBottom = panelTop + panel.Bounds.Height;

                Assert.True(panel.Bounds is { Width: > 100, Height: > 40 },
                    $"the best-run panel laid out as {panel.Bounds} — it rendered nothing");
                Assert.True(panelTop >= scrollTop - 0.5 && panelBottom <= playTop + 0.5,
                    $"at rest the best-run panel occupies y={panelTop:0.#}..{panelBottom:0.#} "
                    + $"while the body's viewport is y={scrollTop:0.#}..{scrollTop + scroller.Bounds.Height:0.#} "
                    + $"and Play starts at y={playTop:0.#} — the record is cut off without being scrolled to");
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     With only two arrangement tiles the CTA still sits on the rail's
    ///     FLOOR, not directly under the tiles — the action's place does not
    ///     move with the amount of content above it.
    /// </summary>
    [Fact]
    public async Task WithShortContent_ThePlayButton_StaysOnTheRailFloor()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(FakeSongSelectScreen.Library(), (view, window) =>
            {
                var rail = Named(view, "SongSelectRedesignDetailRail");
                var play = Named(view, "SongSelectRedesignPlayButton");
                var scroller = (ScrollViewer)Named(view, "SongSelectRedesignRailScroll");
                var details = Named(view, "SongSelectRedesignDetailBody");

                var railBottom = Top(rail, window) + rail.Bounds.Height;
                var playBottom = Top(play, window) + play.Bounds.Height;

                // FireContextRail's own padding is the only gap allowed below it.
                Assert.True(railBottom - playBottom <= 32,
                    $"Play ends at y={playBottom:0.#} inside a rail that ends at y={railBottom:0.#} — "
                    + "the CTA has floated up under the arrangement tiles");

                // The song's details alone; the setlist panel under them (#646) may scroll.
                Assert.True(details.Bounds.Height <= scroller.Viewport.Height + 1,
                    $"the two-tile details already overflow ({details.Bounds.Height:0.#} into "
                    + $"{scroller.Viewport.Height:0.#}) — this case no longer describes short content");
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The setlist panel (#646) — the app's own <c>SetlistPanelView</c> on the fake
    ///     <c>ISetlistPanel</c> — sits under the song's details inside the rail, its songs
    ///     laid out within the rail's width, and its verbs inside the scrolling body.
    /// </summary>
    [Theory]
    [InlineData("Setlist")]
    [InlineData("Full")]
    [InlineData("LongText")]
    public async Task TheSetlistPanel_SitsUnderTheDetails_InsideTheRail(string variant)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(Fake(variant), (view, window) =>
            {
                var rail = Named(view, "SongSelectRedesignDetailRail");
                var details = Named(view, "SongSelectRedesignDetailBody");
                var panel = Named(view, "SongSelectSetlistPanel");

                Assert.True(Top(panel, window) >= Top(details, window) + details.Bounds.Height,
                    $"{variant}: the setlist panel starts at y={Top(panel, window):0.#}, inside the song's details");
                var railLeft = rail.TranslatePoint(default, window)!.Value.X;
                foreach (var name in new[] { "SongSelectSetlistPanel", "SetlistPicker", "SetlistEntries", "SetlistAddButton", "SetlistPlayButton" })
                {
                    var part = Named(view, name);
                    var left = part.TranslatePoint(default, window)!.Value.X;
                    Assert.True(part.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: {name} laid out as {part.Bounds}");
                    Assert.True(left >= railLeft - 0.5 && left + part.Bounds.Width <= railLeft + rail.Bounds.Width + 0.5,
                        $"{variant}: {name} spans x={left:0.#}..{left + part.Bounds.Width:0.#} outside the rail");
                }

                var titles = Named(view, "SetlistEntries").GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 }).ToList();
                Assert.NotEmpty(titles);
                Assert.All(titles, t => Assert.True(t.Bounds.Width > 1, $"{variant}: '{Trim(t.Text)}' laid out as {t.Bounds}"));
            });
        }, TestContext.Current.CancellationToken);
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    private static void WithScreen(ISongSelectScreen screen, Action<SongSelectScreenView, Window> body)
    {
        var view = new SongSelectScreenView { DataContext = screen };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };

        // Show() roots the tree and IS the initial layout pass; the Background
        // drain is what realizes deferred template bodies.
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            body(view, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static double Top(Visual control, Window window) =>
        control.TranslatePoint(default, window)?.Y
        ?? throw new Xunit.Sdk.XunitException($"{control} is not connected to the window");

    private static Control Named(SongSelectScreenView view, string name) =>
        view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"the view has no part named '{name}'");

    private static void AssertMeasured(SongSelectScreenView view, string name, string variant)
    {
        var part = Named(view, name);
        Assert.True(part.Bounds is { Width: > 1, Height: > 1 },
            $"{variant}: {name} laid out as {part.Bounds} — it rendered nothing");
    }

    private static List<Border> RealizedRows(SongSelectScreenView view) =>
        view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("FireListRow"))
            .ToList();

    private static string Trim(string? text) =>
        text is { Length: > 40 } ? text[..40] + "…" : text ?? "";

    private static void Capture(Window window, string variant)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;

        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"songselect-redesign-{variant}.png"), new PngBitmapEncoderOptions());
    }
}
