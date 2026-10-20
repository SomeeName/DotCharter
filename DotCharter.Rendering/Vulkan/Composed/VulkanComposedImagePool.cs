using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Vulkan;
using VkImage = Silk.NET.Vulkan.Image;
using DxgiFormat = Silk.NET.DXGI.Format;
using VkFormat = Silk.NET.Vulkan.Format;

namespace DotCharter.Rendering.Vulkan.Composed;

/// <summary>
///     The exportable image pool behind the Avalonia-composed 3D output path
///     (0.16). Each of the <see cref="ComposedImagePoolPolicy.ImageCount" />
///     images is a D3D11 shared texture (BGRA8, keyed mutex) created on a
///     D3D11 device on the SAME adapter as the Vulkan device, imported into
///     Vulkan memory (VK_KHR_external_memory_win32, dedicated allocation) and
///     bound to a <see cref="VkImage" /> the scene renders into. Avalonia's
///     D3D11 compositor opens its own NT handle per image
///     (<see cref="CreateExportHandle" />) and synchronizes via the keyed
///     mutex: Vulkan acquires key 0 / releases key 1 on the frame submit, the
///     compositor acquires 1 / releases 0
///     (<c>CompositionDrawingSurface.UpdateWithKeyedMutexAsync(img, 1, 0)</c>).
///
///     Threading: <see cref="Recreate" />/<see cref="Dispose" /> run on the
///     render thread under the renderer lock with an idle device; state
///     transitions and <see cref="CreateExportHandle" /> may race from the UI
///     thread and are guarded by <see cref="_stateLock" />.
/// </summary>
public sealed unsafe class VulkanComposedImagePool : IDisposable
{
    private readonly VulkanContext _context;
    private readonly object _stateLock = new();

    private D3D11? _d3d11Api;
    private DXGI? _dxgiApi;
    private ComPtr<ID3D11Device> _d3dDevice;

    private Slot[] _slots = [];
    private int _lastAcquired = -1;

    private struct Slot
    {
        public ComPtr<ID3D11Texture2D> Texture;
        public VkImage Image;
        public DeviceMemory Memory;
        public ulong MemorySize;
        public ComposedImageState State;
    }

    public VulkanComposedImagePool(VulkanContext context) => _context = context;

    /// <summary>Bumped on every <see cref="Recreate" /> so late present
    /// completions for images of a dead pool are ignored.</summary>
    public long Generation { get; private set; }

    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public int Count => _slots.Length;

    /// <summary>The Vulkan images, index-aligned with the pool slots — the
    /// swapchain manager builds views/framebuffers over these exactly like
    /// swapchain images.</summary>
    public VkImage[] Images
    {
        get
        {
            var images = new VkImage[_slots.Length];
            for (var i = 0; i < _slots.Length; i++) images[i] = _slots[i].Image;
            return images;
        }
    }

