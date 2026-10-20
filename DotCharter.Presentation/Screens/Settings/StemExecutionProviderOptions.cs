using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Audio.StemSeparation.Gpu;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>One picker entry. <see cref="Value" /> is the name <c>AppConfig</c> stores.</summary>
public sealed record StemExecutionProviderOption(string Value, string Label, bool IsAvailable, string UnavailableReason)
    : IStemExecutionProviderOption;

/// <summary>One GPU picker entry. <see cref="Key" /> is what <c>AppConfig.StemSeparationGpuAdapter</c> stores.</summary>
public sealed record StemGpuOption(string Key, string Label, bool IsAvailable, string UnavailableReason) : IStemGpuOption;

// Settings ▸ Stem models pickers; the rules are in docs/agents/services/stem-separation.md.
public static class StemExecutionProviderOptions
{
    public const string DirectMl = "DirectML";

    public const string Cpu = "cpu";

    public static IReadOnlyList<StemExecutionProviderOption> For(
        ILocalizationService localization, StemExecutionReason? directMlBlocker) =>
    [
        new(DirectMl, "DirectML", directMlBlocker == null, directMlBlocker switch
        {
            null => "",
            StemExecutionReason.NoDirectMlInRuntime => localization["Settings_StemModels_DirectMlMissing"],
            _ => localization.Format("Settings_StemModels_DirectMlUnavailable",
                $"{StemGpuRanking.MinimumVideoMemoryBytes >> 30} GB")
        }),
        new(Cpu, "CPU", IsAvailable: true, "")
    ];

    public static string Normalize(string? stored, bool directMlAvailable) =>
        string.Equals(stored?.Trim(), Cpu, StringComparison.OrdinalIgnoreCase) || !directMlAvailable ? Cpu : DirectMl;

    public static IReadOnlyList<StemGpuOption> GpuOptions(
        ILocalizationService localization, IReadOnlyList<RankedStemGpu> ranked)
    {
        var automatic = StemGpuRanking.Resolve(ranked, null);
        return
        [
            new StemGpuOption(
                AutomaticGpuKey,
                localization.Format("Settings_StemModels_GpuAuto", automatic == null ? "—" : Label(automatic.Adapter)),
                automatic != null,
                ""),
            .. ranked.Select(r => new StemGpuOption(
                r.Key,
                Label(r.Adapter),
                r.IsUsable,
                r.IsUsable
                    ? ""
                    : localization.Format("Settings_StemModels_GpuTooSmallFmt",
                        GigaBytes(StemGpuRanking.MinimumVideoMemoryBytes),
                        GigaBytes(r.Adapter.DedicatedVideoMemoryBytes))))
        ];
    }

    public const string AutomaticGpuKey = "";

    private static string Label(StemGpuAdapter adapter) => $"{adapter.Name} · {GigaBytes(adapter.DedicatedVideoMemoryBytes)}";

    private static string GigaBytes(ulong bytes) => bytes >= 1UL << 30
        ? $"{Math.Round(bytes / (double)(1UL << 30)):0} GB"
        : $"{bytes / (double)(1UL << 20):0} MB";
}
