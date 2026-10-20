using DotCharter.Rendering.Timeline2D;
using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace DotCharter.Rendering.Vulkan2D;

/// <summary>
///     Per-viewport frame plumbing for the 2D Vulkan timeline: the command
///     pool + per-frame-in-flight command buffers/fences and the two
///     persistently-mapped host-visible vertex buffers (solid + general
///     streams), partitioned per frame in flight exactly like the 3D
///     renderer's. Consumes an <see cref="TimelineBatcher" /> — the
///     backend-neutral CPU geometry the 2D layers batch into — and records
///     the draw-command list in insertion order, binding pipeline + vertex
///     buffer only when the stream kind switches, so alpha-blend order is
///     exactly the layers' emission order.
///     <para>
///         Buffers GROW on demand: a frame that would overflow its slot
///         device-idles and reallocates (rare, amortized) rather than
///         dropping geometry — the 2D scene must never lose content.
///     </para>
/// </summary>
internal sealed unsafe class Timeline2DFrameResources : IDisposable
{
    public const int MaxFramesInFlight = 2;

    private const int SolidFloatsPerVertex = 6;
    private const int GeneralFloatsPerVertex = 9;
    private const ulong InitialSolidSlotBytes = 4 * 1024 * 1024;
    private const ulong InitialGeneralSlotBytes = 2 * 1024 * 1024;

    private readonly VulkanContext _context;

    private VkBuffer _solidBuffer;
    private DeviceMemory _solidMemory;
    private byte* _solidMapped;
    private ulong _solidSlotBytes;

    private VkBuffer _generalBuffer;
    private DeviceMemory _generalMemory;
    private byte* _generalMapped;
    private ulong _generalSlotBytes;

    private CommandPool _commandPool;
    private bool _disposed;

    public CommandBuffer[] CommandBuffers { get; } = new CommandBuffer[MaxFramesInFlight];
    public Fence[] Fences { get; } = new Fence[MaxFramesInFlight];

