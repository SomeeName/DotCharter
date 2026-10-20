using System.Diagnostics;
using System.Globalization;

namespace DotCharter.Tools.RigDataset;

/// <summary>One GPU as <c>nvidia-smi</c> reports it.</summary>
/// <param name="Index">The <c>nvidia-smi</c> index. <b>Not</b> the CUDA ordinal:
/// CUDA's default <c>CUDA_DEVICE_ORDER</c> is <c>FASTEST_FIRST</c>, so the two
/// disagree the moment a box has two different cards — which is exactly this
/// box.</param>
/// <param name="Name">The marketing name, e.g. <c>NVIDIA GeForce RTX 3080</c>.</param>
/// <param name="Uuid">The stable <c>GPU-…</c> uuid. This is what we actually pin
/// with, because it cannot be reordered by anything.</param>
public sealed record GpuDevice(int Index, string Name, string Uuid);

/// <summary>The resolved device.</summary>
/// <param name="Device">The card that was chosen.</param>
/// <param name="VisibleDevices">How the card is named for CUDA-based tools
/// (<c>CUDA_VISIBLE_DEVICES</c>): the uuid when we have one, the index otherwise. The
/// ONNX path maps <see cref="GpuDevice.Name" /> to a DirectML device instead
/// (<see cref="GpuSelector.DirectMlDeviceFor" />).</param>
/// <param name="Reason">One line fit for a log.</param>
public sealed record GpuChoice(GpuDevice Device, string VisibleDevices, string Reason);

/// <summary>Raised when no GPU can be used — no card matched, none was found, or
/// the only match is the owner's gaming card and nobody said it was allowed.</summary>
public sealed class GpuSelectionException : Exception
{
    /// <summary>Creates the exception with a message fit to print to the operator.</summary>
    /// <param name="message">Why no GPU was selected.</param>
    public GpuSelectionException(string message) : base(message) { }
}

/// <summary>
///     Picks the GPU stage B runs on <b>by NAME</b>, and refuses the owner's gaming
///     card unless it is explicitly allowed.
///     <para>
///         <b>By name, never by index, and that is the whole point of this type.</b>
///         This box has two cards — an RTX 3080 (compute) and an RTX 5070 Ti (the
///         owner's gaming GPU) — and there are three different numberings in play:
///         <c>nvidia-smi</c> is PCI-bus ordered (3080 = 0), CUDA defaults to
///         <c>FASTEST_FIRST</c>, and torch reports the 5070 Ti as <c>cuda:0</c>.
///         Any pipeline that hard-codes "device 1" is one driver update away from
///         quietly taking over the machine the owner is playing on. So the name is
///         resolved to a <c>GPU-…</c> uuid and THAT is what pins the runtime.
///     </para>
///     <para>
///         <b>The ONNX runtime is the DirectML build, so the chosen NAME is mapped
///         to a DXGI adapter</b> (<see cref="DirectMlDeviceFor" />) and passed as the
///         separator's <c>deviceId</c>. DXGI adds a fourth numbering — the display
///         adapter is 0 — which is one more reason the name is the only key.
///     </para>
/// </summary>
public static class GpuSelector
{
    /// <summary>The card stage B runs on unless told otherwise: the compute GPU.</summary>
    public const string DefaultWantedName = "3080";

    /// <summary>Name fragments that identify a card this pipeline must not take.
    /// The 5070 Ti is the owner's gaming GPU; a separation pass that grabs it turns
    /// a game into a slideshow with no message anywhere saying why.</summary>
    public static IReadOnlyList<string> GamingGpuMarkers { get; } = ["5070"];

