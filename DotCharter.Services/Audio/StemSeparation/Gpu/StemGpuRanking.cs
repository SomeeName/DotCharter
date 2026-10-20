namespace DotCharter.Services.Audio.StemSeparation.Gpu;

/// <summary>
///     One graphics adapter as stem separation sees it. <see cref="DeviceId" /> is the
///     DXGI adapter index — the DirectML <c>device_id</c> — and index 0 is merely the
///     adapter driving the main display, not the fastest.
/// </summary>
/// <param name="DeviceId">The DXGI adapter index, which is the DirectML <c>device_id</c>.</param>
/// <param name="Name">The driver's description, e.g. "NVIDIA GeForce RTX 3080".</param>
/// <param name="DedicatedVideoMemoryBytes">Video memory not shared with the CPU.</param>
/// <param name="IsSoftware">True for WARP, the "Microsoft Basic Render Driver".</param>
/// <param name="DrivesDisplay">True when a monitor is attached to the adapter (DXGI lists an
/// output for it). That card also runs the desktop, the app's own rendering and any game.</param>
/// <param name="Luid">The adapter's locally unique id, high part in the upper 32 bits — the
/// <c>luid_0x…_0x…</c> Windows' GPU performance counters name the card by.</param>
public sealed record StemGpuAdapter(
    int DeviceId,
    string Name,
    ulong DedicatedVideoMemoryBytes,
    bool IsSoftware,
    bool DrivesDisplay = false,
    ulong Luid = 0)
{
    /// <summary>The LUID as the GPU performance counters print it: <c>0x00000000_0x00010ee9</c>.</summary>
    public string LuidText => $"0x{(uint)(Luid >> 32):x8}_0x{(uint)Luid:x8}";
}

/// <summary>The adapter a separation runs on and why, in words fit for the log.</summary>
public sealed record StemGpuChoice(RankedStemGpu? Gpu, string Reason);

/// <summary>Why an adapter cannot run stem separation.</summary>
public enum StemGpuBlocker
{
    /// <summary>It can.</summary>
    None,

    /// <summary>Less dedicated video memory than <see cref="StemGpuRanking.MinimumVideoMemoryBytes" />.</summary>
    TooLittleVideoMemory
}

/// <summary>An adapter in ranked order, with what (if anything) keeps it from running the model.</summary>
public sealed record RankedStemGpu(StemGpuAdapter Adapter, StemGpuBlocker Blocker)
{
    public bool IsUsable => Blocker == StemGpuBlocker.None;

    /// <summary>What <c>AppConfig.StemSeparationGpuAdapter</c> stores for this adapter.</summary>
    public string Key => StemGpuRanking.KeyFor(Adapter);
}

/// <summary>
///     Pure ranking of the machine's adapters for stem separation, and the resolution of
///     a stored choice against them. No GPU is needed to test any of it: the adapter
///     list is an input.
///     <para>
///         <b>Automatic prefers a GPU that drives no display</b>, then the most dedicated
///         video memory. DirectML's <c>device_id</c> is the DXGI adapter index and index 0
///         is the card the main monitor hangs on — the one also running the desktop, this
///         app's own 3D rendering and any game. On the owner's machine that is
///         the RTX 5070 Ti; the RTX 3080 drives nothing, so a separation there competes with
/// nobody. Video memory is a proxy for a card's class (an integrated GPU
///         reports a few hundred MB), not for its speed: the two cards separated the same
///         song in 22.4 s and 23.5 s when both were idle.
///     </para>
/// </summary>
public static class StemGpuRanking
{
    /// <summary>
    ///     The least dedicated video memory an adapter needs to be offered. htdemucs
    ///     6-stem on DirectML peaked at ~2.5 GB above idle (both cards);
    ///     below 3 GB a run would fail mid-song, so such an adapter is listed
    ///     disabled with its reason rather than looking live.
    /// </summary>
    public const ulong MinimumVideoMemoryBytes = 3UL << 30;

