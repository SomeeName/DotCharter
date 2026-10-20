using System.Diagnostics;
using System.Runtime.InteropServices;
using DotCharter.Rendering.Vulkan.Composed;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VkImage = Silk.NET.Vulkan.Image;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Encapsulates the foundational Vulkan instance, physical/logical devices,
///     queues, and fundamental memory allocation helpers. There is no
///     presentation surface and no swapchain: the context is initialized either
///     for the Avalonia-composed path (<see cref="InitializeComposed" />) or for
///     offscreen headless rendering (<see cref="InitializeDeviceHeadless" />).
///     The native child-HWND swapchain path was removed; the default-gate
///     <c>Rendering/NoNativeSwapchainTests</c> keeps it from coming back.
/// </summary>
public unsafe class VulkanContext : IDisposable
{
    private bool _isDisposed;

    public Vk Api { get; private set; } = null!;
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public Queue GraphicsQueue { get; private set; }

    public uint GraphicsQueueFamilyIndex { get; private set; }
    public string DeviceName { get; private set; } = "Unknown";
    public bool IsInitialized { get; private set; }

    /// <summary>True when the context was initialized for the Avalonia-
    /// composed output path (0.16): no surface, no swapchain extension —
    /// presentation happens by exporting D3D11-shared images to the
    /// compositor.</summary>
    public bool IsComposedPresentation { get; private set; }

    /// <summary>True when the context was initialized for OFFSCREEN headless
    /// rendering (testing): no surface, no swapchain, no D3D11 interop — the
    /// scene renders into a plain device-local image that is copied back to
    /// the CPU. See <c>HeadlessHighwayRenderer</c>.</summary>
    public bool IsHeadless { get; private set; }

    /// <summary>VK_KHR_external_memory_win32, only in composed mode (needed
    /// to import the D3D11 shared-texture memory).</summary>
    public KhrExternalMemoryWin32 KhrExternalMemoryWin32Ext { get; private set; } = null!;

    /// <summary>True when VK_KHR_win32_keyed_mutex was enabled on the device
    /// (composed mode): submits may chain
    /// <c>Win32KeyedMutexAcquireReleaseInfoKHR</c>.</summary>
    public bool HasKeyedMutex { get; private set; }

    /// <summary>The picked device's 8-byte Windows adapter LUID (composed
    /// mode; null when the driver reported none) — the composed image pool
    /// creates its D3D11 device on exactly this adapter.</summary>
    public byte[]? AdapterLuid { get; private set; }

    // Optional validation-layer messenger (DOTCHARTER_VK_VALIDATION=1):
    // counts + logs validation output so the composed/headless paths can be
    // proven clean without a debugger attached.
    private ExtDebugUtils? _debugUtilsExt;
    private DebugUtilsMessengerEXT _debugMessenger;
    // Rooted so the unmanaged callback cannot be collected.
    private static readonly DebugUtilsMessengerCallbackFunctionEXT DebugCallback = OnValidationMessage;

    /// <summary>Image usages the output images are created with (set once at
    /// init: the composed pool's exported images and the headless offscreen
    /// image both carry every usage below). Render scale needs TRANSFER_DST
    /// here — the output image becomes a blit destination.</summary>
    public ImageUsageFlags SurfaceSupportedUsage { get; private set; } = ImageUsageFlags.ColorAttachmentBit;

    /// <summary>
    ///     Sample counts the device can render to framebuffers with — the
    ///     intersection of <c>framebufferColorSampleCounts</c> and
    ///     <c>framebufferDepthSampleCounts</c> (our subpass renders color and
    ///     depth at the same rate). MSAA requests clamp against this via the
    ///     pure <see cref="MsaaPolicy" />.
    /// </summary>
    public SampleCountFlags FramebufferSampleCountMask { get; private set; }

    /// <summary>Largest 2D render-target size this device can both create an
    /// image for AND attach to a framebuffer — bounds the render-scale
    /// intermediate target (<see cref="RenderScalePolicy" />).</summary>
    public uint MaxRenderTargetWidth { get; private set; } = 1;

    public uint MaxRenderTargetHeight { get; private set; } = 1;

    /// <summary>The anisotropy texture samplers use, 0 when the device cannot filter
    /// anisotropically (<see cref="TextureFilterPolicy.Anisotropy" />).</summary>
    public float SamplerAnisotropy { get; private set; }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (!IsInitialized) return;

        Api.DeviceWaitIdle(Device);