    /// <summary>True when the name is one of <see cref="GamingGpuMarkers" />.</summary>
    /// <param name="name">A GPU marketing name.</param>
    public static bool IsGamingGpu(string name)
        => GamingGpuMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Parses <c>nvidia-smi --query-gpu=index,name,uuid --format=csv,noheader</c>
    ///     output. Tolerates the header line, so the caller may pass <c>csv</c> too.
    /// </summary>
    /// <param name="csv">The raw stdout.</param>
    public static IReadOnlyList<GpuDevice> ParseNvidiaSmi(string? csv)
    {
        var outp = new List<GpuDevice>();
        foreach (var raw in (csv ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue; // the header
            outp.Add(new GpuDevice(index, parts[1].Trim(), parts.Length > 2 ? parts[2].Trim() : string.Empty));
        }

        return outp;
    }

    /// <summary>
    ///     Resolves <paramref name="wantedName" /> against the cards present.
    /// </summary>
    /// <param name="devices">What <c>nvidia-smi</c> reported.</param>
    /// <param name="wantedName">A case-insensitive fragment of the card's name, e.g.
    /// <c>3080</c>. Lowest index wins when more than one matches, so the answer does
    /// not depend on enumeration order.</param>
    /// <param name="allowGamingGpu">Permission to take a card in
    /// <see cref="GamingGpuMarkers" />. Off by default and it must stay off by
    /// default.</param>
    /// <exception cref="GpuSelectionException">No card, no match, or a refused
    /// gaming card.</exception>
    public static GpuChoice Select(IReadOnlyList<GpuDevice> devices, string wantedName, bool allowGamingGpu)
    {
        if (devices.Count == 0)
        {
            throw new GpuSelectionException(
                "no NVIDIA GPU was reported by nvidia-smi — run the stage with --cpu, or fix the driver.");
        }

        var listing = string.Join(", ", devices.Select(d => $"{d.Index}:{d.Name}"));
        var want = (wantedName ?? string.Empty).Trim();
        if (want.Length == 0)
            throw new GpuSelectionException($"--gpu-name is empty; name the card to use. Present: {listing}.");

        var matches = devices
            .Where(d => d.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Index)
            .ToArray();

        if (matches.Length == 0)
        {
            throw new GpuSelectionException(
                $"no GPU whose name contains '{want}'. Present: {listing}. "
                + "Pass --gpu-name with a fragment of one of those, or --cpu.");
        }

        var chosen = matches[0];
        if (IsGamingGpu(chosen.Name) && !allowGamingGpu)
        {
            throw new GpuSelectionException(
                $"refusing to run on '{chosen.Name}' — it is the owner's GAMING GPU. "
                + "Pass --allow-gaming-gpu if the box is genuinely free, or name the compute card "
                + $"(--gpu-name {DefaultWantedName}). Present: {listing}.");
        }

        var visible = chosen.Uuid.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase)
            ? chosen.Uuid
            : chosen.Index.ToString(CultureInfo.InvariantCulture);

        var how = visible == chosen.Uuid
            ? "by uuid (immune to CUDA_DEVICE_ORDER)"
            : "by nvidia-smi index (no uuid reported — CUDA_DEVICE_ORDER=PCI_BUS_ID is set to match)";
        return new GpuChoice(chosen, visible,
            $"GPU: '{chosen.Name}' (nvidia-smi {chosen.Index}), pinned {how}.");
    }

    /// <summary>Runs <c>nvidia-smi</c> and parses it. Returns an empty list when the
    /// tool is absent or fails — the caller turns that into a refusal or a CPU
    /// fallback, which is its decision and not this one's.</summary>
    /// <param name="log">Receives one line if the probe could not run.</param>
    public static IReadOnlyList<GpuDevice> Probe(Action<string>? log = null)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi")
            {
                ArgumentList = { "--query-gpu=index,name,uuid", "--format=csv,noheader" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20_000);
            return ParseNvidiaSmi(text);
        }
        catch (Exception ex)
        {
            log?.Invoke($"nvidia-smi could not be run ({ex.GetType().Name}: {ex.Message}).");
            return [];
        }
    }

    /// <summary>
    ///     The DirectML device id (DXGI adapter index) of the card named
    ///     <paramref name="cardName" />, or null unless EXACTLY one adapter carries that
    ///     name — two identical cards, or none, refuse rather than guess, because DXGI
    ///     adapter 0 is the display card.
    /// </summary>
    /// <param name="cardName">The chosen card's name as <c>nvidia-smi</c> reports it.</param>
    /// <param name="dxgiAdapters">The DXGI adapters, as (device id, description).</param>
    public static int? DirectMlDeviceFor(string cardName, IReadOnlyList<(int DeviceId, string Name)> dxgiAdapters)
    {
        var matches = dxgiAdapters
            .Where(a => string.Equals(a.Name.Trim(), cardName.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0].DeviceId : null;
    }
}