    /// <summary>
    ///     The hardware adapters in automatic-choice order: usable ones first, then the
    ///     ones that drive no display, then by dedicated video memory (largest first) and
    ///     finally by device id. The software adapter (WARP, "Microsoft Basic Render
    ///     Driver") is dropped: DirectML on it runs at CPU speed, and listing it on every
    ///     machine would make a single-GPU machine look like a two-GPU one.
    /// </summary>
    public static IReadOnlyList<RankedStemGpu> Rank(IReadOnlyList<StemGpuAdapter> adapters) =>
    [
        .. adapters
            .Where(a => !a.IsSoftware)
            .Select(a => new RankedStemGpu(a,
                a.DedicatedVideoMemoryBytes < MinimumVideoMemoryBytes
                    ? StemGpuBlocker.TooLittleVideoMemory
                    : StemGpuBlocker.None))
            .OrderBy(r => r.IsUsable ? 0 : 1)
            .ThenBy(r => r.Adapter.DrivesDisplay ? 1 : 0)
            .ThenByDescending(r => r.Adapter.DedicatedVideoMemoryBytes)
            .ThenBy(r => r.Adapter.DeviceId)
    ];

    /// <summary>
    ///     The stored form: device id and name. The name is what identifies the card (a
    ///     display change renumbers adapters, see <see cref="Choose" />); the id only
    ///     tells two identical cards apart.
    /// </summary>
    public static string KeyFor(StemGpuAdapter adapter) => $"{adapter.DeviceId}:{adapter.Name}";

    /// <summary>The adapter to run on, or null when no adapter is usable. See <see cref="Choose" />.</summary>
    public static RankedStemGpu? Resolve(IReadOnlyList<RankedStemGpu> ranked, string? storedKey) =>
        Choose(ranked, storedKey).Gpu;

    /// <summary>
    ///     The adapter to run on and why: the stored one when it is still present and
    ///     usable (matched by id AND name, else by name alone, because a display change
    ///     renumbers adapters), otherwise the automatic choice — the first of
    ///     <see cref="Rank" />. Null when no adapter is usable.
    /// </summary>
    public static StemGpuChoice Choose(IReadOnlyList<RankedStemGpu> ranked, string? storedKey)
    {
        var usable = ranked.Where(r => r.IsUsable).ToList();
        if (usable.Count == 0)
            return new StemGpuChoice(null, $"no hardware GPU with {MinimumVideoMemoryBytes >> 30} GB of video memory");

        if (!string.IsNullOrWhiteSpace(storedKey))
        {
            var exact = usable.FirstOrDefault(r => string.Equals(r.Key, storedKey, StringComparison.Ordinal));
            if (exact != null) return new StemGpuChoice(exact, "chosen in Settings");

            var colon = storedKey.IndexOf(':', StringComparison.Ordinal);
            var name = colon >= 0 ? storedKey[(colon + 1)..] : storedKey;
            var byName = usable.FirstOrDefault(r => string.Equals(r.Adapter.Name, name, StringComparison.Ordinal));
            if (byName != null) return new StemGpuChoice(byName, "chosen in Settings (found by name; the adapter order changed)");

            return new StemGpuChoice(usable[0], $"the GPU chosen in Settings ('{name}') is missing or cannot run the model, so automatic: " +
                                                AutomaticReason(usable));
        }

        return new StemGpuChoice(usable[0], "automatic: " + AutomaticReason(usable));
    }

    private static string AutomaticReason(IReadOnlyList<RankedStemGpu> usable)
    {
        if (usable.Count == 1) return "the only usable GPU";
        var first = usable[0].Adapter;
        if (!first.DrivesDisplay && usable.Any(r => r.Adapter.DrivesDisplay))
        {
            return usable.Count(r => !r.Adapter.DrivesDisplay) == 1
                ? "the GPU that drives no display (the display GPU also runs the desktop, this app's rendering and games)"
                : "drives no display, and the most video memory of those that do not";
        }

        return first.DrivesDisplay
            ? "every GPU drives a display; the most video memory"
            : "no GPU drives a display; the most video memory";
    }

    /// <summary>Every adapter as one log line: index, name, video memory, LUID and whether it drives a display.</summary>
    public static string DescribeAdapters(IReadOnlyList<StemGpuAdapter> adapters) => adapters.Count == 0
        ? "(DXGI listed no adapter)"
        : string.Join("; ", adapters.Select(a =>
            $"#{a.DeviceId} '{a.Name}' {a.DedicatedVideoMemoryBytes / 1048576} MB luid={a.LuidText}" +
            (a.IsSoftware ? " software" : a.DrivesDisplay ? " drives a display" : " no display")));
}
