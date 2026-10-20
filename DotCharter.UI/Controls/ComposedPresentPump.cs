using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using DotCharter.Rendering.Diagnostics;
using DotCharter.Rendering.Vulkan.Composed;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Controls;

/// <summary>
///     UI-thread present pump for a composed-Vulkan frame source (3D highway
///     or 2D timeline — anything implementing
///     <see cref="IComposedFrameSource" />): for every submitted frame it
///     (lazily, once per pool generation) imports the exported image and
///     issues <c>UpdateWithKeyedMutexAsync(image, 1, 0)</c> on the drawing
///     surface, then returns the image to the renderer's pool when the update
///     completes. INVARIANT: every ComposedFrameReady gets exactly one
///     CompleteComposedPresent — even after shutdown, even on failure — or a
///     pool slot wedges forever. With
///     <c>publishPreview3DDiagnostics: true</c> (the 3D preview's pump),
///     present latency (submit → composition update completed) feeds the led
///     render clock and is published ~1 Hz to
///     <c>RenderDiagnostics.Preview3DPresentLatency</c>; the 2D timeline pump
///     leaves those 3D channels untouched.
/// </summary>
internal sealed class ComposedPresentPump
{
    private readonly IComposedFrameSource _source;
    private readonly ICompositionGpuInterop _interop;
    private readonly CompositionDrawingSurface _surface;
    private readonly bool _publishPreview3DDiagnostics;

    private readonly ImportEntry?[] _imports = new ImportEntry?[ComposedImagePoolPolicy.ImageCount];

    // One queued frame per ComposedFrameReady, presented FIFO by ONE cached
    // dispatcher delegate. Posting must not capture (index, generation,
    // timestamp) in a closure — a display class, an Action and the async
    // state-machine box per frame, at up to 144 Hz on two surfaces.
    // A struct queue and a cached Action leave
    // only the async box, which UpdateWithKeyedMutexAsync's genuine
    // suspension needs. The queue keeps every frame's own record, so a
    // resize racing the post can never make two posts present one frame.
    private readonly ConcurrentQueue<PendingFrame> _pending = new();
    private readonly Action _presentNext;

    private readonly record struct PendingFrame(int Index, long Generation, long SubmitTimestamp);
    private readonly FrameTimeRing _latency = new();
    // Present-INTERVAL jitter (owner metric): time between consecutive
    // present completions. A constant latency is compensated by the led
    // render clock (VisualLeadTiming); interval jitter is what the
    // player would actually feel, so it is published first-class.
    private readonly FrameTimeRing _presentIntervals = new();
    private long _lastPresentCompleteTs;
    private long _lastLatencyPublish = Stopwatch.GetTimestamp();
    private volatile bool _shutdown;
    private bool _loggedPresentFailure;

    private sealed class ImportEntry
    {
        public required long Generation { get; init; }
        public required ICompositionImportedGpuImage Image { get; init; }
    }

    public ComposedPresentPump(IComposedFrameSource source, ICompositionGpuInterop interop,
        CompositionDrawingSurface surface, bool publishPreview3DDiagnostics)
    {
        _source = source;
        _interop = interop;
        _surface = surface;
        _publishPreview3DDiagnostics = publishPreview3DDiagnostics;
        _presentNext = PresentNext;
        _source.ComposedFrameReady += OnFrameReady;
    }

    /// <summary>Stops accepting new frames. In-flight presents still complete
    /// back to the pool; already-imported images are disposed lazily as their
    /// entries are superseded or the pump is collected.</summary>
    public void Shutdown()
    {
        _shutdown = true;
        _source.ComposedFrameReady -= OnFrameReady;
        for (var i = 0; i < _imports.Length; i++)
        {
            var entry = _imports[i];
            _imports[i] = null;
            if (entry != null) _ = entry.Image.DisposeAsync();
        }
    }

    private void OnFrameReady(int index, long generation)
    {
        // Source thread → UI thread. Render priority so presents keep flowing
        // while the UI is busy; posts run FIFO, preserving submission order.
        // (The 2D timeline already raises on the UI thread — the post still
        // decouples the present from the raising frame.)
        _pending.Enqueue(new PendingFrame(index, generation, Stopwatch.GetTimestamp()));
        Dispatcher.UIThread.Post(_presentNext, DispatcherPriority.Render);
    }

    /// <summary>One post, one frame: the head of the queue, in submission order.</summary>
    private void PresentNext()
    {
        if (_pending.TryDequeue(out var frame))
            Present(frame.Index, frame.Generation, frame.SubmitTimestamp);
    }

    private async void Present(int index, long generation, long submitTimestamp)
    {
        if (_shutdown)
        {
            // The surface is (about to be) dead — fail the present so the
            // pool marks the image broken and the next attach rebuilds it.
            _source.CompleteComposedPresent(index, generation, false);
            return;
        }

        try
        {
            var entry = _imports[index];
            if (entry == null || entry.Generation != generation)
            {
                if (entry != null) _ = entry.Image.DisposeAsync();
                _imports[index] = null;

                var handle = _source.CreateComposedExportHandle(index, generation, out var memorySize);
                if (handle == 0)
                {
                    // Pool moved on (resize raced the post) — nothing to show.
                    _source.CompleteComposedPresent(index, generation, false);
                    return;
                }

                var (width, height) = _source.ComposedImageSize;
                var imported = _interop.ImportImage(
                    new PlatformHandle(handle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle),
                    new PlatformGraphicsExternalImageProperties
                    {
                        Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                        Width = (int)width,
                        Height = (int)height,
                        MemorySize = memorySize
                    });
                entry = new ImportEntry { Generation = generation, Image = imported };
                _imports[index] = entry;
            }

            // Vulkan released the keyed mutex to key 1 on the frame submit;
            // the compositor acquires 1 and releases back to 0 when done —
            // which is what the completed task proves.
            await _surface.UpdateWithKeyedMutexAsync(entry.Image, 1, 0);

            _source.CompleteComposedPresent(index, generation, true);

            if (!_publishPreview3DDiagnostics) return;

            // Present-latency proxy: frame-submit → composition-update
            // completed. Feeds the led render clock's EMA estimate; ~1 Hz
            // publication, mirroring the render loop's HUD cadence.
            var latencyMs = Stopwatch.GetElapsedTime(submitTimestamp).TotalMilliseconds;
            _latency.Add(latencyMs);
            VisualLeadTiming.PublishMeasuredPresentLatency(latencyMs);

            var now = Stopwatch.GetTimestamp();
            if (_lastPresentCompleteTs != 0)
                _presentIntervals.Add(Stopwatch.GetElapsedTime(_lastPresentCompleteTs, now).TotalMilliseconds);
            _lastPresentCompleteTs = now;

            if (Stopwatch.GetElapsedTime(_lastLatencyPublish, now).TotalSeconds >= 1)
            {
                _lastLatencyPublish = now;
                Diagnostics.RenderDiagnostics.PublishPreview3DPresentLatency(_latency.Summarize());
                Diagnostics.RenderDiagnostics.PublishPreview3DPresentInterval(
                    _presentIntervals.Summarize(), _presentIntervals.StdDevMs());
            }
        }
        catch (Exception ex)
        {
            _source.CompleteComposedPresent(index, generation, false);
            if (!_loggedPresentFailure)
            {
                _loggedPresentFailure = true;
                App.Services?.GetService<IAppLogger>()?.Warning(AppLogCategory.Rendering,
                    $"Composed present failed (pool will rebuild): {ex.Message}");
            }
        }
    }
}
