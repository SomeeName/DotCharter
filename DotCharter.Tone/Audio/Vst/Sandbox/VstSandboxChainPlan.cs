namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// The per-slot decisions the sandboxed guitar chain makes, kept pure so they
/// are tested without a child process. A slot belongs to a SEAT: the
/// child runs it only on that player's input, so the seat travels with the
/// <c>load</c> and a slot moved to another player is reloaded.
/// </summary>
internal static class VstSandboxChainPlan
{
    /// <summary>The <c>load</c> request for <paramref name="entry"/>, carrying its seat.</summary>
    public static VstSandboxRequest LoadRequest(VstChainEntry entry) => new()
    {
        Op = "load",
        Slot = entry.Id.ToString(),
        Path = entry.PluginPath,
        Bypass = entry.Bypass,
        Seat = entry.Player,
    };

    /// <summary>
    /// Whether a loaded slot must be unloaded before the chain is applied: it
    /// is absent from the desired chain, its plugin differs, or its seat differs.
    /// </summary>
    public static bool MustUnload(VstChainEntry? desired, string livePath, int livePlayer) =>
        desired == null
        || !string.Equals(desired.PluginPath, livePath, StringComparison.OrdinalIgnoreCase)
        || desired.Player != livePlayer;
}