        Api.DestroyDevice(Device, null);
        if (_debugMessenger.Handle != 0 && _debugUtilsExt != null)
            _debugUtilsExt.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);
        Api.DestroyInstance(Instance, null);
        Api.Dispose();

        IsInitialized = false;
    }

    // ---- Optional validation layer (both init paths) ---------------------

    /// <summary>Set DOTCHARTER_VK_VALIDATION=1 to run with the Khronos
    /// validation layer + a debug-utils messenger that logs and counts every
    /// warning/error (see <see cref="VulkanValidationDiagnostics" />). Off by
    /// default — zero cost in normal runs.</summary>
    private static bool ValidationRequested =>
        Environment.GetEnvironmentVariable("DOTCHARTER_VK_VALIDATION") == "1";

    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";

    private bool TryPrepareValidation(List<string> instanceExtensions, out List<string> layers)
    {
        layers = [];
        if (!ValidationRequested) return false;

        uint layerCount = 0;
        Api.EnumerateInstanceLayerProperties(ref layerCount, null);
        if (layerCount == 0) return false;
        var props = new LayerProperties[layerCount];
        fixed (LayerProperties* pProps = props)
        {
            Api.EnumerateInstanceLayerProperties(ref layerCount, pProps);
        }

        var available = false;
        for (var i = 0; i < layerCount; i++)
        {
            fixed (LayerProperties* p = &props[i])
            {
                if (Marshal.PtrToStringAnsi((nint)p->LayerName) == ValidationLayerName)
                {
                    available = true;
                    break;
                }
            }
        }

        if (!available)
        {
            Debug.WriteLine("[VULKAN] Validation requested but VK_LAYER_KHRONOS_validation is not installed.");
            return false;
        }

        layers.Add(ValidationLayerName);
        instanceExtensions.Add(ExtDebugUtils.ExtensionName);
        VulkanValidationDiagnostics.Reset(active: true);
        return true;
    }

    private void CreateDebugMessenger()
    {
        if (!Api.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils)) return;
        _debugUtilsExt = debugUtils;
        var createInfo = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                              DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                          DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                          DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback)
        };
        debugUtils.CreateDebugUtilsMessenger(Instance, in createInfo, null, out _debugMessenger);
    }

    private static uint OnValidationMessage(DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types, DebugUtilsMessengerCallbackDataEXT* data, void* userData)
    {
        var message = data != null ? Marshal.PtrToStringAnsi((nint)data->PMessage) ?? "" : "";
        VulkanValidationDiagnostics.Record(
            (severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0, message);
        return Vk.False;
    }

    private void CreateInstanceCore(List<string> extensions, List<string> layers, ApplicationInfo* appInfo)
    {
        var ptrInst = extensions.Count > 0 ? SilkMarshal.StringArrayToPtr(extensions.ToArray()) : 0;
        var ptrLayers = layers.Count > 0 ? SilkMarshal.StringArrayToPtr(layers.ToArray()) : 0;
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)ptrInst,
            EnabledLayerCount = (uint)layers.Count,
            PpEnabledLayerNames = (byte**)ptrLayers
        };

        if (Api.CreateInstance(in createInfo, null, out var instance) != Result.Success)
            throw new Exception("Failed to create Vulkan instance.");
        Instance = instance;

        if (ptrInst != 0) SilkMarshal.Free(ptrInst);
        if (ptrLayers != 0) SilkMarshal.Free(ptrLayers);
    }

    /// <summary>
    ///     Initializes for the Avalonia-composed output path (0.16): no
    ///     surface, no swapchain — the device is picked by the compositor's
    ///     adapter LUID (<paramref name="adapterLuid" />, from
    ///     <c>ICompositionGpuInterop.DeviceLuid</c>) and must support D3D11
    ///     shared-texture import (VK_KHR_external_memory_win32) plus keyed-
    ///     mutex synchronization (VK_KHR_win32_keyed_mutex). Throws when no
    ///     such device exists (the caller surfaces the failure; there is no
    ///     other on-screen path to fall back to).
    /// </summary>
    public void InitializeComposed(byte[]? adapterLuid)
    {
        if (IsInitialized) return;

        Api = Vk.GetApi();

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            ApiVersion = Vk.Version12
        };

        var instExtsList = new List<string>();
        var validationOn = TryPrepareValidation(instExtsList, out var layers);
        CreateInstanceCore(instExtsList, layers, &appInfo);
        if (validationOn) CreateDebugMessenger();

        uint deviceCount = 0;
        Api.EnumeratePhysicalDevices(Instance, ref deviceCount, null);
        if (deviceCount == 0) throw new NotSupportedException("No Vulkan physical devices found.");
        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pDevices = devices)
        {
            Api.EnumeratePhysicalDevices(Instance, ref deviceCount, pDevices);
        }

        // Reduce every device to the pure pick input: interop extensions +
        // adapter LUID (VkPhysicalDeviceIDProperties, core 1.1+).
        var candidates = new List<ComposedDeviceCandidate>((int)deviceCount);
        for (var d = 0; d < deviceCount; d++)
        {
            var hasExts = Api.IsDeviceExtensionPresent(devices[d], KhrExternalMemoryWin32.ExtensionName) &&
                          Api.IsDeviceExtensionPresent(devices[d], "VK_KHR_win32_keyed_mutex");

            var idProps = new PhysicalDeviceIDProperties { SType = StructureType.PhysicalDeviceIDProperties };
            var props2 = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &idProps
            };
            Api.GetPhysicalDeviceProperties2(devices[d], &props2);

            var luid = new byte[8];
            for (var i = 0; i < 8; i++) luid[i] = idProps.DeviceLuid[i];
            candidates.Add(new ComposedDeviceCandidate(hasExts, idProps.DeviceLuidvalid, luid));
        }

        var pick = ComposedDevicePolicy.PickDeviceIndex(candidates, adapterLuid);
        if (pick < 0)
        {
            throw new NotSupportedException(
                "No Vulkan device on the compositor's adapter supports D3D11 shared-texture interop " +
                "(VK_KHR_external_memory_win32 + VK_KHR_win32_keyed_mutex).");
        }

        PhysicalDevice = devices[pick];
        AdapterLuid = candidates[pick].LuidValid ? candidates[pick].Luid : null;
        Api.GetPhysicalDeviceProperties(PhysicalDevice, out var properties);
        DeviceName = Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName) ?? "Unknown GPU";
        FramebufferSampleCountMask = properties.Limits.FramebufferColorSampleCounts &
                                     properties.Limits.FramebufferDepthSampleCounts;
        MaxRenderTargetWidth = Math.Min(properties.Limits.MaxFramebufferWidth,
            properties.Limits.MaxImageDimension2D);
        MaxRenderTargetHeight = Math.Min(properties.Limits.MaxFramebufferHeight,
            properties.Limits.MaxImageDimension2D);

        uint queueFamilyCount = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref queueFamilyCount, null);
        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
        {
            Api.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref queueFamilyCount, pQueueFamilies);
        }

        // No present-support requirement — nothing is presented from Vulkan.
        GraphicsQueueFamilyIndex = uint.MaxValue;
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                GraphicsQueueFamilyIndex = i;
                break;
            }
        }

        if (GraphicsQueueFamilyIndex == uint.MaxValue)
            throw new NotSupportedException("No graphics queue family on the selected device.");

        var queuePriority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = GraphicsQueueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        Api.GetPhysicalDeviceFeatures(PhysicalDevice, out var supportedFeatures);
        SamplerAnisotropy = TextureFilterPolicy.Anisotropy(supportedFeatures.SamplerAnisotropy,
            properties.Limits.MaxSamplerAnisotropy);
        var deviceFeatures = new PhysicalDeviceFeatures { SamplerAnisotropy = SamplerAnisotropy >= 1f };
        // Dedicated allocation + get_memory_requirements2 are core in 1.1;
        // only the two Win32 interop extensions need enabling.
        string[] devExts = [KhrExternalMemoryWin32.ExtensionName, "VK_KHR_win32_keyed_mutex"];
        var ptrDev = SilkMarshal.StringArrayToPtr(devExts);

        var deviceCreateInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PQueueCreateInfos = &queueCreateInfo,
            QueueCreateInfoCount = 1,
            PEnabledFeatures = &deviceFeatures,
            EnabledExtensionCount = (uint)devExts.Length,
            PpEnabledExtensionNames = (byte**)ptrDev
        };

        if (Api.CreateDevice(PhysicalDevice, in deviceCreateInfo, null, out var device) != Result.Success)
            throw new Exception("Failed to create logical device (composed mode).");
        Device = device;

        SilkMarshal.Free(ptrDev);

        if (!Api.TryGetDeviceExtension(Instance, Device, out KhrExternalMemoryWin32 extMemWin32))
            throw new Exception("VK_KHR_external_memory_win32 extension not loadable.");
        KhrExternalMemoryWin32Ext = extMemWin32;
        HasKeyedMutex = true;

        Api.GetDeviceQueue(Device, GraphicsQueueFamilyIndex, 0, out var graphicsQueue);
        GraphicsQueue = graphicsQueue;

        // The exported images are created with exactly these usages, so the
        // render-scale TRANSFER_DST gate and friends behave as on a surface
        // that supports everything.
        SurfaceSupportedUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit |
                                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit;

        IsComposedPresentation = true;
        IsInitialized = true;
    }

    /// <summary>
    ///     Initializes for OFFSCREEN headless rendering (testing / golden
    ///     frames): no surface, no swapchain, no D3D11 interop. Picks the first
    ///     device that exposes a graphics queue family (no present support is
    ///     required — nothing is ever presented). The scene renders into a
    ///     plain device-local BGRA8 image that <see cref="VulkanRenderer" />
    ///     copies back to the CPU. Mutually exclusive with
    ///     <see cref="InitializeComposed" />. The
    ///     validation layer is honored exactly as on the other paths
    ///     (<c>DOTCHARTER_VK_VALIDATION=1</c>).
    /// </summary>
    public void InitializeDeviceHeadless()
    {
        if (IsInitialized) return;

        Api = Vk.GetApi();

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            ApiVersion = Vk.Version12
        };

        // No surface extensions — nothing is presented. Validation adds only
        // the debug-utils instance extension + the Khronos layer when asked.
        var instExtsList = new List<string>();
        var validationOn = TryPrepareValidation(instExtsList, out var layers);
        CreateInstanceCore(instExtsList, layers, &appInfo);
        if (validationOn) CreateDebugMessenger();

        uint deviceCount = 0;
        Api.EnumeratePhysicalDevices(Instance, ref deviceCount, null);
        if (deviceCount == 0) throw new NotSupportedException("No Vulkan physical devices found.");
        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pDevices = devices)
        {
            Api.EnumeratePhysicalDevices(Instance, ref deviceCount, pDevices);
        }

        // Pick the first device that has a graphics queue family. Without a
        // surface there is no present-support requirement, so this can never
        // fail on hardware that renders at all.
        var pickedIndex = -1;
        var pickedFamily = uint.MaxValue;
        for (var d = 0; d < deviceCount && pickedIndex < 0; d++)
        {
            uint familyCount = 0;
            Api.GetPhysicalDeviceQueueFamilyProperties(devices[d], ref familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* pFamilies = families)
            {
                Api.GetPhysicalDeviceQueueFamilyProperties(devices[d], ref familyCount, pFamilies);
            }

            for (uint i = 0; i < familyCount; i++)
            {
                if (families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                {
                    pickedIndex = d;
                    pickedFamily = i;
                    break;
                }
            }
        }

        if (pickedIndex < 0)
            throw new NotSupportedException("No Vulkan device exposes a graphics queue family.");

        PhysicalDevice = devices[pickedIndex];
        GraphicsQueueFamilyIndex = pickedFamily;
        Api.GetPhysicalDeviceProperties(PhysicalDevice, out var properties);
        DeviceName = Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName) ?? "Unknown GPU";
        FramebufferSampleCountMask = properties.Limits.FramebufferColorSampleCounts &
                                     properties.Limits.FramebufferDepthSampleCounts;
        MaxRenderTargetWidth = Math.Min(properties.Limits.MaxFramebufferWidth,
            properties.Limits.MaxImageDimension2D);
        MaxRenderTargetHeight = Math.Min(properties.Limits.MaxFramebufferHeight,
            properties.Limits.MaxImageDimension2D);

        var queuePriority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = GraphicsQueueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        Api.GetPhysicalDeviceFeatures(PhysicalDevice, out var supportedFeatures);
        SamplerAnisotropy = TextureFilterPolicy.Anisotropy(supportedFeatures.SamplerAnisotropy,
            properties.Limits.MaxSamplerAnisotropy);
        var deviceFeatures = new PhysicalDeviceFeatures { SamplerAnisotropy = SamplerAnisotropy >= 1f };
        // No device extensions at all — no swapchain, no interop.
        var deviceCreateInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PQueueCreateInfos = &queueCreateInfo,
            QueueCreateInfoCount = 1,
            PEnabledFeatures = &deviceFeatures,
            EnabledExtensionCount = 0,
            PpEnabledExtensionNames = null
        };

        if (Api.CreateDevice(PhysicalDevice, in deviceCreateInfo, null, out var device) != Result.Success)
            throw new Exception("Failed to create logical device (headless mode).");
        Device = device;

        Api.GetDeviceQueue(Device, GraphicsQueueFamilyIndex, 0, out var graphicsQueue);
        GraphicsQueue = graphicsQueue;

        // The offscreen color image is created with exactly these usages — the
        // render-scale TRANSFER_DST gate + the copy-to-buffer TRANSFER_SRC both
        // behave as on a surface that supports everything.
        SurfaceSupportedUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit |
                                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit;

        IsHeadless = true;
        IsInitialized = true;
    }

    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        Api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memProperties);
        for (var i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }

        throw new Exception("Failed to find memory type.");
    }

    public void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, out VkBuffer buffer,
        out DeviceMemory bufferMemory)
    {
        var bufferInfo = new BufferCreateInfo
        { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        VulkanCheck.Ok(Api.CreateBuffer(Device, in bufferInfo, null, out buffer), "VulkanContext.CreateBuffer");
        Api.GetBufferMemoryRequirements(Device, buffer, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, properties)
        };
        VulkanCheck.Ok(Api.AllocateMemory(Device, in allocInfo, null, out bufferMemory), "VulkanContext.AllocateMemory");
        VulkanCheck.Ok(Api.BindBufferMemory(Device, buffer, bufferMemory, 0), "VulkanContext.BindBufferMemory");
    }

    /// <summary>
    ///     Creates a 2D image and binds fresh memory. When
    ///     <paramref name="preferredProperties" /> is non-zero, a memory type
    ///     carrying those flags is preferred and <paramref name="properties" />
    ///     is the fallback — always constrained by the image's actual
    ///     <c>memoryTypeBits</c> (pure selection in
    ///     <see cref="MemoryTypePolicy.Pick" />). Used for the transient MSAA/
    ///     depth attachments: prefer LAZILY_ALLOCATED, fall back DEVICE_LOCAL.
    /// </summary>
    public void CreateImage(uint width, uint height, Format format, ImageTiling tiling, ImageUsageFlags usage,
        MemoryPropertyFlags properties, out VkImage image, out DeviceMemory imageMemory,
        SampleCountFlags samples = SampleCountFlags.Count1Bit, MemoryPropertyFlags preferredProperties = 0,
        uint mipLevels = 1)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1), MipLevels = mipLevels, ArrayLayers = 1, Format = format, Tiling = tiling,
            InitialLayout = ImageLayout.Undefined, Usage = usage, Samples = samples, SharingMode = SharingMode.Exclusive
        };
        VulkanCheck.Ok(Api.CreateImage(Device, in imageInfo, null, out image), "VulkanContext.CreateImage");
        Api.GetImageMemoryRequirements(Device, image, out var memRequirements);

        Api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memProperties);
        Span<MemoryPropertyFlags> typeFlags = stackalloc MemoryPropertyFlags[(int)memProperties.MemoryTypeCount];
        for (var i = 0; i < typeFlags.Length; i++) typeFlags[i] = memProperties.MemoryTypes[i].PropertyFlags;

        var typeIndex = MemoryTypePolicy.Pick(memRequirements.MemoryTypeBits, typeFlags,
            preferredProperties, properties);
        if (typeIndex < 0) throw new Exception("Failed to find memory type.");

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = memRequirements.Size,
            MemoryTypeIndex = (uint)typeIndex
        };
        VulkanCheck.Ok(Api.AllocateMemory(Device, in allocInfo, null, out imageMemory), "VulkanContext.AllocateMemory");
        VulkanCheck.Ok(Api.BindImageMemory(Device, image, imageMemory, 0), "VulkanContext.BindImageMemory");
    }

    public ImageView CreateImageView(VkImage image, Format format, ImageAspectFlags aspectFlags,
        uint mipLevels = 1)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D, Format = format,
            SubresourceRange = new ImageSubresourceRange
            { AspectMask = aspectFlags, BaseMipLevel = 0, LevelCount = mipLevels, BaseArrayLayer = 0, LayerCount = 1 }
        };
        VulkanCheck.Ok(Api.CreateImageView(Device, in viewInfo, null, out var imageView), "VulkanContext.CreateImageView");
        return imageView;
    }
}