    public Timeline2DFrameResources(VulkanContext context)
    {
        _context = context;
        var api = context.Api;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = context.GraphicsQueueFamilyIndex
        };
        if (api.CreateCommandPool(context.Device, in poolInfo, null, out _commandPool) != Result.Success)
            throw new Exception("Failed to create the 2D timeline command pool.");

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MaxFramesInFlight
        };
        fixed (CommandBuffer* pBuffers = CommandBuffers)
        {
            if (api.AllocateCommandBuffers(context.Device, in allocInfo, pBuffers) != Result.Success)
                throw new Exception("Failed to allocate the 2D timeline command buffers.");
        }

        // Signaled: the first frame's fence wait must pass immediately.
        var fenceInfo = new FenceCreateInfo
        { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (api.CreateFence(context.Device, in fenceInfo, null, out Fences[i]) != Result.Success)
                throw new Exception("Failed to create a 2D timeline frame fence.");
        }

        CreateVertexBuffers(InitialSolidSlotBytes, InitialGeneralSlotBytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var api = _context.Api;
        // Caller contract: device already idle (the renderers idle before
        // tearing down); the extra idle here keeps standalone disposal safe.
        api.DeviceWaitIdle(_context.Device);
        DestroyVertexBuffers();
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (Fences[i].Handle != 0)
                api.DestroyFence(_context.Device, Fences[i], null);
        }

        if (_commandPool.Handle != 0) api.DestroyCommandPool(_context.Device, _commandPool, null);
        _commandPool = default;
    }

    /// <summary>Grows the vertex buffers when this frame's batch would
    /// overflow a slot. Device-idles on growth (rare — capacities double).</summary>
    public void EnsureCapacity(TimelineBatcher batcher)
    {
        var solidBytes = (ulong)batcher.SolidVertexCount * SolidFloatsPerVertex * sizeof(float);
        var generalBytes = (ulong)batcher.GeneralVertexCount * GeneralFloatsPerVertex * sizeof(float);
        if (solidBytes <= _solidSlotBytes && generalBytes <= _generalSlotBytes) return;

        var newSolid = _solidSlotBytes;
        while (newSolid < solidBytes) newSolid *= 2;
        var newGeneral = _generalSlotBytes;
        while (newGeneral < generalBytes) newGeneral *= 2;

        _context.Api.DeviceWaitIdle(_context.Device);
        DestroyVertexBuffers();
        CreateVertexBuffers(newSolid, newGeneral);
    }

    /// <summary>Copies the batcher's CPU float buffers into frame slot
    /// <paramref name="frameSlot" /> and returns the byte offsets the vertex
    /// buffer binds use.</summary>
    public (ulong SolidBase, ulong GeneralBase) Upload(int frameSlot, TimelineBatcher batcher)
    {
        var solidBase = (ulong)frameSlot * _solidSlotBytes;
        var generalBase = (ulong)frameSlot * _generalSlotBytes;

        var solidFloats = batcher.SolidVertexCount * SolidFloatsPerVertex;
        if (solidFloats > 0)
        {
            fixed (float* src = batcher.SolidBuffer)
            {
                System.Buffer.MemoryCopy(src, _solidMapped + solidBase,
                    _solidSlotBytes, (ulong)solidFloats * sizeof(float));
            }
        }

        var generalFloats = batcher.GeneralVertexCount * GeneralFloatsPerVertex;
        if (generalFloats > 0)
        {
            fixed (float* src = batcher.GeneralBuffer)
            {
                System.Buffer.MemoryCopy(src, _generalMapped + generalBase,
                    _generalSlotBytes, (ulong)generalFloats * sizeof(float));
            }
        }

        return (solidBase, generalBase);
    }

    /// <summary>
    ///     Records the batcher's draw-command list into <paramref name="cmd" />
    ///     in insertion order. Pipeline + vertex buffer bind only when the
    ///     stream kind switches; the push-constant resolution rides each
    ///     pipeline-layout bind. The atlas set binds with the general
    ///     pipeline (its fragment shader statically samples the font).
    /// </summary>
    public void RecordDraws(CommandBuffer cmd, Timeline2DVulkanCore core, TimelineBatcher batcher,
        ulong solidBase, ulong generalBase, float logicalWidth, float logicalHeight, DescriptorSet atlasSet)
    {
        // Flush the batcher's pending run into DrawCommands. Without it the
        // FINAL run of every frame is silently dropped (a single-stream frame
        // renders NOTHING). Must run exactly once per batched frame:
        // FinalizeFrame is not idempotent.
        batcher.FinalizeFrame();

        var api = _context.Api;
        var push = new Timeline2DVulkanCore.PushConstants
        { ResolutionX = logicalWidth, ResolutionY = logicalHeight };

        var haveKind = false;
        var currentKind = BatchKind.Solid;
        var drawCommands = batcher.DrawCommands;
        for (var i = 0; i < drawCommands.Count; i++)
        {
            var dc = drawCommands[i];
            if (!haveKind || dc.Kind != currentKind)
            {
                if (dc.Kind == BatchKind.Solid)
                {
                    api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, core.SolidPipeline);
                    api.CmdPushConstants(cmd, core.SolidPipelineLayout, ShaderStageFlags.VertexBit, 0,
                        (uint)sizeof(Timeline2DVulkanCore.PushConstants), &push);
                    var buffer = _solidBuffer;
                    var offset = solidBase;
                    api.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
                }
                else
                {
                    api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, core.GeneralPipeline);
                    api.CmdPushConstants(cmd, core.GeneralPipelineLayout, ShaderStageFlags.VertexBit, 0,
                        (uint)sizeof(Timeline2DVulkanCore.PushConstants), &push);
                    api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, core.GeneralPipelineLayout,
                        0, 1, &atlasSet, 0, null);
                    var buffer = _generalBuffer;
                    var offset = generalBase;
                    api.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
                }

                currentKind = dc.Kind;
                haveKind = true;
            }

            api.CmdDraw(cmd, (uint)dc.VertexCount, 1, (uint)dc.FirstVertex, 0);
        }
    }

    private void CreateVertexBuffers(ulong solidSlotBytes, ulong generalSlotBytes)
    {
        _solidSlotBytes = solidSlotBytes;
        _generalSlotBytes = generalSlotBytes;

        _context.CreateBuffer(solidSlotBytes * MaxFramesInFlight, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _solidBuffer, out _solidMemory);
        void* mapped;
        _context.Api.MapMemory(_context.Device, _solidMemory, 0, solidSlotBytes * MaxFramesInFlight, 0, &mapped);
        _solidMapped = (byte*)mapped;

        _context.CreateBuffer(generalSlotBytes * MaxFramesInFlight, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _generalBuffer, out _generalMemory);
        _context.Api.MapMemory(_context.Device, _generalMemory, 0, generalSlotBytes * MaxFramesInFlight, 0, &mapped);
        _generalMapped = (byte*)mapped;
    }

    private void DestroyVertexBuffers()
    {
        var api = _context.Api;
        if (_solidMapped != null)
        {
            api.UnmapMemory(_context.Device, _solidMemory);
            _solidMapped = null;
        }

        if (_solidBuffer.Handle != 0) api.DestroyBuffer(_context.Device, _solidBuffer, null);
        if (_solidMemory.Handle != 0) api.FreeMemory(_context.Device, _solidMemory, null);
        _solidBuffer = default;
        _solidMemory = default;

        if (_generalMapped != null)
        {
            api.UnmapMemory(_context.Device, _generalMemory);
            _generalMapped = null;
        }

        if (_generalBuffer.Handle != 0) api.DestroyBuffer(_context.Device, _generalBuffer, null);
        if (_generalMemory.Handle != 0) api.FreeMemory(_context.Device, _generalMemory, null);
        _generalBuffer = default;
        _generalMemory = default;
    }
}
