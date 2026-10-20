using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Pure MSAA sample-count decisions (0.14.2 Settings → Graphics),
///     extracted so the clamping rules are unit-testable without a Vulkan
///     device (<c>MsaaPolicyTests</c>). The offered set is 1 (off) / 2 / 4 / 8;
///     a request is clamped DOWN to the nearest offered level the device's
///     framebuffer limits support (color AND depth mask intersection —
///     the subpass renders color and depth at the same sample count).
///     1 sample is always valid, so clamping can never fail.
/// </summary>
public static class MsaaPolicy
{
    /// <summary>The levels the settings UI offers, ascending. 1 = off.</summary>
    public static readonly int[] OfferedSampleCounts = [1, 2, 4, 8];

    public const int DefaultSampleCount = 4; // the pre-0.14.2 hardcoded level

    /// <summary>
    ///     Normalizes an arbitrary request to the offered set (round down,
    ///     floor 1), then walks down until the device's framebuffer sample
    ///     mask supports it.
    /// </summary>
    public static int Clamp(int requestedSamples, SampleCountFlags supportedMask)
    {
        var target = 1;
        foreach (var offered in OfferedSampleCounts)
        {
            if (offered <= requestedSamples)
                target = offered;
        }

        while (target > 1 && (supportedMask & ToFlag(target)) == 0) target /= 2;
        return target;
    }

    public static SampleCountFlags ToFlag(int samples)
    {
        return samples switch
        {
            2 => SampleCountFlags.Count2Bit,
            4 => SampleCountFlags.Count4Bit,
            8 => SampleCountFlags.Count8Bit,
            _ => SampleCountFlags.Count1Bit
        };
    }

    /// <summary>The offered levels this device can actually render
    /// (1 is always included) — feeds the settings combo.</summary>
    public static int[] SupportedOffered(SampleCountFlags supportedMask)
    {
        var result = new List<int>(OfferedSampleCounts.Length);
        foreach (var offered in OfferedSampleCounts)
        {
            if (offered == 1 || (supportedMask & ToFlag(offered)) != 0)
                result.Add(offered);
        }

        return result.ToArray();
    }
}
