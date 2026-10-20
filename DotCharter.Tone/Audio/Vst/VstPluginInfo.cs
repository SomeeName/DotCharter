namespace DotCharter.Tone.Audio.Vst;

/// <summary>
/// Lightweight description of a discovered VST3 plugin. The new audio engine
/// only enumerates VST3 bundles — VST2 is not supported.
/// </summary>
public sealed class VstPluginInfo
{
    public string Name { get; init; } = string.Empty;
    public string PluginPath { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;
    public string? Version { get; init; }
    public bool IsInstrument { get; init; }
    public bool IsEffect { get; init; }
}
