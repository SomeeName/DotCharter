using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace DotCharter.Rendering.Vulkan.Composed;

/// <summary>What <see cref="ComposedDeviceProbe" /> found.</summary>
public enum ComposedDeviceSupport
{
    /// <summary>A device the composed path can open exists.</summary>
    Supported,

    /// <summary>No Vulkan loader or driver: <c>vulkan-1.dll</c> is missing or no instance could be created.</summary>
    NoVulkan,

    /// <summary>Vulkan loads but enumerates no physical device.</summary>
    NoDevice,

    /// <summary>Devices exist, but none has a graphics queue AND the D3D11 shared-texture interop extensions.</summary>
    NoInteropDevice
}

/// <summary>The probe's answer: the verdict, the device the composed path would pick, and a log line.</summary>
public readonly record struct ComposedDeviceProbeResult(ComposedDeviceSupport Support, string DeviceName, string Detail);

/// <summary>
///     Asks, WITHOUT creating a device, whether <see cref="VulkanContext.InitializeComposed" />
///     could succeed on this machine: an instance, the physical devices, and the same
///     pick <see cref="ComposedDevicePolicy" /> makes (interop extensions + a graphics
///     queue family). The compositor's adapter LUID is not known before a window
///     composes, so the pick runs without one — a machine that passes here can still
///     fail the LUID match later, and says so where it always did.
///     <para>
///         Blocking (a driver call): call it off the UI thread. The instance is
///         destroyed before it returns.
///     </para>
/// </summary>
public static unsafe class ComposedDeviceProbe
{
    private const string KeyedMutexExtension = "VK_KHR_win32_keyed_mutex";

    public static ComposedDeviceProbeResult Run()
    {
        Vk api;
        try
        {
            api = Vk.GetApi();
        }
        catch (Exception ex)
        {
            return new ComposedDeviceProbeResult(ComposedDeviceSupport.NoVulkan, string.Empty, ex.Message);
        }

        var instance = default(Instance);
        try
        {
            var appInfo = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version12 };
            var createInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &appInfo };
            var created = api.CreateInstance(in createInfo, null, out instance);
            if (created != Result.Success)
                return new ComposedDeviceProbeResult(ComposedDeviceSupport.NoVulkan, string.Empty, $"vkCreateInstance returned {created}");

            return Classify(Describe(api, instance));
        }
        catch (Exception ex)
        {
            return new ComposedDeviceProbeResult(ComposedDeviceSupport.NoVulkan, string.Empty, ex.Message);
        }
        finally
        {
            if (instance.Handle != 0) api.DestroyInstance(instance, null);
            api.Dispose();
        }
    }

    /// <summary>The pure half: one candidate and one name per enumerated device, in enumeration order.</summary>
    public static ComposedDeviceProbeResult Classify(IReadOnlyList<(ComposedDeviceCandidate Candidate, string Name)> devices)
    {
        if (devices.Count == 0)
            return new ComposedDeviceProbeResult(ComposedDeviceSupport.NoDevice, string.Empty, "no Vulkan physical devices");

        var pick = ComposedDevicePolicy.PickDeviceIndex([.. devices.Select(d => d.Candidate)], targetLuid: null);
        var names = string.Join(", ", devices.Select(d => d.Name));
        return pick < 0
            ? new ComposedDeviceProbeResult(ComposedDeviceSupport.NoInteropDevice, string.Empty,
                $"no device with a graphics queue and {KhrExternalMemoryWin32.ExtensionName} + {KeyedMutexExtension} among: {names}")
            : new ComposedDeviceProbeResult(ComposedDeviceSupport.Supported, devices[pick].Name, $"would pick {devices[pick].Name} of: {names}");
    }

    private static List<(ComposedDeviceCandidate, string)> Describe(Vk api, Instance instance)
    {
        uint count = 0;
        api.EnumeratePhysicalDevices(instance, ref count, null);
        var devices = new PhysicalDevice[count];
        if (count > 0)
        {
            fixed (PhysicalDevice* pDevices = devices)
                api.EnumeratePhysicalDevices(instance, ref count, pDevices);
        }

        var described = new List<(ComposedDeviceCandidate, string)>((int)count);
        foreach (var device in devices)
        {
            var usable = HasGraphicsQueue(api, device)
                         && api.IsDeviceExtensionPresent(device, KhrExternalMemoryWin32.ExtensionName)
                         && api.IsDeviceExtensionPresent(device, KeyedMutexExtension);
            api.GetPhysicalDeviceProperties(device, out var properties);
            var name = Marshal.PtrToStringAnsi((nint)properties.DeviceName) ?? "Unknown GPU";
            described.Add((new ComposedDeviceCandidate(usable, false, new byte[8]), name));
        }

        return described;
    }

    private static bool HasGraphicsQueue(Vk api, PhysicalDevice device)
    {
        uint count = 0;
        api.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        var families = new QueueFamilyProperties[count];
        if (count > 0)
        {
            fixed (QueueFamilyProperties* pFamilies = families)
                api.GetPhysicalDeviceQueueFamilyProperties(device, ref count, pFamilies);
        }

        return families.Any(f => f.QueueFlags.HasFlag(QueueFlags.GraphicsBit));
    }
}
