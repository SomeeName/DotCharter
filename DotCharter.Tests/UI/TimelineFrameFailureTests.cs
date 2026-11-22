using DotCharter.UI.Controls.Timeline2D;
using DotCharter.UI.Controls;
using DotCharter.Rendering.Vulkan.Composed;

namespace DotCharter.Tests.UI;

/// <summary>Every frame callback failure must stop future frames, release its viewport
/// resources and report one error without unwinding the UI dispatcher.</summary>
public sealed class TimelineFrameFailureTests
{
    public static TheoryData<string> FrameSites =>
    [
        "atlas raster/upload/map", "pool texture/image/memory/bind", "image view/framebuffer",
        "fence wait/reset/recreation", "vertex growth/allocation/map", "vertex upload/record",
        "submit", "frame-ready notification"
    ];

    [Theory]
    [MemberData(nameof(FrameSites))]
    public void AFailureAtAnyFrameSiteIsContainedAndTearsDownOnce(string site)
    {
        var backend = new VulkanTimelineBackend(null!);
        var resources = new List<string> { "pump", "renderer", "visual", "surface" };
        var reports = new List<string>();
        var calls = 0;
        void Frame()
        {
            calls++;
            throw new InvalidOperationException(site);
        }
        void Fail(string message)
        {
            reports.Add(message);
            resources.Clear();
        }

        var escaped = Record.Exception(() => backend.ExecuteFrame(Frame, Fail));
        Assert.Null(escaped);
        Assert.True(backend.HasFailed);
        Assert.Empty(resources);
        Assert.Contains(site, Assert.Single(reports), StringComparison.Ordinal);
        backend.ExecuteFrame(Frame, Fail);
        Assert.Equal(1, calls);
        Assert.Single(reports);
    }

    [Theory]
    [InlineData("export handle")]
    [InlineData("import image")]
    [InlineData("keyed-mutex update")]
    [InlineData("present completion")]
    public void AsyncPresentFailureUsesTheSameOneShotTransition(string site)
    {
        var backend = new VulkanTimelineBackend(null!);
        var releases = 0;
        var frameCalls = 0;
        void Fail(string _) => releases++;
        backend.ReportFrameFailure(site, Fail);
        backend.ReportFrameFailure(site, Fail);
        backend.ExecuteFrame(() => frameCalls++, Fail);
        Assert.True(backend.HasFailed);
        Assert.Equal(1, releases);
        Assert.Equal(0, frameCalls);
    }

    [Fact]
    public void AThrowingFailureObserverCannotEscapeTheFrameCallback()
    {
        var backend = new VulkanTimelineBackend(null!);
        Assert.Null(Record.Exception(() => backend.ExecuteFrame(
            () => throw new InvalidOperationException("map"),
            _ => throw new InvalidOperationException("teardown"))));
        Assert.True(backend.HasFailed);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TeardownAttemptsEveryReleaseEvenWhenOneReleaseThrows(int failureAt)
    {
        var backend = new VulkanTimelineBackend(null!);
        var remaining = new HashSet<int>(Enumerable.Range(0, 4));
        var releases = Enumerable.Range(0, 4).Select(index => (Action)(() =>
        {
            remaining.Remove(index);
            if (index == failureAt) throw new InvalidOperationException("release");
        })).ToArray();
        Assert.Null(Record.Exception(() => backend.ReportFrameFailure("map",
            _ => VulkanTimelineBackend.ReleaseFrameResources(releases))));
        Assert.True(backend.HasFailed);
        Assert.Empty(remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentCompletionIsAttemptedExactlyOnceAndNeverEscapes(bool notificationThrows)
    {
        var source = new ThrowingCompletionSource();
        var errors = new List<Exception>();
        Assert.Null(Record.Exception(() => ComposedPresentPump.CompletePresent(source, 0, 1, false,
            new InvalidOperationException("import"), error =>
            {
                errors.Add(error);
                if (notificationThrows) throw new InvalidOperationException("observer");
            })));
        Assert.Equal(1, source.Completions);
        var aggregate = Assert.IsType<AggregateException>(Assert.Single(errors));
        Assert.Contains(aggregate.InnerExceptions, error => error.Message == "import");
        Assert.Contains(aggregate.InnerExceptions, error => error.Message == "completion");
    }

    [Fact]
    public void AnimationAndAsyncPresentUseTheGuardAndEveryPresentHasAFinallyCompletion()
    {
        var backend = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Timeline2D", "VulkanTimelineBackend.cs"));
        Assert.Contains("ExecuteFrame(_renderFrame, _onFailure)", backend, StringComparison.Ordinal);
        Assert.Contains("onFailure: OnPresentFailure", backend, StringComparison.Ordinal);
        Assert.Contains("ReportFrameFailure", backend, StringComparison.Ordinal);
        Assert.Contains("ReleaseFrameResources(", backend, StringComparison.Ordinal);
        Assert.Contains("() => pump?.Shutdown()", backend, StringComparison.Ordinal);
        Assert.Contains("() => renderer?.Dispose()", backend, StringComparison.Ordinal);
        Assert.Contains("() => ElementComposition.SetElementChildVisual(_owner, null)", backend, StringComparison.Ordinal);
        Assert.Contains("() => surface?.Dispose()", backend, StringComparison.Ordinal);
        var pump = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "ComposedPresentPump.cs"));
        Assert.Contains("finally", pump, StringComparison.Ordinal);
        Assert.Contains("CompletePresent(_source, index, generation, success, failure, _reportFailure)", pump, StringComparison.Ordinal);
        Assert.Contains("_onFailure(error)", pump, StringComparison.Ordinal);
    }

    private sealed class ThrowingCompletionSource : IComposedFrameSource
    {
        public int Completions { get; private set; }
        public event Action<int, long>? ComposedFrameReady { add { } remove { } }
        public (uint Width, uint Height) ComposedImageSize => (1, 1);
        public nint CreateComposedExportHandle(int index, long generation, out ulong memorySize)
        {
            memorySize = 4;
            return 0;
        }
        public void CompleteComposedPresent(int index, long generation, bool success)
        {
            Completions++;
            throw new InvalidOperationException("completion");
        }
    }
}
