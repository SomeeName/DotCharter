namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// Moves plugin state (the child's <c>getState</c>/<c>setState</c>, base64) between the
/// sandbox child and <see cref="VstSandboxChainState.PluginStates"/>, so a restart
/// brings back the user's presets and knobs and not just the plugin paths.
/// Every call is bounded through <see cref="VstSandboxCalls"/>; nothing here throws.
/// </summary>
internal static class VstSandboxStateSync
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Asks the child for the state of each slot. Returns only the slots that answered
    /// with a blob; a slot that failed is absent, so the caller keeps its older blob.
    /// </summary>
    public static async Task<Dictionary<string, string>> CaptureAsync(
        IVstSandboxChannel host, IEnumerable<string> slotIds, Action<string, Exception> onFailure)
    {
        var captured = new Dictionary<string, string>();
        foreach (var slot in slotIds)
        {
            var reply = await VstSandboxCalls.TrySendAsync(host,
                new VstSandboxRequest { Op = "getState", Slot = slot }, StateTimeout, ex => onFailure(slot, ex))
                .ConfigureAwait(false);
            if (reply is { Ok: true, State: { Length: > 0 } state }) captured[slot] = state;
            else if (reply != null) onFailure(slot, new InvalidOperationException(reply.Error ?? "no state returned"));
        }
        return captured;
    }

    /// <summary>
    /// Sends <c>setState</c> for every live slot that has a captured blob. Returns how many
    /// slots the child accepted.
    /// </summary>
    public static async Task<int> ReplayAsync(
        IVstSandboxChannel host, IReadOnlyDictionary<string, string> states,
        IEnumerable<string> liveSlotIds, Action<string, Exception> onFailure)
    {
        var restored = 0;
        foreach (var slot in liveSlotIds)
        {
            if (!states.TryGetValue(slot, out var state) || string.IsNullOrEmpty(state)) continue;
            var reply = await VstSandboxCalls.TrySendAsync(host,
                new VstSandboxRequest { Op = "setState", Slot = slot, State = state }, StateTimeout, ex => onFailure(slot, ex))
                .ConfigureAwait(false);
            if (reply is { Ok: true }) restored++;
            else if (reply != null) onFailure(slot, new InvalidOperationException(reply.Error ?? "setState rejected"));
        }
        return restored;
    }
}
