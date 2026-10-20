namespace DotCharter.Rendering.Vulkan.Composed;

/// <summary>
///     One enumerated physical device, reduced to what the composed-output
///     device pick needs. <see cref="Luid" /> is the 8-byte Windows adapter
///     LUID from <c>VkPhysicalDeviceIDProperties.deviceLUID</c>, only
///     meaningful while <see cref="LuidValid" />.
/// </summary>
public readonly record struct ComposedDeviceCandidate(bool HasRequiredExtensions, bool LuidValid, byte[] Luid);

/// <summary>
///     Pure device-selection rule for the Avalonia-composed presentation path
///     (0.16): the exported images are D3D11 shared textures, so the Vulkan
///     device MUST be the same physical adapter Avalonia's D3D11 compositor
///     runs on — cross-adapter external memory import is invalid. No Vulkan
///     calls — unit-tested in <c>ComposedDevicePolicyTests</c>.
/// </summary>
public static class ComposedDevicePolicy
{
    /// <summary>
    ///     Picks the device index: the first candidate that has the required
    ///     interop extensions AND matches the compositor's adapter LUID.
    ///     Without a target LUID (compositor did not report one) the first
    ///     extension-capable device wins — the historical devices[0] behaviour,
    ///     constrained to devices that can actually export. -1 = no usable
    ///     device (the composed path must fail init, never render cross-GPU).
    /// </summary>
    public static int PickDeviceIndex(IReadOnlyList<ComposedDeviceCandidate> candidates, byte[]? targetLuid)
    {
        if (targetLuid is { Length: 8 })
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.HasRequiredExtensions && c.LuidValid && LuidEquals(c.Luid, targetLuid))
                    return i;
            }

            // A reported LUID that matches no exportable device is a hard
            // mismatch (e.g. the compositor sits on an iGPU we can't see):
            // do NOT fall back to a different adapter — the import would fail
            // or silently run cross-GPU.
            return -1;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].HasRequiredExtensions)
                return i;
        }

        return -1;
    }

    private static bool LuidEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }
}
