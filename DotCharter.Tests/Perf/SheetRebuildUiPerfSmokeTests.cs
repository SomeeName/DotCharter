using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Export.Sheet;
using DotCharter.UI.Controls.Sheet;
using SkiaSharp;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class SheetRebuildUiPerfSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalNotation_SelectsTheCurrentArrangementsSounds(bool pageMode)
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var state = Fixture();
            var renderer = new ControlledRenderer();
            var view = new SheetMusicView(renderer) { ProjectState = state, PageMode = pageMode };
            var window = Show(view);
            try
            {
                await Rebuild(view);
                window.UpdateLayout();
                Assert.True(view.Bounds.Width > 0 && view.Bounds.Height > 0);
                var canvas = Assert.Single(view.GetVisualDescendants().OfType<SheetCanvas>());
                Assert.True(canvas.Bounds.Width > 0 && canvas.Bounds.Height > 0);

                var replacement = Arrangement();
                state.ActiveProject!.Arrangements.Add(replacement);
                state.ActiveArrangement = replacement;
                await Rebuild(view);

                IReadOnlyList<GuitarNote>? selected = null;
                view.SelectSoundsRequested += (notes, _, _) => selected = notes;
                view.SelectAtPointer(20 * canvas.RenderScale, 20 * canvas.RenderScale, false);

                Assert.NotNull(selected);
                Assert.Same(replacement.Levels[0].Notes[0], Assert.Single(selected));
                Assert.Equal(1, renderer.Calls);
            }
            finally
            {
                Canvas(view).SetResult(null);
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task EmptyChart_CancelsAndRejectsAnOlderRender(int missing, bool pageMode)
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var state = Fixture();
            var renderer = new ControlledRenderer();
            var pending = new TaskCompletionSource<SheetMusicRenderer.RenderResult?>();
            renderer.Next = () => pending.Task;
            var view = new SheetMusicView(renderer) { ProjectState = state, PageMode = pageMode };
            var window = Show(view);
            var older = Rebuild(view);
            try
            {
                var token = renderer.LastToken;
                if (missing == 0) state.ActiveArrangement = null;
                else if (missing == 1) state.ActiveLevel = null;
                else state.ActiveProject!.Beats.Clear();
                await Rebuild(view);
                Assert.True(token.IsCancellationRequested);

                var stale = Result();
                using var staleImage = stale.Chunks[0].Image;
                pending.SetResult(stale);
                await older;
                Assert.Equal(IntPtr.Zero, stale.Chunks[0].Image.Handle);
                Assert.Null(Get<SheetMusicRenderer.RenderResult?>(Canvas(view), "_result"));
            }
            finally
            {
                pending.TrySetResult(null);
                await older;
                Canvas(view).SetResult(null);
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PendingHiddenRebuild_ResumesWhenAnAncestorBecomesVisible()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new SheetMusicView(new ControlledRenderer()) { ProjectState = Fixture() };
            var host = new Border { Child = view };
            var window = Show(host);
            try
            {
                var debounce = Get<DispatcherTimer>(view, "_debounce");
                debounce.Stop();
                host.IsVisible = false;
                view.StavesMode = 2;
                Assert.True(Get<bool>(view, "_rebuildPendingWhileHidden"));
                Assert.False(debounce.IsEnabled);

                host.IsVisible = true;

                Assert.False(Get<bool>(view, "_rebuildPendingWhileHidden"));
                Assert.True(debounce.IsEnabled);
                debounce.Stop();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRefinement_ReleasesTheFirstPass(bool cancelled)
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var first = Result(100);
            using var firstImage = first.Chunks[0].Image;
            var refinement = new TaskCompletionSource<SheetMusicRenderer.RenderResult?>();
            var renderer = new ControlledRenderer();
            renderer.Next = () =>
            {
                renderer.Next = () => refinement.Task;
                return Task.FromResult<SheetMusicRenderer.RenderResult?>(first);
            };
            var view = new SheetMusicView(renderer) { ProjectState = Fixture() };
            var window = Show(view);
            var rebuild = Rebuild(view);
            try
            {
                Assert.Equal(2, renderer.Calls);
                if (cancelled) refinement.SetCanceled();
                else refinement.SetException(new InvalidOperationException("refinement failed"));
                await rebuild;

                Assert.Equal(IntPtr.Zero, first.Chunks[0].Image.Handle);
                Assert.Null(Get<SheetMusicRenderer.RenderResult?>(Canvas(view), "_result"));
            }
            finally
            {
                refinement.TrySetCanceled();
                await rebuild;
                Canvas(view).SetResult(null);
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReturningToTheDisplayedKey_RejectsADifferentInFlightRender()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var renderer = new ControlledRenderer();
            var view = new SheetMusicView(renderer) { ProjectState = Fixture(), PageMode = true };
            var window = Show(view);
            var pending = new TaskCompletionSource<SheetMusicRenderer.RenderResult?>();
            var older = Task.CompletedTask;
            try
            {
                await Rebuild(view);
                var displayed = Get<SheetMusicRenderer.RenderResult?>(Canvas(view), "_result");
                renderer.Next = () => pending.Task;
                view.StavesMode = 2;
                older = Rebuild(view);
                var token = renderer.LastToken;
                view.StavesMode = 0;
                await Rebuild(view);
                var stale = Result();
                using var staleImage = stale.Chunks[0].Image;
                pending.SetResult(stale);
                await older;

                Assert.True(token.IsCancellationRequested);
                Assert.Same(displayed, Get<SheetMusicRenderer.RenderResult?>(Canvas(view), "_result"));
                Assert.Equal(IntPtr.Zero, stale.Chunks[0].Image.Handle);
                Assert.Equal(2, renderer.Calls);
            }
            finally
            {
                pending.TrySetResult(null);
                await older;
                Canvas(view).SetResult(null);
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AnInputChange_SupersedesTheRenderInFlightAtOnce_AndItsResultIsNeverPublished()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var state = Fixture();
            var renderer = new ControlledRenderer();
            var pending = new TaskCompletionSource<SheetMusicRenderer.RenderResult?>();
            renderer.Next = () => pending.Task;
            var view = new SheetMusicView(renderer) { ProjectState = state };
            var window = Show(view);
            var older = Rebuild(view);
            try
            {
                var token = renderer.LastToken;
                var replacement = Arrangement();
                state.ActiveProject!.Arrangements.Add(replacement);
                state.ActiveArrangement = replacement;

                // At once: not when the debounce fires, which may be after A's render lands.
                Assert.True(token.IsCancellationRequested);
                Get<DispatcherTimer>(view, "_debounce").Stop();
                var stale = Result();
                using var staleImage = stale.Chunks[0].Image;
                pending.SetResult(stale);
                await older;

                Assert.Null(Get<SheetMusicRenderer.RenderResult?>(Canvas(view), "_result"));
                Assert.Equal(IntPtr.Zero, stale.Chunks[0].Image.Handle);
            }
            finally
            {
                pending.TrySetResult(null);
                await older;
                Get<DispatcherTimer>(view, "_debounce").Stop();
                Canvas(view).SetResult(null);
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PageThenAnAbandonedStripRender_KeepsThePagesLayoutWhenThePageKeyIsReused()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var renderer = new ControlledRenderer();
            var view = new SheetMusicView(renderer) { ProjectState = Fixture(), PageMode = true };
            var window = Show(view);
            var pending = new TaskCompletionSource<SheetMusicRenderer.RenderResult?>();
            var older = Task.CompletedTask;
            try
            {
                await Rebuild(view);
                var displayed = Get<SheetMusicRenderer.RenderResult?>(Canvas(view), "_result");
                Assert.True(Get<bool>(view, "_pageMode"));

                renderer.Next = () => pending.Task;
                view.PageMode = false;
                older = Rebuild(view);
                view.PageMode = true;
                await Rebuild(view);
                var stale = Result();
                using var staleImage = stale.Chunks[0].Image;
                pending.SetResult(stale);
                await older;

                // The page is still on the canvas, so the layout and the hit testing are the page's.
                Assert.Same(displayed, Get<SheetMusicRenderer.RenderResult?>(Canvas(view), "_result"));
                Assert.True(Get<bool>(view, "_pageMode"));
                Assert.True(Get<bool>(Canvas(view), "_pageMode"));
                Assert.Equal(2, renderer.Calls);
            }
            finally
            {
                pending.TrySetResult(null);
                await older;
                Canvas(view).SetResult(null);
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static Window Show(Control content)
    {
        var window = new Window { Width = 800, Height = 220, Content = content };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static ProjectState Fixture()
    {
        var project = new ChartProject();
        var arrangement = Arrangement();
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 4; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i == 0 });
        return new ProjectState { ActiveProject = project, ActiveArrangement = arrangement };
    }

    private static Arrangement Arrangement()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        for (var i = 0; i < 4; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 2, Fret = 5 });
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static SheetMusicRenderer.RenderResult Result(int height = 220)
    {
        using var bitmap = new SKBitmap(1, 1);
        var image = SKImage.FromBitmap(bitmap);
        return new SheetMusicRenderer.RenderResult(
            [new SheetMusicRenderer.SheetChunk(0, 0, 400, height, image)], 400, height,
            [new SheetMusicRenderer.MeasureBound(0, 0, 0, 400, height)],
            Enumerable.Range(0, 4).Select(i => new SheetMusicRenderer.BeatBound(0, i, i * 100, 100)).ToArray());
    }

    private static SheetCanvas Canvas(SheetMusicView view) => Assert.Single(view.GetVisualDescendants().OfType<SheetCanvas>());

    private static Task Rebuild(SheetMusicView view)
    {
        Get<DispatcherTimer>(view, "_debounce").Stop();
        var method = typeof(SheetMusicView).GetMethod("RebuildAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method.Invoke(view, null)!;
    }

    private static T Get<T>(object owner, string name)
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field.GetValue(owner)!;
    }

    private sealed class ControlledRenderer : ISheetMusicRenderer
    {
        public Func<Task<SheetMusicRenderer.RenderResult?>> Next { get; set; } =
            () => Task.FromResult<SheetMusicRenderer.RenderResult?>(Result());

        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<SheetMusicRenderer.RenderResult?> RenderAsync(string tex, double width, double displayScale, int staves,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastToken = cancellationToken;
            return Next();
        }

        public Task<SheetMusicRenderer.RenderResult?> RenderPagedAsync(string tex, double pageWidth, double displayScale, int staves,
            CancellationToken cancellationToken, SheetMusicRenderer.InkPalette palette = SheetMusicRenderer.InkPalette.Screen) =>
            RenderAsync(tex, pageWidth, displayScale, staves, cancellationToken);
    }
}
