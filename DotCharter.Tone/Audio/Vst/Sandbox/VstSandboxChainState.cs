using System.Text.Json;

namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// The desired plugin state of the sandbox child: the ordered guitar chain
/// plus the single instrument slot, with optional captured plugin state
/// blobs (base64, as returned by the child's <c>getState</c>). Replayed
/// verbatim after a sandbox crash/restart so the user's rig comes back
/// without interaction. JSON round-trippable so it could be persisted.
/// </summary>
public sealed class VstSandboxChainState
{
    public List<VstChainEntry> Chain { get; set; } = [];
    public string? InstrumentPath { get; set; }
    /// <summary>Captured plugin states keyed by slot id ("instrument" for
    /// the VSTi), base64-encoded.</summary>
    public Dictionary<string, string> PluginStates { get; set; } = [];

    public bool IsEmpty => Chain.Count == 0 && string.IsNullOrEmpty(InstrumentPath);

    public VstSandboxChainState Clone()
    {
        return new VstSandboxChainState
        {
            Chain = Chain.Select(e => new VstChainEntry
            {
                Id = e.Id,
                PluginPath = e.PluginPath,
                Bypass = e.Bypass,
                Player = e.Player,
            }).ToList(),
            InstrumentPath = InstrumentPath,
            PluginStates = new Dictionary<string, string>(PluginStates),
        };
    }

    public string ToJson()
        => JsonSerializer.Serialize(this, VstSandboxProtocol.Json);

    public static VstSandboxChainState? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<VstSandboxChainState>(json, VstSandboxProtocol.Json); }
        catch (JsonException) { return null; }
    }
}