    /// <summary>True when any image's present faulted — the renderer flags a
    /// deferred recreation (the keyed-mutex state of a faulted image is
    /// unknowable, the pool never heals in place).</summary>
    public bool HasBroken
    {
        get
        {
            lock (_stateLock)
            {
                for (var i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].State == ComposedImageState.Broken)
                        return true;
                }

                return false;
            }
        }
    }

    /// <summary>The imported device memory backing image
    /// <paramref name="index" /> — the keyed-mutex sync object for the
    /// frame submit's acquire/release info.</summary>
    public DeviceMemory GetDeviceMemory(uint index) => _slots[index].Memory;

    /// <summary>
    ///     (Re)creates the D3D device (first call) and all pool images at the
    ///     given pixel size. Caller contract: render thread, device idle, no
    ///     frame in flight, and every view/framebuffer over the old images
    ///     already destroyed (VulkanSwapchainManager.RecreateComposed does
    ///     this). Pending compositor reads of the OLD images stay safe — the
    ///     D3D textures are COM-refcounted and the compositor holds its own
    ///     reference through its imported handle; we only drop ours.
    /// </summary>
    public void Recreate(uint width, uint height)
    {
        DestroySlots();
        if (width == 0 || height == 0) return;

        EnsureD3DDevice();

        // ALL-OR-NOTHING. CreateSlot throws on a failed image create, a failed
        // shared-handle export or a failed memory import; DestroySlots() above
        // already emptied _slots, so a throw part-way would strand every slot
        // built so far — a live VkImage + an imported DeviceMemory + a D3D11
        // texture COM ref, in a local nothing else could reach. The driver
        // retries this up to 120 times, so one sustained VRAM shortage would leak a
        // batch per retry.
        var slots = NativeResourceSet.BuildAllOrRollback(
            ComposedImagePoolPolicy.ImageCount, _ => CreateSlot(width, height), DestroySlot);

        lock (_stateLock)
        {
            _slots = slots;
            _lastAcquired = -1;
            Generation++;
            Width = width;
            Height = height;
        }
    }

    /// <summary>Free → Rendering. False when the compositor still owns every
    /// image (natural back-pressure: the render loop skips and retries).</summary>
    public bool TryAcquire(out uint index)
    {
        lock (_stateLock)
        {
            Span<ComposedImageState> states = stackalloc ComposedImageState[_slots.Length];
            for (var i = 0; i < _slots.Length; i++) states[i] = _slots[i].State;

            var pick = ComposedImagePoolPolicy.PickNext(states, _lastAcquired);
            if (pick < 0)
            {
                index = 0;
                return false;
            }

            _slots[pick].State = ComposedImageState.Rendering;
            _lastAcquired = pick;
            index = (uint)pick;
            return true;
        }
    }

    /// <summary>Rendering → AwaitingPresent (the frame submit carrying the
    /// keyed-mutex release is on the queue; the compositor may acquire).</summary>
    public void MarkSubmitted(uint index)
    {
        lock (_stateLock)
        {
            if (_slots[index].State == ComposedImageState.Rendering)
                _slots[index].State = ComposedImageState.AwaitingPresent;
        }
    }

    /// <summary>
    ///     Poisons one image so <see cref="HasBroken" /> makes the renderer
    ///     rebuild the whole pool on the deferred path. Used when a SUBMIT
    ///     against the image fails (a keyed-mutex acquire timeout, a lost
    ///     device): the mutex state is then unknowable, and leaving the slot in
    ///     <c>Rendering</c> wedges it for the life of the pool — it is never
    ///     Free again and never gets its <c>CompleteComposedPresent</c>.
    /// </summary>
    public void MarkBroken(uint index)
    {
        lock (_stateLock)
        {
            if (index < _slots.Length) _slots[index].State = ComposedImageState.Broken;
        }
    }

    /// <summary>Called (UI-thread continuation) when the composition update
    /// for image <paramref name="index" /> completed. A stale generation is
    /// ignored; a fault marks the image broken so the renderer rebuilds.</summary>
    public void CompletePresent(uint index, long generation, bool success)
    {
        lock (_stateLock)
        {
            if (generation != Generation || index >= _slots.Length) return;
            if (_slots[index].State != ComposedImageState.AwaitingPresent) return;
            _slots[index].State = success ? ComposedImageState.Free : ComposedImageState.Broken;
        }
    }

    /// <summary>
    ///     Creates a fresh DXGI shared NT handle for image
    ///     <paramref name="index" /> for the compositor import
    ///     (<c>ICompositionGpuInterop.ImportImage</c> takes ownership of the
    ///     handle). UI thread; returns 0 when the pool was rebuilt since the
    ///     caller looked (check <paramref name="generation" /> first).
    /// </summary>
    public nint CreateExportHandle(int index, long generation, out ulong memorySize)
    {
        lock (_stateLock)
        {
            memorySize = 0;
            if (generation != Generation || index < 0 || index >= _slots.Length) return 0;
            var slot = _slots[index];
            memorySize = slot.MemorySize;
            return CreateSharedNtHandle(slot.Texture);
        }
    }

    public void Dispose()
    {
        DestroySlots();
        if (_d3dDevice.Handle != null)
        {
            _d3dDevice.Dispose();
            _d3dDevice = default;
        }

        _d3d11Api?.Dispose();
        _dxgiApi?.Dispose();
        _d3d11Api = null;
        _dxgiApi = null;
    }

    // ---- internals -------------------------------------------------------

    private void EnsureD3DDevice()
    {
        if (_d3dDevice.Handle != null) return;

        _dxgiApi ??= new DXGI(DXGI.CreateDefaultContext(["dxgi.dll"]));
        _d3d11Api ??= new D3D11(D3D11.CreateDefaultContext(["d3d11.dll"]));

        using var factory = _dxgiApi.CreateDXGIFactory1<IDXGIFactory1>();
        using var adapter = FindAdapter(factory);

        var featureLevels = stackalloc D3DFeatureLevel[4]
        {
            D3DFeatureLevel.Level111,
            D3DFeatureLevel.Level110,
            D3DFeatureLevel.Level101,
            D3DFeatureLevel.Level100
        };

        ComPtr<ID3D11Device> device = default;
        D3DFeatureLevel actual;
        SilkMarshal.ThrowHResult(_d3d11Api.CreateDevice(
            adapter.Handle,
            adapter.Handle != null ? D3DDriverType.Unknown : D3DDriverType.Hardware,
            IntPtr.Zero,
            0u,
            featureLevels,
            4u,
            D3D11.SdkVersion,
            device.GetAddressOf(),
            &actual,
            (ID3D11DeviceContext**)null));

        _d3dDevice = device;
    }

    /// <summary>The DXGI adapter with the Vulkan device's LUID — the import
    /// is only valid same-adapter. Null adapter (first hardware device) only
    /// when the context reported no LUID at all.</summary>
    private ComPtr<IDXGIAdapter> FindAdapter(ComPtr<IDXGIFactory1> factory)
    {
        const int dxgiErrorNotFound = unchecked((int)0x887A0002);
        var targetLuid = _context.AdapterLuid;
        if (targetLuid is not { Length: 8 }) return default;

        var index = 0u;
        ComPtr<IDXGIAdapter> adapter = default;
        // Hoisted out of the loop: the buffer is loop-invariant (8-byte LUID) and
        // MemoryMarshal.Write fully overwrites it each iteration, so reusing one
        // stack allocation avoids accumulating stack per enumerated adapter (CA2014).
        Span<byte> luid = stackalloc byte[8];
        while (true)
        {
            // Loop on SUCCESS, not on "not the end-of-list code". Terminating
            // only on DXGI_ERROR_NOT_FOUND would let any OTHER failing
            // HRESULT (a device removed mid-enumeration, E_OUTOFMEMORY) leave
            // `adapter` at its null default — and the very next line would call
            // GetDesc on it, then increment `index` and go round again,
            // without bound.
            var hr = factory.EnumAdapters(index, adapter.GetAddressOf());
            if (hr == dxgiErrorNotFound) break;
            if (hr != 0)
            {
                throw new NotSupportedException(
                    $"DXGI EnumAdapters failed (HRESULT 0x{hr:X8}) at index {index} while looking " +
                    "for the adapter matching the Vulkan device LUID.");
            }

            AdapterDesc desc;
            if (adapter.GetDesc(&desc) == 0)
            {
                MemoryMarshal.Write(luid, in desc.AdapterLuid);
                if (luid.SequenceEqual(targetLuid)) return adapter;
            }

            adapter.Dispose();
            adapter = default;
            ++index;
        }

        throw new NotSupportedException("No DXGI adapter matches the Vulkan device LUID.");
    }

    private Slot CreateSlot(uint width, uint height)
    {
        // BGRA8 to match every existing pass/pipeline format
        // (Format.B8G8R8A8Unorm is baked into the scene + composite passes).
        // RenderTarget so D3D accepts it as an interop render target,
        // ShaderResource so the compositor can sample it; keyed mutex +
        // NT-handle sharing per the Avalonia GpuInterop contract.
        var desc = new Texture2DDesc
        {
            Format = DxgiFormat.FormatB8G8R8A8Unorm,
            Width = width,
            Height = height,
            ArraySize = 1,
            MipLevels = 1,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
            CPUAccessFlags = 0,
            MiscFlags = (uint)(ResourceMiscFlag.SharedKeyedmutex | ResourceMiscFlag.SharedNthandle)
        };

        ComPtr<ID3D11Texture2D> texture = default;
        SilkMarshal.ThrowHResult(_d3dDevice.CreateTexture2D(&desc, (SubresourceData*)null, texture.GetAddressOf()));

        // Vulkan side: an image whose memory IS the D3D texture. Usage covers
        // every consumer across output modes: scene color attachment (Direct),
        // blit destination (Blit), composite target (Sampled) — and
        // TransferSrc/Sampled for potential readback/debug.
        var externalInfo = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.D3D11TextureBit
        };

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            PNext = &externalInfo,
            ImageType = ImageType.Type2D,
            Format = VkFormat.B8G8R8A8Unorm,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                    ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        if (_context.Api.CreateImage(_context.Device, in imageInfo, null, out var image) != Result.Success)
        {
            texture.Dispose();
            throw new Exception("Failed to create composed VkImage.");
        }

        _context.Api.GetImageMemoryRequirements(_context.Device, image, out var memReq);

        // Import the D3D texture's allocation. Dedicated allocation is
        // REQUIRED for D3D11 texture handles; the NT handle we import stays
        // ours to close (import never takes ownership of NT handles).
        var importHandle = CreateSharedNtHandle(texture);
        if (importHandle == 0)
        {
            _context.Api.DestroyImage(_context.Device, image, null);
            texture.Dispose();
            throw new Exception("Failed to create DXGI shared handle for import.");
        }

        var dedicated = new MemoryDedicatedAllocateInfo
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Image = image
        };
        var import = new ImportMemoryWin32HandleInfoKHR
        {
            SType = StructureType.ImportMemoryWin32HandleInfoKhr,
            PNext = &dedicated,
            HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
            Handle = importHandle
        };
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &import,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = _context.FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        var allocResult = _context.Api.AllocateMemory(_context.Device, in alloc, null, out var memory);
        CloseHandle(importHandle);
        if (allocResult != Result.Success)
        {
            _context.Api.DestroyImage(_context.Device, image, null);
            texture.Dispose();
            throw new Exception($"Failed to import D3D11 texture memory ({allocResult}).");
        }

        VulkanCheck.Ok(_context.Api.BindImageMemory(_context.Device, image, memory, 0), "VulkanComposedImagePool.BindImageMemory");

        return new Slot
        {
            Texture = texture,
            Image = image,
            Memory = memory,
            MemorySize = memReq.Size,
            State = ComposedImageState.Free
        };
    }

    private static nint CreateSharedNtHandle(ComPtr<ID3D11Texture2D> texture)
    {
        using var resource = texture.QueryInterface<IDXGIResource1>();
        nint handle = 0;
        var hr = resource.CreateSharedHandle((SecurityAttributes*)null,
            DXGI.SharedResourceRead | DXGI.SharedResourceWrite, (char*)null, (void**)&handle);
        if (hr != 0)
        {
            Debug.WriteLine($"[VULKAN] CreateSharedHandle failed: 0x{hr:X8}");
            return 0;
        }

        return handle;
    }

    private void DestroySlots()
    {
        Slot[] slots;
        lock (_stateLock)
        {
            slots = _slots;
            _slots = [];
        }

        foreach (var slot in slots) DestroySlot(slot);
    }

    /// <summary>
    ///     Releases one slot's three native objects. Shared with the
    ///     <see cref="NativeResourceSet.BuildAllOrRollback{T}" /> rollback path, so
    ///     a half-built pool is torn down by exactly the same code that tears down
    ///     a live one.
    /// </summary>
    private void DestroySlot(Slot slot)
    {
        if (slot.Image.Handle != 0) _context.Api.DestroyImage(_context.Device, slot.Image, null);
        if (slot.Memory.Handle != 0) _context.Api.FreeMemory(_context.Device, slot.Memory, null);
        if (slot.Texture.Handle != null) slot.Texture.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
